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
    private bool _centerButtonChecksUpdates;
    private bool _updateCheckRunning;
    private bool _apkUpdateInProgress;
    private TMP_Text _centerButtonLabel;
    private QuestUpdateManager _updateManager;
    private ApkInstaller _apkInstaller;
    private InstallMenuController _installMenuController;

    private void Start()
    {
        if (extractDataButton != null)
        {
            extractDataButton.onClick.AddListener(HandleCenterButton);
            _centerButtonLabel = extractDataButton.GetComponentInChildren<TMP_Text>(true);
        }

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
            extractDataButton.onClick.RemoveListener(HandleCenterButton);

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
            bool returnedFromInstall = _installFlowStarted;
            _installFlowStarted = false;
            _openingEchoSettings = false;

            if (launchEchoVrButton != null)
                launchEchoVrButton.interactable = true;

            if (returnedFromInstall && _apkUpdateInProgress)
                StartCoroutine(CompleteApkUpdateAfterReturn());
            else
                RefreshState();
        }
    }

    private void HandleCenterButton()
    {
        if (_centerButtonChecksUpdates)
            CheckForUpdates();
        else
            ExtractGameData();
    }

    public void CheckForUpdates()
    {
        if (_updateCheckRunning)
            return;

        StartCoroutine(CheckForUpdatesRoutine());
    }

    private System.Collections.IEnumerator CheckForUpdatesRoutine()
    {
        SetUpdateBusy(true, "[CHECKING...]");
        SetStatus("Checking for Echo VR updates...");

        QuestUpdateManager manager = GetUpdateManager();
        bool manifestFinished = false;
        bool manifestSucceeded = false;
        string manifestError = null;
        manager.RefreshManifest((success, error) =>
        {
            manifestSucceeded = success;
            manifestError = error;
            manifestFinished = true;
        });

        yield return new WaitUntil(() => manifestFinished);
        if (!manifestSucceeded)
        {
            FinishUpdateWithError(manifestError ?? "Could not check for updates.");
            yield break;
        }

        if (!manager.TryGetInstalledMarker(out InstallVersionMarkerData marker))
        {
            FinishUpdateWithError(
                "This installation has no version marker, so its APK cannot be updated safely.");
            yield break;
        }

        bool apkAlreadyCurrent = string.Equals(
            marker.BaseSha256,
            manager.CurrentManifest.BaseApkSha256,
            StringComparison.OrdinalIgnoreCase);

        if (apkAlreadyCurrent)
        {
            yield return SynchronizeUpdateAssets(
                finalizeInstallMarker: false,
                successMessage: "Echo VR is up to date. All update assets were verified.");
            yield break;
        }

        if (marker.Patched)
        {
            _installMenuController = FindFirstObjectByType<InstallMenuController>();
            if (_installMenuController == null)
            {
                FinishUpdateWithError("Could not open the patched APK update screen.");
                yield break;
            }

            SetStatus("A new Echo APK is available. Paste its patched APK link to continue.");
            if (afterInstallPanel != null)
                afterInstallPanel.SetActive(false);

            _installMenuController.BeginPatchedUpdate(manager.CurrentManifest);
            yield break;
        }

        BeginLegacyApkUpdate(manager.CurrentManifest);
    }

    private void BeginLegacyApkUpdate(QuestUpdateManifest manifest)
    {
        ApkInstaller installer = GetApkInstaller();
        if (installer == null)
        {
            FinishUpdateWithError("Could not find the APK installer.");
            return;
        }

        BeginApkUpdateDisplay();
        installer.DownloadAndInstallUpdateFromManifest(
            manifest,
            GetUpdateManager().GetBaseApkMirrors());
    }

    public void BeginPatchedApkUpdate(
        string apkUrl,
        QuestUpdateManifest manifest)
    {
        ApkInstaller installer = GetApkInstaller();
        if (installer == null)
        {
            CancelPatchedUpdate("Could not find the APK installer.");
            return;
        }

        BeginApkUpdateDisplay();
        installer.DownloadAndInstallPatchedUpdateFromUrl(apkUrl, manifest);
    }

    public void CancelPatchedUpdate(string message = null)
    {
        _apkUpdateInProgress = false;
        SetUpdateBusy(false);

        if (afterInstallPanel != null)
            afterInstallPanel.SetActive(true);

        RefreshState();
        if (!string.IsNullOrWhiteSpace(message))
            SetStatus(message);
    }

    private void BeginApkUpdateDisplay()
    {
        _apkUpdateInProgress = true;
        _updateCheckRunning = true;

        if (dataDownloader == null)
            dataDownloader = FindFirstObjectByType<Download>(FindObjectsInactive.Include);

        dataDownloader?.PrepareForApkOnlyUpdateDisplay();

        if (afterInstallPanel != null)
            afterInstallPanel.SetActive(false);

        SetDownloadPanelVisible(true);
    }

    private System.Collections.IEnumerator CompleteApkUpdateAfterReturn()
    {
        if (downloadPanel != null)
            downloadPanel.SetActive(false);

        if (afterInstallPanel != null)
            afterInstallPanel.SetActive(true);

        ApkInstaller installer = GetApkInstaller();
        if (installer == null || !installer.IsPendingApkInstalled())
        {
            _apkUpdateInProgress = false;
            FinishUpdateWithError(
                "The APK update was not installed. You can select Check for Updates to retry.");
            yield break;
        }

        yield return SynchronizeUpdateAssets(
            finalizeInstallMarker: true,
            successMessage: "Echo VR and its update assets are now up to date.");
    }

    private System.Collections.IEnumerator SynchronizeUpdateAssets(
        bool finalizeInstallMarker,
        string successMessage)
    {
        QuestUpdateManager manager = GetUpdateManager();
        bool finished = false;
        bool succeeded = false;
        string updateError = null;

        yield return manager.SynchronizeAssets(
            (success, error) =>
            {
                succeeded = success;
                updateError = error;
                finished = true;
            },
            (message, completed, total) =>
                SetStatus(total > 0
                    ? $"{message}\n{completed}/{total} files"
                    : message));

        if (!finished || !succeeded)
        {
            _apkUpdateInProgress = false;
            FinishUpdateWithError(updateError ?? "The update did not complete.");
            yield break;
        }

        if (finalizeInstallMarker &&
            !manager.FinalizePendingInstall(out string markerError))
        {
            _apkUpdateInProgress = false;
            FinishUpdateWithError("Update installed, but version tracking failed: " + markerError);
            yield break;
        }

        _apkUpdateInProgress = false;
        SetUpdateBusy(false);
        RefreshState();
        SetStatus(successMessage);
    }

    private void FinishUpdateWithError(string message)
    {
        SetUpdateBusy(false);

        if (afterInstallPanel != null)
            afterInstallPanel.SetActive(true);

        RefreshState();
        SetStatus(message);
    }

    private void SetUpdateBusy(bool busy, string centerLabel = null)
    {
        _updateCheckRunning = busy;

        if (extractDataButton != null)
            extractDataButton.interactable = !busy;
        if (openEchoPermissionsButton != null)
            openEchoPermissionsButton.interactable = !busy;
        if (launchEchoVrButton != null)
            launchEchoVrButton.interactable = !busy;

        if (_centerButtonLabel != null)
            _centerButtonLabel.text = centerLabel ??
                                      (_centerButtonChecksUpdates
                                          ? "[CHECK FOR UPDATES]"
                                          : "[EXTRACT DATA]");
    }

    private QuestUpdateManager GetUpdateManager()
    {
        if (_updateManager == null)
            _updateManager = FindFirstObjectByType<QuestUpdateManager>();
        if (_updateManager == null)
            _updateManager = gameObject.AddComponent<QuestUpdateManager>();

        return _updateManager;
    }

    private ApkInstaller GetApkInstaller()
    {
        if (_apkInstaller == null)
            _apkInstaller = FindFirstObjectByType<ApkInstaller>(FindObjectsInactive.Include);
        return _apkInstaller;
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
            bool showCenterButton = showExtract || showFinalActions;
            _centerButtonChecksUpdates = !showExtract && showFinalActions;
            extractDataButton.gameObject.SetActive(showCenterButton);
            extractDataButton.interactable = showCenterButton && !_updateCheckRunning;

            if (_centerButtonLabel != null && !_updateCheckRunning)
                _centerButtonLabel.text = _centerButtonChecksUpdates
                    ? "[CHECK FOR UPDATES]"
                    : "[EXTRACT DATA]";
        }

        if (openEchoPermissionsButton != null)
        {
            openEchoPermissionsButton.gameObject.SetActive(showFinalActions);
            openEchoPermissionsButton.interactable = showFinalActions && !_updateCheckRunning;
        }

        if (launchEchoVrButton != null)
        {
            launchEchoVrButton.gameObject.SetActive(showFinalActions);
            launchEchoVrButton.interactable = showFinalActions && !_updateCheckRunning;
        }
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
