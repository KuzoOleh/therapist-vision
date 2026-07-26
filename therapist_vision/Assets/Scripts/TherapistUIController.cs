using System;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class TherapistUIController : MonoBehaviour
{
    [Header("Session Input")]
    [SerializeField] private TMP_InputField patientNameField;
    [SerializeField] private TMP_InputField therapistNameField;
    [SerializeField] private TMP_InputField sessionDateField;

    [Header("Controls")]
    [SerializeField] private Button startSessionButton;
    [SerializeField] private Button fetchResultsButton;
    [SerializeField] private TMP_Text statusText;

    [Header("Networking")]
    [SerializeField] private VRAppClient vrAppClient;

    [Header("Export")]
    [SerializeField] private string exportFolderName = "SessionExports";
    [SerializeField] private TMP_InputField saveFolderPathField;
    [SerializeField] private Button browseFolderButton;

    private void Awake()
    {
        startSessionButton.onClick.AddListener(OnStartSessionClicked);
        fetchResultsButton.onClick.AddListener(OnFetchResultsClicked);
        fetchResultsButton.interactable = false;

        if (browseFolderButton != null)
            browseFolderButton.onClick.AddListener(OnBrowseFolderClicked);

        if (sessionDateField != null && string.IsNullOrEmpty(sessionDateField.text))
            sessionDateField.text = DateTime.Now.ToString("yyyy-MM-dd");

        if (saveFolderPathField != null && string.IsNullOrEmpty(saveFolderPathField.text))
            saveFolderPathField.text = Path.Combine(Application.persistentDataPath, exportFolderName);
    }

    private enum StatusType { Info, Success, Error }

    [Header("Status Colors")]
    [SerializeField] private Color infoColor = Color.white;
    [SerializeField] private Color successColor = Color.green;
    [SerializeField] private Color errorColor = Color.red;

    private void OnStartSessionClicked()
    {
        string patientName = patientNameField.text.Trim();
        string therapistName = therapistNameField.text.Trim();
        string sessionDate = sessionDateField.text.Trim();

        if (string.IsNullOrEmpty(patientName) || string.IsNullOrEmpty(therapistName) || string.IsNullOrEmpty(sessionDate))
        {
            SetStatus("Enter patient name, therapist name, and date before starting.", StatusType.Error);
            return;
        }

        var info = new SessionInfo
        {
            patientName = patientName,
            therapistName = therapistName,
            sessionDate = sessionDate
        };

        SetStatus("Sending session info to VR app...", StatusType.Info);
        startSessionButton.interactable = false;

        vrAppClient.SendSessionInfo(info, (success, message) =>
        {
            startSessionButton.interactable = true;
            if (success)
            {
                SetStatus("Session started. Waiting for it to finish, then fetch results.", StatusType.Success);
                fetchResultsButton.interactable = true;
            }
            else
            {
                SetStatus($"Failed to reach VR app: {message}", StatusType.Error);
            }
        });
    }

    private void OnFetchResultsClicked()
    {
        SetStatus("Fetching session results...", StatusType.Info);
        fetchResultsButton.interactable = false;

        string exportPath = saveFolderPathField != null && !string.IsNullOrWhiteSpace(saveFolderPathField.text)
            ? saveFolderPathField.text.Trim()
            : Path.Combine(Application.persistentDataPath, exportFolderName);

        vrAppClient.RequestSessionCsv(exportPath, (success, result) =>
        {
            fetchResultsButton.interactable = true;
            SetStatus(success ? $"Saved results to: {result}" : $"Failed to fetch results: {result}",
                success ? StatusType.Success : StatusType.Error);
        });
    }

    private void OnBrowseFolderClicked()
    {
#if UNITY_EDITOR
        string startingPath = saveFolderPathField != null && !string.IsNullOrWhiteSpace(saveFolderPathField.text)
            ? saveFolderPathField.text.Trim()
            : Application.persistentDataPath;

        string selected = UnityEditor.EditorUtility.OpenFolderPanel("Choose Save Folder", startingPath, "");
        if (!string.IsNullOrEmpty(selected) && saveFolderPathField != null)
        {
            saveFolderPathField.text = selected;
        }
#else
        SetStatus("Folder browsing is only available in the Unity Editor. Type or paste a folder path above instead.", StatusType.Info);
#endif
    }

    private void SetStatus(string message, StatusType type = StatusType.Info)
    {
        if (statusText != null)
        {
            statusText.text = message;
            statusText.color = type switch
            {
                StatusType.Success => successColor,
                StatusType.Error => errorColor,
                _ => infoColor
            };
        }

        Debug.Log($"[TherapistUIController] {message}");
    }
}
