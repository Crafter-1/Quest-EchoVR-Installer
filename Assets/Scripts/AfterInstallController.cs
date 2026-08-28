using System;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class AfterInstallController : MonoBehaviour
{
    private const string EchoPackageName = "com.readyatdawn.r15";
    private const string DataReadyPreference = "EchoDataExtractionComplete";

    [Header("Panels")]
    public GameObject downloadPanel;
    public GameObject afterInstallPanel;

    [Header("UI")]
    public TMP_Text statusText;
    public Button extractDataButton;
    public Button openEchoPermissionsButton;
    public Button launchEchoVrButton;

    [Header("Install flow")]
    public Download dataDownloader;

    private bool _installFlowStarted;
    private bool _dataDownloaded;
    private bool _dataReady;
    private bool _openingEchoSettings;
    private CanvasGroup _downloadCanvasGroup;

    private void Start()
    {
        if (extractDataButton != null)
            extractDataButton.onClick.AddListener(ExtractGameData);

        if (openEchoPermissionsButton != null)
            openEchoPermissionsButton.onClick.AddListener(OpenEchoPermissions);

        if (launchEchoVrButton != null)
            launchEchoVrButton.onClick.AddListener(LaunchEchoVr);

        if (afterInstallPanel != null)
            afterInstallPanel.SetActive(false);

        _dataReady = PlayerPrefs.GetInt(DataReadyPreference, 0) == 1 || DataMarkerExists();
        RefreshState();
    }

    private void OnDestroy()
    {
        if (extractDataButton != null)
            extractDataButton.onClick.RemoveListener(ExtractGameData);

        if (openEchoPermissionsButton != null)
            openEchoPermissionsButton.onClick.RemoveListener(OpenEchoPermissions);

        if (launchEchoVrButton != null)
            launchEchoVrButton.onClick.RemoveListener(LaunchEchoVr);
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        // The Android package installer temporarily takes focus away from Unity.
        if (hasFocus && (_installFlowStarted || _openingEchoSettings))
        {
            _openingEchoSettings = false;

            if (launchEchoVrButton != null)
                launchEchoVrButton.interactable = true;

            RefreshState();
        }
    }

    public bool HandleExistingInstallIfPresent()
    {
        if (!IsEchoInstalled())
            return false;

        _dataReady = PlayerPrefs.GetInt(DataReadyPreference, 0) == 1 || DataMarkerExists();
        if (_dataReady)
        {
            RefreshState();
            return true;
        }

        if (dataDownloader == null)
            dataDownloader = FindFirstObjectByType<Download>(FindObjectsInactive.Include);

        if (dataDownloader != null && dataDownloader.HasDownloadedDataZip())
        {
            _dataDownloaded = true;
            RefreshState();
        }
        else
        {
            if (afterInstallPanel != null)
                afterInstallPanel.SetActive(false);

            SetDownloadPanelVisible(true);
            SetStatus("Echo VR is installed. Downloading the missing game data...");
        }

        return true;
    }

    public void NotifyInstallFlowStarted()
    {
        _installFlowStarted = true;
        SetStatus("Complete the Echo VR installation, then return here.");
    }

    public void NotifyDataReady()
    {
        _dataReady = true;
        PlayerPrefs.SetInt(DataReadyPreference, 1);
        PlayerPrefs.Save();
        RefreshState();
    }

    public void NotifyDataDownloaded()
    {
        _dataDownloaded = true;
        _dataReady = false;
        PlayerPrefs.SetInt(DataReadyPreference, 0);
        PlayerPrefs.Save();
        RefreshState();
    }

    public void NotifyDataSetupFailed(string error)
    {
        _dataReady = false;

        if (afterInstallPanel != null)
            afterInstallPanel.SetActive(true);

        SetButtonMode(showExtract: true, showFinalActions: false);
        if (extractDataButton != null)
            extractDataButton.interactable = true;

        SetStatus("Echo data setup failed. Select Extract Data to retry.\n" + error);
    }

    public void ExtractGameData()
    {
        if (dataDownloader == null)
            dataDownloader = FindFirstObjectByType<Download>();

        if (dataDownloader == null)
        {
            SetStatus("Could not find the downloaded game data.");
            return;
        }

        if (extractDataButton != null)
            extractDataButton.interactable = false;

        SetStatus("Extracting Echo VR data...");
        dataDownloader.ExtractDownloadedData();
    }

    public void OpenEchoPermissions()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        Debug.Log("[AfterInstallController] OpenEchoPermissions invoked.");

        if (!IsEchoInstalled())
        {
            SetStatus("Echo VR is not installed, so its permissions cannot be opened yet.");
            return;
        }

        try
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var intent = new AndroidJavaObject("android.content.Intent");
            intent.Call<AndroidJavaObject>(
                "setClassName",
                Application.identifier,
                "com.crafter.evrinstaller.bridge.EchoPermissionBridgeActivity");

            _openingEchoSettings = true;
            if (launchEchoVrButton != null)
                launchEchoVrButton.interactable = false;

            activity.Call("startActivity", intent);
        }
        catch (Exception exception)
        {
            _openingEchoSettings = false;
            if (launchEchoVrButton != null)
                launchEchoVrButton.interactable = true;

            Debug.LogError($"[AfterInstallController] Could not open Echo VR permissions: {exception}");
            SetStatus("Could not open Echo permissions. Open Settings > Apps > Echo VR > Permissions.");
            OpenApplicationSettingsFallback();
        }
