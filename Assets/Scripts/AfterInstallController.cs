using System;
using System.Collections;
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
    private NevrReleaseManager _releaseManager;
    private Coroutine _installCompletionMonitor;

    private void Start()
    {
        InstallerLog.Mark("AFTER_INSTALL_START");
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
            InstallerLog.Mark("UNITY_FOCUS_RETURN", "installPending=" + _installFlowStarted +
                " | settingsPending=" + _openingEchoSettings);
            _openingEchoSettings = false;

            if (launchEchoVrButton != null)
                launchEchoVrButton.interactable = true;

            if (_installFlowStarted)
            {
                if (IsEchoInstalled())
                    HandleInstallDetected("focus return");
                else
                    InstallerLog.Warning("UNITY_FOCUS_RETURN_WITHOUT_ECHO_PACKAGE");
            }
            else
                RefreshState();
        }
    }

    private IEnumerator MonitorInstallCompletion()
    {
        const float timeoutSeconds = 90f;
        const float pollSeconds = 1f;
        float startedAt = Time.unscaledTime;
        float nextProgressLog = startedAt;

        while (_installFlowStarted)
        {
            if (IsEchoInstalled())
            {
                HandleInstallDetected("background monitor");
                yield break;
            }

            float elapsed = Time.unscaledTime - startedAt;
            if (elapsed >= timeoutSeconds)
            {
                InstallerLog.Warning("ANDROID_INSTALL_DETECTION_TIMEOUT | seconds=" + timeoutSeconds);
                _installFlowStarted = false;
                SetStatus("Android installation was not detected yet. Return to the installer or try again.");
                RefreshState();
                yield break;
            }

            if (Time.unscaledTime >= nextProgressLog)
            {
                InstallerLog.Mark("ANDROID_INSTALL_WAITING", "elapsedSeconds=" + Mathf.FloorToInt(elapsed));
                nextProgressLog = Time.unscaledTime + 10f;
            }

            yield return new WaitForSecondsRealtime(pollSeconds);
        }
    }

    private void HandleInstallDetected(string source)
    {
        if (!_installFlowStarted)
            return;

        InstallerLog.Mark("ANDROID_INSTALL_DETECTED", "source=" + source);
        _installFlowStarted = false;

        if (_installCompletionMonitor != null)
        {
            StopCoroutine(_installCompletionMonitor);
            _installCompletionMonitor = null;
        }

        if (_apkUpdateInProgress)
            StartCoroutine(CompleteApkUpdateAfterReturn());
        else
            RefreshState();
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
        InstallerLog.Mark("UPDATE_CHECK_REQUESTED");
        if (_updateCheckRunning)
            return;

        StartCoroutine(CheckForUpdatesRoutine());
    }

    private System.Collections.IEnumerator CheckForUpdatesRoutine()
    {
        SetUpdateBusy(true, "[CHECKING...]");
        SetStatus("Checking for Echo VR updates...");

        NevrReleaseManager release = GetReleaseManager();
        bool finished = false;
        bool succeeded = false;
        string releaseError = null;
        release.Refresh((success, error) =>
        {
            succeeded = success;
            releaseError = error;
            finished = true;
        });

        yield return new WaitUntil(() => finished);
        if (!succeeded)
        {
            InstallerLog.Error("UPDATE_CHECK_MANIFEST_FAILED | " + releaseError);
            FinishUpdateWithError(releaseError ?? "Could not check for updates.");
            yield break;
        }

        NevrArtifact apk = release.CurrentApk;
        if (!NevrAndroid.TryReadInstalled(
                NevrQuestRelease.EchoPackageName,
                out bool installed,
                out NevrAndroid.PackageDetails details,
                out string readError))
        {
            FinishUpdateWithError("Could not check which Echo VR is installed: " + readError);
            yield break;
        }

        NevrInstallAction action = NevrQuestRelease.Plan(
            apk, installed, installed ? details.VersionCode : 0, installed ? details.SignerCertSha256 : null);
        InstallerLog.Mark("UPDATE_PLAN", "action=" + action);
        switch (action)
        {
            case NevrInstallAction.AlreadyCurrent:
                InstallerLog.Mark("UPDATE_APK_CURRENT");
                yield return SynchronizeUpdateAssets(
                    finalizeInstallMarker: false,
                    successMessage: "Echo VR is up to date. All update assets were verified.");
                break;
            case NevrInstallAction.NewerInstalled:
                FinishUpdateWithError(
                    "The Echo VR on this headset is newer than the release on the update list, so nothing was changed.");
                break;
            case NevrInstallAction.Update:
                BeginSignedApkUpdate(apk, replaceOtherSigner: false);
                break;
            case NevrInstallAction.Install:
                FinishUpdateWithError("Echo VR is not installed on this headset. Use the install screen first.");
                break;
            default:
                BeginSignedApkUpdate(apk, replaceOtherSigner: true);
                break;
        }
    }

    private void BeginSignedApkUpdate(NevrArtifact apk, bool replaceOtherSigner)
    {
        InstallerLog.Mark("UPDATE_SIGNED_APK_REQUIRED");
        ApkInstaller installer = GetApkInstaller();
        if (installer == null)
        {
            FinishUpdateWithError("Could not find the APK installer.");
            return;
        }

        BeginApkUpdateDisplay();
        installer.DownloadAndInstallSigned(apk, apkOnlyUpdate: true, replaceOtherSigner: replaceOtherSigner);
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
        InstallerLog.Mark("APK_UPDATE_RETURNED_FROM_ANDROID");
        if (downloadPanel != null)
            downloadPanel.SetActive(false);

        if (afterInstallPanel != null)
            afterInstallPanel.SetActive(true);

        ApkInstaller installer = GetApkInstaller();
        if (installer == null || !installer.IsPendingApkInstalled())
        {
            InstallerLog.Error("APK_UPDATE_NOT_CONFIRMED");
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
        InstallerLog.Error("UPDATE_FAILED | " + message);
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

    private NevrReleaseManager GetReleaseManager()
    {
        if (_releaseManager == null)
            _releaseManager = FindFirstObjectByType<NevrReleaseManager>();
        if (_releaseManager == null)
            _releaseManager = gameObject.AddComponent<NevrReleaseManager>();

        return _releaseManager;
    }

    private ApkInstaller GetApkInstaller()
    {
        if (_apkInstaller == null)
            _apkInstaller = FindFirstObjectByType<ApkInstaller>(FindObjectsInactive.Include);
        return _apkInstaller;
    }

    public bool HandleExistingInstallIfPresent()
    {
        InstallerLog.Mark("EXISTING_INSTALL_CHECK");
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
        InstallerLog.Mark("ANDROID_INSTALL_STARTED");
        _installFlowStarted = true;
        if (_installCompletionMonitor != null)
            StopCoroutine(_installCompletionMonitor);
        _installCompletionMonitor = StartCoroutine(MonitorInstallCompletion());
        SetStatus("Complete the Echo VR installation, then return here.");
    }

    public void NotifyDataReady()
    {
        InstallerLog.Mark("DATA_READY_NOTIFIED");
        _dataReady = true;
        PlayerPrefs.SetInt(DataReadyPreference, 1);
        PlayerPrefs.Save();
        RefreshState();
    }

    public void NotifyDataDownloaded()
    {
        InstallerLog.Mark("DATA_DOWNLOADED_NOTIFIED");
        _dataDownloaded = true;
        _dataReady = false;
        PlayerPrefs.SetInt(DataReadyPreference, 0);
        PlayerPrefs.Save();
        RefreshState();
    }

    public void NotifyDataSetupFailed(string error)
    {
        InstallerLog.Error("DATA_SETUP_FAILURE_NOTIFIED | " + error);
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
        InstallerLog.Mark("EXTRACT_BUTTON_PRESSED");
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
        InstallerLog.Mark("PERMISSIONS_BUTTON_PRESSED");
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
        InstallerLog.Mark("LAUNCH_BUTTON_PRESSED");
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
