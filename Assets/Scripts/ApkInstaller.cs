using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

public class ApkInstaller : MonoBehaviour
{
    [Header("UI - matches Download.cs style")]
    public TMPro.TMP_Text statusText;
    public AfterInstallController afterInstallController;

    private long _downloadedBytes;
    private long _totalBytes;
    private string _pendingApkPath;
    private bool _apkDownloadReady;
    private bool _dataDownloadReady;
    private bool _installStarted;
    private bool _apkOnlyUpdate;
    private int _pendingPackageVersionCode;
    private bool _replaceOtherSigner;

    // Downloads the Quest APK the signed manifest names, checks it against the manifest,
    // then hands it to Android's installer once the game data is ready. `apkOnlyUpdate`
    // skips waiting for the data download.
    // `replaceOtherSigner` is for a headset whose Echo VR is signed by someone else: once the
    // new APK is downloaded and checked, the player is told, then Android is asked to
    // uninstall the old one, then the new one is installed.
    public void DownloadAndInstallSigned(NevrArtifact apk, bool apkOnlyUpdate, bool replaceOtherSigner = false)
    {
        InstallerLog.Mark("APK_INSTALL_REQUEST",
            "mode=signed | apkOnly=" + apkOnlyUpdate + " | replaceOtherSigner=" + replaceOtherSigner +
            " | versionCode=" + apk.AndroidVersionCode);

        _replaceOtherSigner = replaceOtherSigner;
        _apkDownloadReady = false;
        _installStarted = false;
        _pendingApkPath = null;
        _pendingPackageVersionCode = 0;
        _apkOnlyUpdate = apkOnlyUpdate;
        if (apkOnlyUpdate)
            _dataDownloadReady = true;

        StartCoroutine(DownloadAndInstallSignedRoutine(apk));
    }

    public void SetStatusMessage(string message)
    {
        SetStatus(message);
    }

    public void SkipApkInstallBecauseCurrent()
    {
        _apkDownloadReady = true;
        _installStarted = true;
        _pendingApkPath = null;
        SetStatus("The installed Echo APK already matches this update. Downloading game data...");
    }

    public static bool IsEchoVrInstalled()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var packageManager = activity.Call<AndroidJavaObject>("getPackageManager"))
            using (var intent = packageManager.Call<AndroidJavaObject>(
                       "getLaunchIntentForPackage", QuestUpdateManager.EchoPackageName))
                return intent != null;
        }
        catch (Exception)
        {
            return false;
        }
#else
        return false;
