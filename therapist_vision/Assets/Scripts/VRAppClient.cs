using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

// Hosts the TCP server that the MusicTherapy VR app connects to. This used to be the TCP
// *client* side (dialing out to the VR app's server on every button click), but the VR app is a
// standalone headset on the same LAN and turned out to be an unreliable place to run a server
// (harder to reach a stable/known IP, background networking restrictions, etc.), so the roles
// were swapped: this machine (typically the therapist's laptop) now listens, and the headset
// dials in.
//
// NOTE on the class name: this class is still called "VRAppClient" (and lives in the same
// file/asset) even though it is now the TCP *server* side, purely to avoid re-linking this
// component in SampleScene.unity's Inspector (TherapistUIController.vrAppClient still points at
// it). See MusicTherapy's VRAppServer.cs (now the TCP *client*) for the matching half of this
// swap.
//
// Unlike the old short-lived "dial, send one message, read one response, close" client, the VR
// app only ever reacts to messages it's sent (it never has anything to say on its own), so the
// connection from it is held open indefinitely: this class accepts that one connection and
// keeps it around, and SendSessionInfo/RequestSessionCsv reuse it whenever a button is clicked.
public class VRAppClient : MonoBehaviour
{
    // Wire protocol (must match VRAppServer.cs on the MusicTherapy side exactly):
    //   [1 byte]  MessageType
    //   [4 bytes] payload length, big-endian int32
    //   [N bytes] payload
    private enum MessageType : byte
    {
        SessionStart = 1,
        Ack = 2,
        ExportRequest = 3,
        ExportResponse = 4,
    }

    // --- Discovery protocol (UDP, separate from the TCP session port below) ---
    // The VR app broadcasts DiscoveryRequestMagic on DiscoveryPort; this side listens there and
    // unicasts back "DiscoveryResponsePrefix<tcpPort>" to whoever asked, so the VR app can read
    // this machine's IP straight off the reply packet instead of anyone having to type it in.
    // Both magic strings and the port must match VRAppServer.cs on the MusicTherapy side exactly.
    private const string DiscoveryRequestMagic = "MUSICTHERAPY_DISCOVERY_REQUEST";
    private const string DiscoveryResponsePrefix = "MUSICTHERAPY_DISCOVERY_RESPONSE:";
    private const int DiscoveryPort = 8081;

    [Header("VR App Connection (Server)")]
    // 0.0.0.0 (all interfaces) — the VR headset is a separate device on the LAN, not a client
    // running on this same machine.
    [SerializeField] private int listenPort = 8080;
    [SerializeField] private int socketTimeoutMs = 10000;

    private TcpListener listener;
    private Thread acceptThread;
    private UdpClient discoveryUdpClient;
    private Thread discoveryThread;
    private volatile bool stopRequested;

    // Guards currentClient/currentStream against concurrent access from AcceptLoop (which may
    // swap in a new connection if the VR app reconnects) and from a SendSessionInfo/
    // RequestSessionCsv call in flight. Held for the duration of one request/response exchange —
    // at most one such exchange happens at a time in practice, driven by therapist button clicks.
    private readonly object connectionLock = new object();
    private TcpClient currentClient;
    private NetworkStream currentStream;

    public bool IsVrAppConnected { get; private set; }
    public int ListenPort => listenPort;

    private class AsyncResult
    {
        public volatile bool Done;
        public bool Success;
        public string Result;
    }

    private void Start()
    {
        try
        {
            listener = new TcpListener(IPAddress.Any, listenPort);
            listener.Start();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[VRAppClient] Failed to start listening on port {listenPort}: {ex.Message}");
            return;
        }

        Debug.Log($"[VRAppClient] Listening for the VR app on {GetLocalIPAddress()}:{listenPort}");

        acceptThread = new Thread(AcceptLoop) { IsBackground = true };
        acceptThread.Start();

        try
        {
            discoveryUdpClient = new UdpClient(DiscoveryPort);
            discoveryThread = new Thread(DiscoveryLoop) { IsBackground = true };
            discoveryThread.Start();
            Debug.Log($"[VRAppClient] Answering discovery broadcasts on UDP port {DiscoveryPort}.");
        }
        catch (Exception ex)
        {
            // Not fatal: the VR app can still connect via the server_ip.txt manual override.
            Debug.LogError($"[VRAppClient] Failed to start discovery listener on port {DiscoveryPort}: {ex.Message}");
        }
    }

