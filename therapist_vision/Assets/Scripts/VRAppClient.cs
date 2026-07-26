using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class VRAppClient : MonoBehaviour
{
    [Header("VR App Connection")]
    [SerializeField] private string vrAppIpAddress = "192.168.1.100";
    [SerializeField] private int vrAppPort = 8080;
    [SerializeField] private string startSessionEndpoint = "/session/start";
    [SerializeField] private string sessionResultEndpoint = "/session/export";

    private string BaseUrl => $"http://{vrAppIpAddress}:{vrAppPort}";

    public void SendSessionInfo(SessionInfo info, Action<bool, string> onComplete)
    {
        StartCoroutine(SendSessionInfoRoutine(info, onComplete));
    }

    private IEnumerator SendSessionInfoRoutine(SessionInfo info, Action<bool, string> onComplete)
    {
        string json = JsonUtility.ToJson(info);
        byte[] body = Encoding.UTF8.GetBytes(json);

        using var request = new UnityWebRequest($"{BaseUrl}{startSessionEndpoint}", UnityWebRequest.kHttpVerbPOST);
        request.uploadHandler = new UploadHandlerRaw(body);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        bool success = request.result == UnityWebRequest.Result.Success;
        onComplete?.Invoke(success, success ? request.downloadHandler.text : request.error);
    }

    public void RequestSessionCsv(string saveDirectory, Action<bool, string> onComplete)
    {
        StartCoroutine(RequestSessionCsvRoutine(saveDirectory, onComplete));
    }

    private IEnumerator RequestSessionCsvRoutine(string saveDirectory, Action<bool, string> onComplete)
    {
        using var request = UnityWebRequest.Get($"{BaseUrl}{sessionResultEndpoint}");
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            onComplete?.Invoke(false, request.error);
            yield break;
        }

        Directory.CreateDirectory(saveDirectory);
        string fileName = ExtractFileName(request) ?? $"session_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        string filePath = Path.Combine(saveDirectory, fileName);
        File.WriteAllBytes(filePath, request.downloadHandler.data);

        onComplete?.Invoke(true, filePath);
    }

    private string ExtractFileName(UnityWebRequest request)
    {
        string disposition = request.GetResponseHeader("Content-Disposition");
        if (string.IsNullOrEmpty(disposition))
            return null;

        const string marker = "filename=";
        int index = disposition.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;

        string name = disposition.Substring(index + marker.Length).Trim('"', ' ');
        return string.IsNullOrEmpty(name) ? null : name;
    }
}