#endif
    }

    public bool IsPendingApkInstalled()
    {
        int expectedVersion = _pendingPackageVersionCode;
        if (expectedVersion <= 0 &&
            InstallVersionMarker.TryReadPending(out InstallVersionMarkerData pending))
            expectedVersion = pending.PackageVersionCode;

        int installedVersion = QuestUpdateManager.GetInstalledEchoVersionCode();
        return expectedVersion <= 0
            ? IsEchoVrInstalled()
            : installedVersion == expectedVersion;
    }

    private IEnumerator DownloadAndInstallSignedRoutine(NevrArtifact apk)
    {
        string savePath = Path.Combine(Application.persistentDataPath, apk.Name);
        string temporaryPath = savePath + ".part";

        // A file from an earlier run is reused only if it is the exact size and hash the
        // manifest now names.
        bool haveVerifiedFile = false;
        if (TryGetLength(savePath) == apk.Size)
        {
            SetStatus("Checking the downloaded APK...");
            string existingSha256 = null;
            string existingError = null;
            yield return Sha256Utility.CalculateFile(
                savePath, hash => existingSha256 = hash, error => existingError = error);
            haveVerifiedFile = existingError == null &&
                               string.Equals(existingSha256, apk.Sha256, StringComparison.OrdinalIgnoreCase);
        }

        if (!haveVerifiedFile)
        {
            TryDelete(savePath);
            TryDelete(temporaryPath);

            FileStream stream;
            try
            {
                Directory.CreateDirectory(Application.persistentDataPath);
                stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
            }
            catch (Exception exception)
            {
                Fail("APK_DOWNLOAD_FAILED", "Could not save the APK on the headset: " + exception.Message);
                yield break;
            }

            var sink = new NevrDownloadSink(stream, apk.Size, true);
            _downloadedBytes = 0;
            _totalBytes = apk.Size;
            string failure = null;
            string downloadedSha256 = null;

            SetStatus("Connecting to the APK download...");
            using (var handler = new NevrCappedDownloadHandler(sink))
            using (var request = UnityWebRequest.Get(NevrManifest.BuildArtifactUrl(apk)))
            {
                request.timeout = 0;
                request.downloadHandler = handler;
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();

                while (!operation.isDone)
                {
                    _downloadedBytes = sink.Received;
                    UpdateStatusText();
                    yield return null;
                }

                if (sink.Error != null)
                    failure = sink.Error;
                else if (request.result == UnityWebRequest.Result.ProtocolError)
                    failure = "The update server answered with an error (HTTP " + request.responseCode + ").";
                else if (request.result != UnityWebRequest.Result.Success)
                    failure = "Could not download the APK (" + request.error + "). Check the headset's internet connection and try again.";
                else if (!sink.Complete(out downloadedSha256))
                    failure = sink.Error;
            }

            sink.Dispose();
            if (failure == null &&
                !string.Equals(downloadedSha256, apk.Sha256, StringComparison.OrdinalIgnoreCase))
                failure = "The downloaded APK is not the file the update list names (its checksum differs), so it was not installed.";

            if (failure == null && !TryReplaceFile(temporaryPath, savePath, out string replaceError))
                failure = "Could not save the APK on the headset: " + replaceError;

            if (failure != null)
            {
                TryDelete(temporaryPath);
                Fail("APK_DOWNLOAD_FAILED", failure);
                yield break;
            }
        }

        SetStatus("Verifying the APK...");
        if (!NevrAndroid.TryReadApk(savePath, out NevrAndroid.PackageDetails details, out string readError))
        {
            TryDelete(savePath);
            Fail("APK_VERIFY_FAILED", "Android could not read the downloaded APK: " + readError);
            yield break;
        }

        string mismatch = NevrQuestRelease.CheckApk(
            apk, details.PackageName, details.VersionCode, details.SignerCertSha256);
        if (mismatch != null)
        {
            TryDelete(savePath);
            Fail("APK_VERIFY_FAILED", mismatch);
            yield break;
        }

        _pendingPackageVersionCode = (int)details.VersionCode;
        if (!InstallVersionMarker.SavePendingSigned(apk, _pendingPackageVersionCode, out string markerError))
        {
            // The APK is verified, so this should be visible but must not stop the install.
            InstallerLog.Warning("PENDING_MARKER_FAILED | " + markerError);
            Debug.LogWarning($"[ApkInstaller] Could not save pending install marker: {markerError}");
        }

        _pendingApkPath = savePath;
        _apkDownloadReady = true;
        InstallerLog.Mark("APK_VERIFIED", "versionCode=" + details.VersionCode + " | sha256=" + apk.Sha256);
        SetStatus(_apkOnlyUpdate
            ? "APK update downloaded and verified. Opening Android installer..."
            : "APK downloaded and verified. Waiting for game data download...");
        TryBeginInstall();
    }

    private void Fail(string logEvent, string message)
    {
        InstallerLog.Error(logEvent + " | " + message);
        SetStatus("APK download failed: " + message);
    }

    private static long TryGetLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    public void NotifyDataDownloadReady()
    {
        _dataDownloadReady = true;
        TryBeginInstall();
    }

    private void TryBeginInstall()
    {
        if (_installStarted || !_apkDownloadReady || !_dataDownloadReady ||
            string.IsNullOrEmpty(_pendingApkPath))
            return;

        _installStarted = true;
        InstallerLog.Mark("APK_INSTALL_LAUNCH", "path=" + _pendingApkPath);
        if (_replaceOtherSigner)
        {
            StartCoroutine(RemoveOldThenInstall(_pendingApkPath));
            return;
        }

        SetStatus("Downloads complete. Installing APK...");
        InstallApk(_pendingApkPath);
    }

    private IEnumerator RemoveOldThenInstall(string apkPath)
    {
        InstallerLog.Mark("MIGRATION_NOTICE_SHOWN");
        for (int left = NevrMigrationMessages.SecondsToRead; left > 0; left--)
        {
            SetStatus(NevrMigrationMessages.BeforeUninstall(left));
            yield return new WaitForSecondsRealtime(1f);
        }

        InstallerLog.Mark("MIGRATION_UNINSTALL_REQUESTED", "package=" + NevrQuestRelease.EchoPackageName);
        if (!NevrAndroid.TryRequestUninstall(NevrQuestRelease.EchoPackageName, out string requestError))
        {
            InstallerLog.Error("MIGRATION_UNINSTALL_REQUEST_FAILED | " + requestError);
            SetStatus(NevrMigrationMessages.CouldNotAsk);
            _installStarted = false;
            yield break;
        }

        SetStatus("Waiting for you to answer Android's uninstall question...");
        const float timeoutSeconds = 180f;
        float startedAt = Time.unscaledTime;
        while (Time.unscaledTime - startedAt < timeoutSeconds)
        {
            yield return new WaitForSecondsRealtime(1f);
            if (!NevrAndroid.TryReadInstalled(
                    NevrQuestRelease.EchoPackageName, out bool installed, out _, out string readError))
            {
                InstallerLog.Warning("MIGRATION_CHECK_FAILED | " + readError);
                continue;
            }

            if (!installed)
            {
                InstallerLog.Mark("MIGRATION_OLD_REMOVED");
                SetStatus(NevrMigrationMessages.Removed);
                InstallApk(apkPath);
                yield break;
            }
        }

        InstallerLog.Warning("MIGRATION_OLD_STILL_INSTALLED");
        SetStatus(NevrMigrationMessages.StillInstalled);
        _installStarted = false;
    }

    private void UpdateStatusText()
    {
        float downloadedMb = _downloadedBytes / 1048576f;
        float totalMb = _totalBytes / 1048576f;
        float percent = _totalBytes > 0
            ? (_downloadedBytes / (float)_totalBytes) * 100f
            : 0f;

        SetStatus(
            "Downloading APK\n" +
            $">>> {downloadedMb:0.0}/{totalMb:0.0}MB <<<\n" +
            $"{percent:0.0}%");
    }

    private void SetStatus(string message)
    {
        if (statusText != null)
            statusText.text = message;

        Debug.Log("[ApkInstaller] " + message);
    }

    private static bool TryReplaceFile(string source, string destination, out string error)
    {
        error = null;
        string backup = destination + ".backup";

        try
        {
            TryDelete(backup);
            if (File.Exists(destination))
                File.Move(destination, backup);

            try
            {
                File.Move(source, destination);
                TryDelete(backup);
                return true;
            }
            catch
            {
                if (!File.Exists(destination) && File.Exists(backup))
                    File.Move(backup, destination);
                throw;
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private void InstallApk(string apkPath)
    {
        InstallerLog.Mark("ANDROID_INSTALL_INTENT", "path=" + apkPath);
#if UNITY_ANDROID && !UNITY_EDITOR
        if (afterInstallController == null)
            afterInstallController = FindFirstObjectByType<AfterInstallController>();

        afterInstallController?.NotifyInstallFlowStarted();

        using (var playerClass = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        using (var activity = playerClass.GetStatic<AndroidJavaObject>("currentActivity"))
        using (var context = activity.Call<AndroidJavaObject>("getApplicationContext"))
        using (var fileProviderClass = new AndroidJavaClass("androidx.core.content.FileProvider"))
        using (var javaFile = new AndroidJavaObject("java.io.File", apkPath))
        {
            string authority = Application.identifier + ".fileprovider";
            using (var uri = fileProviderClass.CallStatic<AndroidJavaObject>(
                       "getUriForFile", context, authority, javaFile))
            using (var intent = new AndroidJavaObject(
                       "android.content.Intent", "android.intent.action.VIEW"))
            {
                intent.Call<AndroidJavaObject>(
                    "setDataAndType", uri, "application/vnd.android.package-archive");
                intent.Call<AndroidJavaObject>("addFlags", 1);
                intent.Call<AndroidJavaObject>("addFlags", 0x10000000);
                activity.Call("startActivity", intent);
            }
        }
#else
        SetStatus("Install intent only runs on-device (Android build), not in the Editor.");
#endif
    }
}