#else
        SetStatus("Echo VR permissions can only be opened on an Android device.");
#endif
    }

    private void OpenApplicationSettingsFallback()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var intent = new AndroidJavaObject(
                "android.content.Intent", "android.settings.APPLICATION_SETTINGS");
            intent.Call<AndroidJavaObject>("addFlags", 0x10000000);
            activity.Call("startActivity", intent);
        }
        catch (Exception exception)
        {
            Debug.LogError($"[AfterInstallController] Could not open Apps settings: {exception}");
        }
#endif
    }

    public void LaunchEchoVr()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        Debug.Log("[AfterInstallController] LaunchEchoVr invoked.");

        // Never launch Echo without microphone access. Besides protecting the
        // user from entering the game without voice chat, this also makes the
        // two post-install button paths fail-safe if an XR UI click is routed
        // to the wrong selectable.
        if (!HasEchoMicrophonePermission())
        {
            SetStatus("Echo VR still needs microphone permission.");
            OpenEchoPermissions();
            return;
        }

        try
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var packageManager = activity.Call<AndroidJavaObject>("getPackageManager");
            using var intent = packageManager.Call<AndroidJavaObject>(
                "getLaunchIntentForPackage", EchoPackageName);

            if (intent == null)
            {
                SetStatus("Echo VR is not installed yet.");
                return;
            }

            activity.Call("startActivity", intent);
        }
        catch (Exception exception)
        {
            Debug.LogError($"[AfterInstallController] Could not launch Echo VR: {exception}");
            SetStatus("Could not launch Echo VR. Check that installation completed.");
        }
#else
        SetStatus("Echo VR can only be launched on an Android device.");
#endif
    }

    private static bool HasEchoMicrophonePermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var packageManager = activity.Call<AndroidJavaObject>("getPackageManager");
            int result = packageManager.Call<int>(
                "checkPermission", "android.permission.RECORD_AUDIO", EchoPackageName);
            return result == 0; // PackageManager.PERMISSION_GRANTED
        }
        catch (Exception exception)
        {
            Debug.LogError($"[AfterInstallController] Could not check Echo microphone permission: {exception}");
            return false;
        }
#else
        return true;
#endif
    }

    public void RefreshState()
    {
        bool echoInstalled = IsEchoInstalled();

        if (echoInstalled && _dataReady)
        {
            if (downloadPanel != null)
                downloadPanel.SetActive(false);

            if (afterInstallPanel != null)
                afterInstallPanel.SetActive(true);

            SetButtonMode(showExtract: false, showFinalActions: true);

            SetStatus("Echo VR and its data are ready. Grant permissions before launching.");
            return;
        }

        if (echoInstalled && _dataDownloaded)
        {
            // Keep the download object active because it owns the extraction
            // coroutine, but hide all of its visuals and disable its raycasts.
            SetDownloadPanelVisible(false);

            if (afterInstallPanel != null)
                afterInstallPanel.SetActive(true);

            SetButtonMode(showExtract: true, showFinalActions: false);
            SetStatus("Echo VR is installed. Extract the downloaded game data to continue.");
            return;
        }

        if (!echoInstalled && _dataDownloaded)
            SetStatus("Downloads are ready. Finish installing Echo VR, then return here.");
        else if (echoInstalled)
            SetStatus("Echo VR is installed. Waiting for the game data download to finish.");
    }

    private void SetButtonMode(bool showExtract, bool showFinalActions)
    {
        if (extractDataButton != null)
        {
            extractDataButton.gameObject.SetActive(showExtract);
            extractDataButton.interactable = showExtract;
        }

        if (openEchoPermissionsButton != null)
            openEchoPermissionsButton.gameObject.SetActive(showFinalActions);

        if (launchEchoVrButton != null)
            launchEchoVrButton.gameObject.SetActive(showFinalActions);
    }

    private void SetDownloadPanelVisible(bool visible)
    {
        if (downloadPanel == null)
            return;

        // Download owns the extraction coroutine, so the panel object must be
        // active even when its visuals are hidden behind the after-install UI.
        if (!downloadPanel.activeSelf)
            downloadPanel.SetActive(true);

        if (_downloadCanvasGroup == null)
        {
            _downloadCanvasGroup = downloadPanel.GetComponent<CanvasGroup>();
            if (_downloadCanvasGroup == null)
                _downloadCanvasGroup = downloadPanel.AddComponent<CanvasGroup>();
        }

        _downloadCanvasGroup.alpha = visible ? 1f : 0f;
        _downloadCanvasGroup.interactable = visible;
        _downloadCanvasGroup.blocksRaycasts = visible;
    }

    private static bool IsEchoInstalled()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var packageManager = activity.Call<AndroidJavaObject>("getPackageManager");
            using var launchIntent = packageManager.Call<AndroidJavaObject>(
                "getLaunchIntentForPackage", EchoPackageName);
            return launchIntent != null;
        }
        catch (Exception)
        {
            return false;
        }
#else
        // This makes the final-panel transition testable in Play Mode.
        return true;
#endif
    }

    private static bool DataMarkerExists()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        string marker = Path.Combine(
            "/storage/emulated/0/Android/media",
            EchoPackageName,
            "files",
            ".quest_evr_data_ready");
        return File.Exists(marker);
#else
        return false;
#endif
    }

    private void SetStatus(string message)
    {
        if (statusText != null)
            statusText.text = message;

        Debug.Log("[AfterInstallController] " + message);
    }
}
