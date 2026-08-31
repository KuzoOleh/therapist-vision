using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

// Talks to the MusicTherapy VR app's VRAppServer over a raw TCP socket. Switched from
// UnityWebRequest/HTTP to sockets to match the VR app's server-side switch away from
// HttpListener, whose cross-platform support under Unity's Mono/IL2CPP backends is
// inconsistent (the two Linux builds couldn't see each other over HTTP).
public class VRAppClient : MonoBehaviour
{
    // Wire protocol (must match VRAppServer.cs on the MusicTherapy side exactly):
    //   [1 byte]  MessageType
    //   [4 bytes] payload length, big-endian int32
    //   [N bytes] payload
    // One request message out, one response message in, then the connection closes.
    private enum MessageType : byte
    {
        SessionStart = 1,
        Ack = 2,
        ExportRequest = 3,
        ExportResponse = 4,
    }

    [Header("VR App Connection")]
    [SerializeField] private string vrAppIpAddress = "192.168.1.100";
    [SerializeField] private int vrAppPort = 8080;
    [SerializeField] private int connectTimeoutMs = 5000;
    [SerializeField] private int socketTimeoutMs = 10000;

    // Done is volatile because it's written on the background thread and polled from the
    // main-thread coroutine below — a plain bool has no visibility guarantee across threads
    // and could keep the coroutine spinning even after the thread has finished. Success/Result
    // are safe to read once Done observes true: they're always written before Done is set,
    // and the volatile write/read pair acts as the release/acquire fence that publishes them.
    private class AsyncResult
    {
        public volatile bool Done;
        public bool Success;
        public string Result;
    }

    public void SendSessionInfo(SessionInfo info, Action<bool, string> onComplete)
    {
        StartCoroutine(SendSessionInfoRoutine(info, onComplete));
    }

    private IEnumerator SendSessionInfoRoutine(SessionInfo info, Action<bool, string> onComplete)
    {
        byte[] payload = Encoding.UTF8.GetBytes(JsonUtility.ToJson(info));
        var state = new AsyncResult();

        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                using TcpClient client = ConnectWithTimeout();
                client.ReceiveTimeout = socketTimeoutMs;
                client.SendTimeout = socketTimeoutMs;
                using NetworkStream stream = client.GetStream();

                WriteMessage(stream, MessageType.SessionStart, payload);

                var responseType = (MessageType)ReadByte(stream);
                byte[] responsePayload = ReadFramedPayload(stream);

                if (responseType != MessageType.Ack)
                    throw new IOException($"Unexpected response type from VR app: {responseType}");

                state.Success = true;
                state.Result = Encoding.UTF8.GetString(responsePayload);
            }
            catch (Exception ex)
            {
                state.Success = false;
                state.Result = ex.Message;
            }
            finally
            {
                state.Done = true;
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

        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                using TcpClient client = ConnectWithTimeout();
                client.ReceiveTimeout = socketTimeoutMs;
                client.SendTimeout = socketTimeoutMs;
                using NetworkStream stream = client.GetStream();

                WriteMessage(stream, MessageType.ExportRequest, Array.Empty<byte>());

                var responseType = (MessageType)ReadByte(stream);
                byte[] responsePayload = ReadFramedPayload(stream);

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
                state.Success = false;
                state.Result = ex.Message;
            }
            finally
            {
                state.Done = true;
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

    private TcpClient ConnectWithTimeout()
    {
        var client = new TcpClient();
        IAsyncResult connectResult = client.BeginConnect(vrAppIpAddress, vrAppPort, null, null);
        bool connected = connectResult.AsyncWaitHandle.WaitOne(connectTimeoutMs);
        if (!connected)
        {
            // Still call EndConnect to properly release the pending async connect instead
            // of just Close()-ing and abandoning it — otherwise the in-flight attempt keeps
            // running against the OS/thread pool until it resolves on its own.
            try { client.EndConnect(connectResult); } catch { /* expected — we're timing out */ }
            client.Close();
            throw new SocketException((int)SocketError.TimedOut);
        }
        client.EndConnect(connectResult);
        return client;
    }

    // --- Framing helpers (mirrored on the MusicTherapy server) ---

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
}