    // Answers "who's out there" broadcasts from the VR app so it never needs a configured IP.
    // The VR app learns this machine's address from the reply packet's source IP, so the
    // response body only needs to carry the TCP port.
    private void DiscoveryLoop()
    {
        while (!stopRequested)
        {
            var remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
            byte[] requestBytes;
            try
            {
                requestBytes = discoveryUdpClient.Receive(ref remoteEndPoint);
            }
            catch (Exception)
            {
                break; // socket closed during shutdown
            }

            if (Encoding.UTF8.GetString(requestBytes) != DiscoveryRequestMagic)
                continue;

            try
            {
                byte[] responseBytes = Encoding.UTF8.GetBytes($"{DiscoveryResponsePrefix}{listenPort}");
                discoveryUdpClient.Send(responseBytes, responseBytes.Length, remoteEndPoint);
                Debug.Log($"[VRAppClient] Answered discovery request from {remoteEndPoint.Address}.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VRAppClient] Failed to reply to discovery request: {ex.Message}");
            }
        }
    }

    private void AcceptLoop()
    {
        while (!stopRequested)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (Exception)
            {
                break; // listener was stopped
            }

            lock (connectionLock)
            {
                CloseCurrentConnectionLocked();
                currentClient = client;
                currentClient.SendTimeout = socketTimeoutMs;
                currentClient.ReceiveTimeout = socketTimeoutMs;
                currentStream = client.GetStream();
                IsVrAppConnected = true;
            }

            Debug.Log($"[VRAppClient] VR app connected from {client.Client.RemoteEndPoint}");
        }
    }

    // Caller must hold connectionLock.
    private void CloseCurrentConnectionLocked()
    {
        try { currentStream?.Dispose(); } catch { /* ignore */ }
        try { currentClient?.Close(); } catch { /* ignore */ }
        currentStream = null;
        currentClient = null;
        IsVrAppConnected = false;
    }

    public void SendSessionInfo(SessionInfo info, Action<bool, string> onComplete)
    {
        StartCoroutine(SendSessionInfoRoutine(info, onComplete));
    }

    private IEnumerator SendSessionInfoRoutine(SessionInfo info, Action<bool, string> onComplete)
    {
        byte[] payload = Encoding.UTF8.GetBytes(JsonUtility.ToJson(info));
        var state = new AsyncResult();

        var thread = new Thread(() =>
        {
            lock (connectionLock)
            {
                if (currentStream == null)
                {
                    state.Success = false;
                    state.Result = "VR app is not connected.";
                    state.Done = true;
                    return;
                }

                try
                {
                    WriteMessage(currentStream, MessageType.SessionStart, payload);

                    var responseType = (MessageType)ReadByte(currentStream);
                    byte[] responsePayload = ReadFramedPayload(currentStream);

                    if (responseType != MessageType.Ack)
                        throw new IOException($"Unexpected response type from VR app: {responseType}");

                    state.Success = true;
                    state.Result = Encoding.UTF8.GetString(responsePayload);
                }
                catch (Exception ex)
                {
                    CloseCurrentConnectionLocked(); // connection is presumed broken
                    state.Success = false;
                    state.Result = ex.Message;
                }
                finally
                {
                    state.Done = true;
                }
            }
        });
        thread.IsBackground = true;
        thread.Start();

        while (!state.Done)
        {
            yield return null;
        }

        onComplete?.Invoke(state.Success, state.Result);
    }

    public void RequestSessionCsv(string saveDirectory, Action<bool, string> onComplete)
    {
        StartCoroutine(RequestSessionCsvRoutine(saveDirectory, onComplete));
    }

    private IEnumerator RequestSessionCsvRoutine(string saveDirectory, Action<bool, string> onComplete)
    {
        var state = new AsyncResult();

        var thread = new Thread(() =>
        {
            lock (connectionLock)
            {
                if (currentStream == null)
                {
                    state.Success = false;
                    state.Result = "VR app is not connected.";
                    state.Done = true;
                    return;
                }

                try
                {
                    WriteMessage(currentStream, MessageType.ExportRequest, Array.Empty<byte>());

                    var responseType = (MessageType)ReadByte(currentStream);
                    byte[] responsePayload = ReadFramedPayload(currentStream);

                    if (responseType != MessageType.ExportResponse)
                        throw new IOException($"Unexpected response type from VR app: {responseType}");

                    state.Success = responsePayload.Length > 0 && responsePayload[0] == 1;
                    if (!state.Success)
                    {
                        state.Result = Encoding.UTF8.GetString(responsePayload, 1, responsePayload.Length - 1);
                    }
                    else
                    {
                        int offset = 1;
                        ushort fileNameLength = (ushort)IPAddress.NetworkToHostOrder(
                            BitConverter.ToInt16(responsePayload, offset));
                        offset += 2;
                        string fileName = Encoding.UTF8.GetString(responsePayload, offset, fileNameLength);
                        offset += fileNameLength;

                        // The filename comes straight off the wire — strip any path components so a
                        // malicious or misbehaving peer can't write outside saveDirectory (e.g. "../../evil.exe").
                        fileName = Path.GetFileName(fileName);
                        if (string.IsNullOrWhiteSpace(fileName))
                            fileName = $"session_{DateTime.Now:yyyyMMdd_HHmmss}.csv";

                        byte[] csvBytes = new byte[responsePayload.Length - offset];
                        Array.Copy(responsePayload, offset, csvBytes, 0, csvBytes.Length);

                        Directory.CreateDirectory(saveDirectory);
                        string filePath = Path.Combine(saveDirectory, fileName);
                        File.WriteAllBytes(filePath, csvBytes);
                        state.Result = filePath;
                    }
                }
                catch (Exception ex)
                {
                    CloseCurrentConnectionLocked(); // connection is presumed broken
                    state.Success = false;
                    state.Result = ex.Message;
                }
                finally
                {
                    state.Done = true;
                }
            }
        });
        thread.IsBackground = true;
        thread.Start();

        while (!state.Done)
        {
            yield return null;
        }

        onComplete?.Invoke(state.Success, state.Result);
    }

    // Display-only: asks the OS which local interface would be used to reach the outside world,
    // without actually sending any packets (UDP "connect" just resolves routing). Good enough to
    // show the therapist which IP to point the headset at; falls back to "unknown" if the
    // machine has no route at all (e.g. no network connection yet).
    public static string GetLocalIPAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            return ((IPEndPoint)socket.LocalEndPoint).Address.ToString();
        }
        catch
        {
            return "unknown - check your network settings";
        }
    }

    public string GetServerAddressDisplay() => $"{GetLocalIPAddress()}:{listenPort}";

    // --- Framing helpers (mirrored on the MusicTherapy client) ---

    private static void WriteMessage(NetworkStream stream, MessageType type, byte[] payload)
    {
        stream.WriteByte((byte)type);
        WriteInt32(stream, payload.Length);
        stream.Write(payload, 0, payload.Length);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        byte[] bytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(value));
        stream.Write(bytes, 0, bytes.Length);
    }

    private static byte ReadByte(Stream stream)
    {
        int b = stream.ReadByte();
        if (b < 0)
            throw new IOException("Connection closed while reading message type.");
        return (byte)b;
    }

    private static byte[] ReadExact(Stream stream, int count)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read <= 0)
                throw new IOException("Connection closed while reading message.");
            offset += read;
        }
        return buffer;
    }

    private static byte[] ReadFramedPayload(Stream stream)
    {
        int length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(ReadExact(stream, 4), 0));
        if (length < 0 || length > 64 * 1024 * 1024) // 64MB sanity cap
            throw new IOException($"Invalid payload length: {length}");
        return length == 0 ? Array.Empty<byte>() : ReadExact(stream, length);
    }

    private void OnDestroy()
    {
        Shutdown();
    }

    private void OnApplicationQuit()
    {
        Shutdown();
    }

    private void Shutdown()
    {
        stopRequested = true;
        try { listener?.Stop(); } catch { /* ignore — already stopped */ }
        listener = null;

        try { discoveryUdpClient?.Close(); } catch { /* ignore — already closed */ }
        discoveryUdpClient = null;

        lock (connectionLock)
        {
            CloseCurrentConnectionLocked();
        }
    }
}
