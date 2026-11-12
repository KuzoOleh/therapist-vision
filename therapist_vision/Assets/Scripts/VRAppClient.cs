using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using MusicTherapy.Net;
using UnityEngine;

// Unity side of the therapist's connection to the headset. The protocol (listening, discovery,
// handshake, heartbeats, file transfer) lives in SessionServer / MusicTherapyProtocol.cs; this
// component starts it, runs the blocking requests on worker threads and reports back on the
// main thread.
//
// NOTE on the class name: still "VRAppClient" (although this is the TCP *server*) so the
// reference in SampleScene.unity / TherapistUIController keeps working.
public class VRAppClient : MonoBehaviour
{
    [Header("VR App Connection (Server)")]
    [SerializeField] private int listenPort = Protocol.DefaultTcpPort;

    private SessionServer server;

    public bool IsVrAppConnected => server != null && server.IsConnected;
    public string ConnectedDeviceName => server?.ConnectedDevice;
    public int ListenPort => listenPort;
    public bool IsBusy { get; private set; }

    // Problems the therapist must know about (port taken, discovery unavailable), or null.
    public string NetworkError
    {
        get
        {
            if (server == null) return null;
            if (!string.IsNullOrEmpty(server.ListenerError)) return server.ListenerError;
            return string.IsNullOrEmpty(server.DiscoveryError) ? null : server.DiscoveryError;
        }
    }

    private sealed class UnityJson : IJsonSerializer
    {
        public string ToJson(object value) => JsonUtility.ToJson(value);
        public T FromJson<T>(string json) => JsonUtility.FromJson<T>(json);
    }

    private void Awake()
    {
        server = new SessionServer(listenPort, Protocol.DiscoveryPort, new UnityJson(), message => Debug.Log("[VRAppClient] " + message))
        {
            ServerName = SystemInfo.deviceName,
        };
        server.Start();
        Debug.Log($"[VRAppClient] Waiting for the VR headset on {GetServerAddressDisplay()}");
    }

    // onComplete(success, message): message is "ok", "queued" (headset asleep — the session
    // starts when it wakes up) or the error.
    public void SendSessionInfo(SessionInfo info, Action<bool, string> onComplete)
    {
        string json = JsonUtility.ToJson(info);
        StartCoroutine(RunRequest(() => server.SendSessionStart(json), result => onComplete?.Invoke(result.Success, result.Message), null));
    }

    // onProgress(fraction 0..1, text) on the main thread while files arrive;
    // onComplete(success, message, saved file paths).
    public void RequestSessionFiles(string saveDirectory, Action<float, string> onProgress, Action<bool, string, List<string>> onComplete)
    {
        var progress = new ProgressBox();
        StartCoroutine(RunRequest(
            () => server.FetchFiles(saveDirectory, (got, total, file) => progress.Set(got, total, file)),
            result => onComplete?.Invoke(result.Success, result.Message, result.SavedFiles),
            () =>
            {
                if (progress.TryRead(out long got, out long total, out string file))
                {
                    float fraction = total > 0 ? (float)got / total : 0f;
                    onProgress?.Invoke(fraction, $"{file}: {SessionServer.FormatBytes(got)} / {SessionServer.FormatBytes(total)}");
                }
            }));
    }

    private IEnumerator RunRequest(Func<SessionServer.Result> work, Action<SessionServer.Result> done, Action eachFrame)
    {
        IsBusy = true;
        Task<SessionServer.Result> task = Task.Run(work);
        while (!task.IsCompleted)
        {
            eachFrame?.Invoke();
            yield return null;
        }
        eachFrame?.Invoke();
        IsBusy = false;

        SessionServer.Result result = task.IsFaulted
            ? new SessionServer.Result { Success = false, Message = task.Exception?.GetBaseException().Message }
            : task.Result;
        done(result);
    }

    // Progress values written by the network thread, read once per frame.
    private sealed class ProgressBox
    {
        private readonly object gate = new object();
        private long got, total;
        private string file;
        private bool changed;

        public void Set(long got, long total, string file)
        {
            lock (gate) { this.got = got; this.total = total; this.file = file; changed = true; }
        }

        public bool TryRead(out long got, out long total, out string file)
        {
            lock (gate)
            {
                got = this.got; total = this.total; file = this.file;
                bool was = changed;
                changed = false;
                return was;
            }
        }
    }

    // Display-only: which local address the headset would reach (no packets are sent — a UDP
    // "connect" only resolves the route).
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

    private void OnDestroy() => server?.Dispose();

    private void OnApplicationQuit() => server?.Dispose();
}
