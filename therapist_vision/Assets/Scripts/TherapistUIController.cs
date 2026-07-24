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

    private void Awake()
    {
        startSessionButton.onClick.AddListener(OnStartSessionClicked);
        fetchResultsButton.onClick.AddListener(OnFetchResultsClicked);
        fetchResultsButton.interactable = false;

        if (sessionDateField != null && string.IsNullOrEmpty(sessionDateField.text))
            sessionDateField.text = DateTime.Now.ToString("yyyy-MM-dd");
    }

    private void OnStartSessionClicked()
    {
        string patientName = patientNameField.text.Trim();
        string therapistName = therapistNameField.text.Trim();
        string sessionDate = sessionDateField.text.Trim();

        if (string.IsNullOrEmpty(patientName) || string.IsNullOrEmpty(therapistName) || string.IsNullOrEmpty(sessionDate))
        {
            SetStatus("Enter patient name, therapist name, and date before starting.");
            return;
        }

        var info = new SessionInfo
        {
            patientName = patientName,
            therapistName = therapistName,
            sessionDate = sessionDate
        };

        SetStatus("Sending session info to VR app...");
        startSessionButton.interactable = false;

        vrAppClient.SendSessionInfo(info, (success, message) =>
        {
            startSessionButton.interactable = true;
            if (success)
            {
                SetStatus("Session started. Waiting for it to finish, then fetch results.");
                fetchResultsButton.interactable = true;
            }
            else
            {
                SetStatus($"Failed to reach VR app: {message}");
            }
        });
    }

    private void OnFetchResultsClicked()
    {
        SetStatus("Fetching session results...");
        fetchResultsButton.interactable = false;

        string exportPath = Path.Combine(Application.persistentDataPath, exportFolderName);
        vrAppClient.RequestSessionCsv(exportPath, (success, result) =>
        {
            fetchResultsButton.interactable = true;
            SetStatus(success ? $"Saved results to: {result}" : $"Failed to fetch results: {result}");
        });
    }

    private void SetStatus(string message)
    {
        if (statusText != null)
            statusText.text = message;

        Debug.Log($"[TherapistUIController] {message}");
    }
}
