using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TherapistVision.Reports;
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
    [SerializeField] private Button exitButton;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private TMP_Text platformInfoText;

    [Header("Networking")]
    [SerializeField] private VRAppClient vrAppClient;

    [Header("Export")]
    [SerializeField] private string exportFolderName = "SessionExports";
    [SerializeField] private TMP_InputField saveFolderPathField;
    [SerializeField] private Button browseFolderButton;

    [Header("PDF Report")]
    [Tooltip("Builds a PDF report from any session CSV the therapist picks. Left empty, a button is created at runtime by cloning the Fetch Data button.")]
    [SerializeField] private Button reportFromFileButton;
    [Tooltip("Opens the last generated PDF report in the system PDF viewer. Left empty, a button is created at runtime by cloning the Fetch Data button.")]
    [SerializeField] private Button openReportButton;
    [Tooltip("Font embedded into the PDF, relative to StreamingAssets. Must contain Cyrillic glyphs.")]
    [SerializeField] private string reportFontPath = "Fonts/LiberationSans.ttf";
    [Tooltip("Open the PDF automatically as soon as it has been generated.")]
    [SerializeField] private bool openReportAutomatically = false;
    [Tooltip("Where the runtime-created report buttons go, in the Fetch Data button's parent space.")]
    [SerializeField] private Vector2 reportFromFileButtonPosition = new Vector2(-120f, -185f);
    [SerializeField] private Vector2 openReportButtonPosition = new Vector2(120f, -185f);
    [SerializeField] private Vector2 runtimeReportButtonSize = new Vector2(220f, 46f);

    private string lastReportPath;
    private bool reportInProgress;

    private void Awake()
    {
        startSessionButton.onClick.AddListener(OnStartSessionClicked);
        fetchResultsButton.onClick.AddListener(OnFetchResultsClicked);
        fetchResultsButton.interactable = false;

        if (exitButton != null)
            exitButton.onClick.AddListener(OnExitClicked);

        if (browseFolderButton != null)
            browseFolderButton.onClick.AddListener(OnBrowseFolderClicked);

        SetUpReportButtons();

        if (sessionDateField != null && string.IsNullOrEmpty(sessionDateField.text))
            sessionDateField.text = DateTime.Now.ToString("yyyy-MM-dd");

        if (saveFolderPathField != null && string.IsNullOrEmpty(saveFolderPathField.text))
            saveFolderPathField.text = Path.Combine(Application.persistentDataPath, exportFolderName);

        // Small on-screen readout so it's obvious which OS a given build is actually
        // running as during cross-platform testing with real headsets — no need to dig
        // through logs or remember which machine you're looking at.
        basePlatformInfo = $"Running on: {SystemInfo.operatingSystem} ({Application.platform})";
        Debug.Log($"[TherapistUIController] {basePlatformInfo}");
        RefreshPlatformInfoText();
    }

    // This app now hosts the TCP server the VR headset connects to (see VRAppClient.cs), so the
    // therapist needs to know which IP/port to point the headset at, and whether it's actually
    // connected. Polled here rather than event-driven because the connection state changes on a
    // background accept thread — cheap enough to just re-render every frame off a bool compare.
    private string basePlatformInfo;
    private bool lastKnownVrAppConnected;

    private void Update()
    {
        bool connected = vrAppClient != null && vrAppClient.IsVrAppConnected;
        if (connected != lastKnownVrAppConnected)
        {
            lastKnownVrAppConnected = connected;
            RefreshPlatformInfoText();
        }
    }

    private void RefreshPlatformInfoText()
    {
        if (platformInfoText == null)
            return;

        string serverAddress = vrAppClient != null ? vrAppClient.GetServerAddressDisplay() : "n/a";
        string connectionStatus = lastKnownVrAppConnected ? "VR headset: connected" : "VR headset: waiting for connection...";
        platformInfoText.text = $"{basePlatformInfo}\nServer listening on: {serverAddress}\n{connectionStatus}";
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

        string exportPath = GetExportFolder();

        vrAppClient.RequestSessionCsv(exportPath, (success, result) =>
        {
            fetchResultsButton.interactable = true;
            if (!success)
            {
                SetStatus($"Failed to fetch results: {result}", StatusType.Error);
                return;
            }

            SetStatus($"Saved results to: {result}\nBuilding PDF report...", StatusType.Info);
            StartCoroutine(GenerateReportRoutine(result));
        });
    }

    // ------------------------------------------------------------------ PDF report

    private void OnReportFromFileClicked()
    {
        if (reportInProgress) return;

        string exportFolder = GetExportFolder();
        CsvFilePicker.Result pick = CsvFilePicker.Pick(exportFolder, out string csvPath);

        if (pick == CsvFilePicker.Result.Cancelled)
            return;

        if (pick == CsvFilePicker.Result.Unavailable)
        {
            // No file dialog on this system (e.g. Linux without zenity/kdialog): use the newest
            // CSV in the export folder instead.
            csvPath = FindNewestCsv(exportFolder);
            if (csvPath == null)
            {
                SetStatus($"No file dialog available and no CSV files found in: {exportFolder}", StatusType.Error);
                return;
            }
        }

        SetStatus($"Building PDF report for {Path.GetFileName(csvPath)}...", StatusType.Info);
        StartCoroutine(GenerateReportRoutine(csvPath));
    }

    private void OnOpenReportClicked()
    {
        if (string.IsNullOrEmpty(lastReportPath) || !File.Exists(lastReportPath))
        {
            SetStatus("No report to open yet — fetch results or build a report from a CSV first.", StatusType.Error);
            return;
        }

        Application.OpenURL(new Uri(lastReportPath).AbsoluteUri);
    }

    // PDF generation is pure C# file work (no Unity API), so it runs on a worker thread to keep
    // the UI responsive; the coroutine just waits for it and reports back on the main thread.
    private IEnumerator GenerateReportRoutine(string csvPath)
    {
        reportInProgress = true;
        SetReportButtonsInteractable(false);

        string fontPath = Path.Combine(Application.streamingAssetsPath, reportFontPath);
        Task<string> task = Task.Run(() => SessionReportGenerator.GenerateFromCsv(csvPath, null, fontPath));

        while (!task.IsCompleted)
        {
            yield return null;
        }

        reportInProgress = false;

        if (task.IsFaulted || task.IsCanceled)
        {
            Exception error = task.Exception?.GetBaseException();
            Debug.LogError($"[TherapistUIController] PDF report failed for {csvPath}: {error}");
            SetStatus($"Results saved to: {csvPath}\nPDF report failed: {error?.Message ?? "cancelled"}", StatusType.Error);
            SetReportButtonsInteractable(true);
            yield break;
        }

        lastReportPath = task.Result;
        SetReportButtonsInteractable(true);
        SetStatus($"Results: {csvPath}\nReport: {lastReportPath}", StatusType.Success);

        if (openReportAutomatically)
            OnOpenReportClicked();
    }

    private void SetReportButtonsInteractable(bool interactable)
    {
        if (reportFromFileButton != null)
            reportFromFileButton.interactable = interactable;
        if (openReportButton != null)
            openReportButton.interactable = interactable && !string.IsNullOrEmpty(lastReportPath);
    }

    private void SetUpReportButtons()
    {
        if (reportFromFileButton == null)
            reportFromFileButton = CloneButton(fetchResultsButton, "ReportFromCsv", "Report from CSV...", reportFromFileButtonPosition);
        if (openReportButton == null)
            openReportButton = CloneButton(fetchResultsButton, "OpenReport", "Open report", openReportButtonPosition);

        if (reportFromFileButton != null)
            reportFromFileButton.onClick.AddListener(OnReportFromFileClicked);
        if (openReportButton != null)
            openReportButton.onClick.AddListener(OnOpenReportClicked);

        SetReportButtonsInteractable(true);
    }

    // Copies an existing scene button (so it inherits the scene's look) instead of requiring
    // a manual scene edit. Assign real buttons in the Inspector to override this.
    private Button CloneButton(Button template, string objectName, string label, Vector2 anchoredPosition)
    {
        if (template == null)
            return null;

        GameObject clone = Instantiate(template.gameObject, template.transform.parent);
        clone.name = objectName;

        var rect = clone.GetComponent<RectTransform>();
        if (rect != null)
        {
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = runtimeReportButtonSize;
        }

        var button = clone.GetComponent<Button>();
        button.onClick = new Button.ButtonClickedEvent(); // drop any listeners copied from the template
        button.interactable = true;

        var text = clone.GetComponentInChildren<TMP_Text>();
        if (text != null)
        {
            text.text = label;
            text.enableAutoSizing = true;
            text.fontSizeMin = 12;
            text.fontSizeMax = 22;
        }

        return button;
    }

    private string GetExportFolder()
    {
        return saveFolderPathField != null && !string.IsNullOrWhiteSpace(saveFolderPathField.text)
            ? saveFolderPathField.text.Trim()
            : Path.Combine(Application.persistentDataPath, exportFolderName);
    }

    private static string FindNewestCsv(string folder)
    {
        if (!Directory.Exists(folder))
            return null;

        return new DirectoryInfo(folder).GetFiles("*.csv")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => f.FullName)
            .FirstOrDefault();
    }

    // ------------------------------------------------------------------ misc

    private void OnExitClicked()
    {
        Debug.Log("[TherapistUIController] Exit requested — closing application.");
        Application.Quit();
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
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
#elif UNITY_STANDALONE_WIN
        if (WindowsFolderBrowser.TryBrowseForFolder("Choose Save Folder", out string selectedFolder) && saveFolderPathField != null)
        {
            saveFolderPathField.text = selectedFolder;
        }
#else
        SetStatus("Folder browsing isn't available on this platform. Type or paste a folder path above instead.", StatusType.Info);
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
