using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MusicTherapy.Net
{
    // Therapist-side network core (no UnityEngine — VRAppClient wraps it).
    //
    // Listens for the headset on TCP, answers discovery broadcasts on UDP, keeps exactly one
    // headset connection alive with heartbeats, and runs the two requests the therapist can
    // make: start a session and fetch the session's files. Request methods block, so call them
    // from a worker thread.
    public sealed class SessionServer : IDisposable
    {
        public sealed class Result
        {
            public bool Success;
            public string Message;
            public List<string> SavedFiles = new List<string>();
        }

        private readonly int tcpPort;
        private readonly int discoveryPort;
        private readonly IJsonSerializer json;
        private readonly Action<string> log;

        private TcpListener listener;
        private UdpClient discovery;
        private volatile bool stopping;

        private readonly object connectionLock = new object();
        private Connection current;
        private readonly object requestLock = new object();

        public string ServerName { get; set; } = Environment.MachineName;
        public int Port => tcpPort;

        // Set when the TCP port / discovery port can't be opened — shown to the therapist.
        public string ListenerError { get; private set; }
        public string DiscoveryError { get; private set; }

        public bool IsConnected
        {
            get
            {
                Connection c = current;
                return c != null && c.IsAlive;
            }
        }

        public string ConnectedDevice
        {
            get
            {
                Connection c = current;
                return c != null && c.IsAlive ? c.Device : null;
            }
        }

        public SessionServer(int tcpPort, int discoveryPort, IJsonSerializer json, Action<string> log)
        {
            this.tcpPort = tcpPort;
            this.discoveryPort = discoveryPort;
            this.json = json;
            this.log = log ?? (_ => { });
        }

        public void Start()
        {
            try
            {
                listener = new TcpListener(IPAddress.Any, tcpPort);
                listener.Start();
                StartThread(AcceptLoop, "MT accept");
                log($"Listening for the headset on TCP port {tcpPort}.");
            }
            catch (SocketException ex)
            {
                listener = null;
                ListenerError = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? $"Port {tcpPort} is already used by another program — close it (or another copy of TherapistVision) and restart."
                    : $"Can't listen on port {tcpPort}: {ex.Message}";
                log(ListenerError);
            }

            try
            {
                discovery = new UdpClient(discoveryPort);
                StartThread(DiscoveryLoop, "MT discovery");
            }
            catch (SocketException ex)
            {
                discovery = null;
                DiscoveryError = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? $"UDP port {discoveryPort} is busy — the headset can't find this computer automatically (server_ip.txt on the headset still works)."
                    : $"Automatic discovery unavailable: {ex.Message}";
                log(DiscoveryError);
            }

            StartThread(HeartbeatLoop, "MT heartbeat");
        }

        // ------------------------------------------------------------------ requests

        public Result SendSessionStart(string sessionInfoJson)
        {
            if (!Monitor.TryEnter(requestLock)) return Fail("Another request is still running.");
            try
            {
                Connection c = AliveConnection();
                if (c == null) return Fail("The VR headset is not connected.");

                c.DrainInbox();
                c.Send(MessageType.SessionStart, Framing.Utf8(sessionInfoJson));
                Frame reply = Expect(c, MessageType.Ack, 15000);
                var ack = json.FromJson<AckMessage>(reply.Text);
                if (ack != null && !string.IsNullOrEmpty(ack.error)) return Fail(ack.error);
                return Ok(ack?.status ?? "ok");
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
            finally
            {
                Monitor.Exit(requestLock);
            }
        }

        // progress(receivedBytes, totalBytes, currentFileName) is called from this thread.
        public Result FetchFiles(string saveDirectory, Action<long, long, string> progress = null)
        {
            if (!Monitor.TryEnter(requestLock)) return Fail("Another request is still running.");
            var partFiles = new List<string>();
            Connection c = null;
            try
            {
                c = AliveConnection();
                if (c == null) return Fail("The VR headset is not connected.");

                c.DrainInbox();
                c.Send(MessageType.ExportRequest, null);

                // The headset may need a moment (main thread, saving the WAV) before it answers.
                var manifest = json.FromJson<ExportManifest>(Expect(c, MessageType.ExportBegin, 30000).Text);
                if (manifest == null || !manifest.ready)
                    return Fail(manifest?.error ?? "The session isn't finished yet.");

                ExportFileInfo[] files = manifest.files ?? new ExportFileInfo[0];
                Directory.CreateDirectory(saveDirectory);
                long total = files.Sum(f => Math.Max(0, f.size));
                long received = 0;
                var finalPaths = new List<string>();

                for (int i = 0; i < files.Length; i++)
                {
                    string name = SafeFileName(files[i].name, i);
                    string finalPath = Path.Combine(saveDirectory, name);
                    string partPath = finalPath + ".part";
                    partFiles.Add(partPath);
                    finalPaths.Add(finalPath);
                    progress?.Invoke(received, total, name);

                    using (var output = new FileStream(partPath, FileMode.Create, FileAccess.Write))
                    {
                        long got = 0;
                        while (got < files[i].size)
                        {
                            Frame chunk = Expect(c, MessageType.FileChunk, 20000);
                            if (chunk.Payload.Length < 2 || Framing.ReadUInt16(chunk.Payload, 0) != i)
                                throw new IOException($"Unexpected data while receiving {name}.");
                            int length = chunk.Payload.Length - 2;
                            if (got + length > files[i].size)
                                throw new IOException($"The headset sent more data than announced for {name}.");

                            output.Write(chunk.Payload, 2, length);
                            got += length;
                            received += length;
                            progress?.Invoke(received, total, name);
                        }
                    }
                }

                Expect(c, MessageType.ExportEnd, 20000);

                for (int i = 0; i < finalPaths.Count; i++)
                {
                    if (File.Exists(finalPaths[i])) File.Delete(finalPaths[i]);
                    File.Move(partFiles[i], finalPaths[i]);
                }
                partFiles.Clear();

                c.Send(MessageType.ExportConfirm, Framing.Utf8(json.ToJson(new ExportConfirmation { saved = true })));
                var result = Ok($"Received {finalPaths.Count} file(s), {FormatBytes(total)}.");
                result.SavedFiles.AddRange(finalPaths);
                return result;
            }
            catch (Exception ex)
            {
                // Tell the headset it must NOT close — the results aren't safely on this computer.
                try
                {
                    if (c != null && !c.Closed)
                        c.Send(MessageType.ExportConfirm, Framing.Utf8(json.ToJson(new ExportConfirmation { saved = false, error = ex.Message })));
                }
                catch { /* connection already gone */ }
                return Fail(ex.Message);
            }
            finally
            {
                foreach (string part in partFiles)
                {
                    try { if (File.Exists(part)) File.Delete(part); } catch { /* ignore */ }
                }
                Monitor.Exit(requestLock);
            }
        }

        // ------------------------------------------------------------------ connection handling

        private void AcceptLoop()
        {
            while (!stopping)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch
                {
                    if (stopping) break;
                    Thread.Sleep(200);
                    continue;
                }
                StartThread(() => Handshake(client), "MT handshake");
            }
        }

        // Every connection must introduce itself with Hello within a few seconds. Port scanners
        // and other programs are dropped, and a second headset can't push out a live one.
        private void Handshake(TcpClient client)
        {
            string remote = Framing.DescribeEndpoint(client.Client.RemoteEndPoint);
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = Protocol.HandshakeTimeoutMs;
                client.SendTimeout = 10000;
                NetworkStream stream = client.GetStream();

                Frame first = Framing.ReadFrame(stream);
                HelloMessage hello = first.Type == MessageType.Hello ? json.FromJson<HelloMessage>(first.Text) : null;
                if (hello == null || hello.app != Protocol.AppId)
                {
                    log($"Ignored a connection from {remote} that isn't the MusicTherapy app.");
                    client.Close();
                    return;
                }

                if (hello.protocol != Protocol.Version)
                {
                    Reply(stream, false, $"Version mismatch: headset uses protocol {hello.protocol}, this computer {Protocol.Version}. Update both apps.");
                    log($"Rejected {hello.device} ({remote}): protocol {hello.protocol} != {Protocol.Version}.");
                    client.Close();
                    return;
                }

                Connection previous;
                Connection connection;
                lock (connectionLock)
                {
                    previous = current;
                    if (previous != null && previous.IsAlive && previous.Device != hello.device)
                    {
                        Reply(stream, false, $"Another headset ({previous.Device}) is already connected to this computer.");
                        log($"Rejected {hello.device} ({remote}): {previous.Device} is connected.");
                        client.Close();
                        return;
                    }

                    connection = new Connection(client, stream, hello.device, log);
                    current = connection;
                }

                // Same headset reconnecting (e.g. after a Wi-Fi drop): the old socket is stale.
                previous?.Close("replaced by a new connection");

                connection.Send(MessageType.HelloReply, Framing.Utf8(json.ToJson(new HelloReply { accepted = true, server = ServerName })));
                client.ReceiveTimeout = Protocol.ConnectionTimeoutMs;
                log($"Headset \"{hello.device}\" connected from {remote} (app {hello.appVersion}).");
                connection.ReadLoop(); // returns when the connection dies
            }
            catch (Exception ex)
            {
                log($"Connection from {remote} failed during handshake: {ex.Message}");
                try { client.Close(); } catch { /* ignore */ }
            }
        }

        private void Reply(NetworkStream stream, bool accepted, string reason)
        {
            try
            {
                Framing.WriteFrame(stream, MessageType.HelloReply,
                    Framing.Utf8(json.ToJson(new HelloReply { accepted = accepted, reason = reason, server = ServerName })));
            }
            catch { /* ignore */ }
        }

        private void HeartbeatLoop()
        {
            while (!stopping)
            {
                Thread.Sleep(Protocol.HeartbeatIntervalMs);
                Connection c = current;
                if (c == null || c.Closed) continue;

                if (!c.IsAlive)
                {
                    c.Close($"no sign of life for {Protocol.ConnectionTimeoutMs / 1000} s");
                    continue;
                }

                try
                {
                    c.Send(MessageType.Ping, null);
                }
                catch (Exception ex)
                {
                    c.Close("send failed: " + ex.Message);
                }
            }
        }

        private void DiscoveryLoop()
        {
            byte[] response = Encoding.UTF8.GetBytes(Protocol.DiscoveryResponsePrefix + tcpPort);
            while (!stopping)
            {
                var remote = new IPEndPoint(IPAddress.Any, 0);
                byte[] request;
                try
                {
                    request = discovery.Receive(ref remote);
                }
                catch
                {
                    if (stopping) break;
                    Thread.Sleep(200);
                    continue;
                }

                if (Encoding.UTF8.GetString(request) != Protocol.DiscoveryRequestMagic) continue;
                try
                {
                    discovery.Send(response, response.Length, remote);
                }
                catch (Exception ex)
                {
                    log($"Discovery reply to {remote.Address} failed: {ex.Message}");
                }
            }
        }

        private Connection AliveConnection()
        {
            Connection c = current;
            return c != null && c.IsAlive ? c : null;
        }

        private static Frame Expect(Connection c, MessageType type, int timeoutMs)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                int remaining = timeoutMs - (int)clock.ElapsedMilliseconds;
                if (remaining <= 0 || !c.Inbox.TryTake(out Frame frame, remaining))
                {
                    if (c.Closed) throw new IOException("The headset disconnected.");
                    throw new TimeoutException($"The headset didn't answer in time (waiting for {type}).");
                }
                if (frame.Type == type) return frame;
            }
        }

        private static string SafeFileName(string name, int index)
        {
            name = Path.GetFileName(name ?? "");
            foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(name) ? $"session_file_{index + 1}" : name;
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.0} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:0} KB";
            return $"{bytes} B";
        }

        private static Result Ok(string message) => new Result { Success = true, Message = message };
        private static Result Fail(string message) => new Result { Success = false, Message = message };

        private static void StartThread(ThreadStart body, string name)
        {
            new Thread(body) { IsBackground = true, Name = name }.Start();
        }

        public void Dispose()
        {
            stopping = true;
            try { listener?.Stop(); } catch { /* ignore */ }
            try { discovery?.Close(); } catch { /* ignore */ }
            current?.Close("shutting down");
        }

        // One headset connection: a reader that feeds frames into an inbox (pings only refresh
        // the "last seen" time), and a locked writer shared by the heartbeat and requests.
        private sealed class Connection
        {
            private readonly TcpClient client;
            private readonly NetworkStream stream;
            private readonly Action<string> log;
            private readonly object writeLock = new object();
            private long lastInboundTicks;
            private volatile bool closed;

            public readonly string Device;
            public readonly BlockingCollection<Frame> Inbox = new BlockingCollection<Frame>();

            public Connection(TcpClient client, NetworkStream stream, string device, Action<string> log)
            {
                this.client = client;
                this.stream = stream;
                this.log = log;
                Device = string.IsNullOrEmpty(device) ? "VR headset" : device;
                Touch();
            }

            public bool Closed => closed;

            public bool IsAlive
            {
                get
                {
                    if (closed) return false;
                    long elapsed = Stopwatch.GetTimestamp() - Interlocked.Read(ref lastInboundTicks);
                    return elapsed < Protocol.ConnectionTimeoutMs * (Stopwatch.Frequency / 1000);
                }
            }

            private void Touch() => Interlocked.Exchange(ref lastInboundTicks, Stopwatch.GetTimestamp());

            public void Send(MessageType type, byte[] payload)
            {
                if (closed) throw new IOException("The headset disconnected.");
                lock (writeLock)
                {
                    Framing.WriteFrame(stream, type, payload);
                }
            }

            public void ReadLoop()
            {
                try
                {
                    while (!closed)
                    {
                        Frame frame = Framing.ReadFrame(stream);
                        Touch();
                        if (frame.Type == MessageType.Ping) continue;
                        Inbox.Add(frame);
                    }
                }
                catch (Exception ex)
                {
                    Close(ex is IOException && ex.InnerException is SocketException se && se.SocketErrorCode == SocketError.TimedOut
                        ? $"no sign of life for {Protocol.ConnectionTimeoutMs / 1000} s"
                        : ex.Message);
                }
            }

            public void DrainInbox()
            {
                while (Inbox.TryTake(out _)) { }
            }

            public void Close(string reason)
            {
                if (closed) return;
                closed = true;
                try { Inbox.CompleteAdding(); } catch { /* ignore */ }
                try { stream.Dispose(); } catch { /* ignore */ }
                try { client.Close(); } catch { /* ignore */ }
                log($"Headset \"{Device}\" disconnected ({reason}).");
            }
        }
    }
}
