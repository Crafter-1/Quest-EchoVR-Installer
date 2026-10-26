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
    private NevrArtifact _pendingApk;

    // An APK download that receives nothing for this long, or runs this long in all, is
    // stopped with a message instead of leaving the player on a progress screen.
    private const double DownloadStallSeconds = 30;
    private const double DownloadTotalSeconds = 3600;

    // Downloads the Quest APK the signed manifest names, checks it against the manifest,
    // then hands it to Android's installer once the game data is ready. `apkOnlyUpdate`
    // skips waiting for the data download.
    public void DownloadAndInstallSigned(NevrArtifact apk, bool apkOnlyUpdate)
    {
        InstallerLog.Mark("APK_INSTALL_REQUEST",
            "mode=signed | apkOnly=" + apkOnlyUpdate + " | versionCode=" + apk.AndroidVersionCode);

        _apkDownloadReady = false;
        _installStarted = false;
        _pendingApkPath = null;
        _pendingPackageVersionCode = 0;
        _pendingApk = null;
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

    // After Android's installer returns: the package on the headset must be the APK this
    // installer verified, signed by the key the manifest named. Returns null when it is,
    // otherwise a sentence for the player.
    public string CheckPendingApkInstalled()
    {
        string expectedCert = _pendingApk != null ? _pendingApk.AndroidSignerCertSha256 : null;
        long expectedVersion = _pendingPackageVersionCode;
        if ((string.IsNullOrEmpty(expectedCert) || expectedVersion <= 0) &&
            InstallVersionMarker.TryReadPending(out InstallVersionMarkerData pending))
        {
            expectedCert = string.IsNullOrEmpty(expectedCert) ? pending.SignerCertSha256 : expectedCert;
            expectedVersion = expectedVersion > 0 ? expectedVersion : pending.PackageVersionCode;
        }

        if (!NevrAndroid.TryReadInstalled(
                NevrQuestRelease.EchoPackageName,
                out bool installed,
                out NevrAndroid.PackageDetails details,
                out string readError))
        {
            InstallerLog.Error("INSTALLED_CHECK_FAILED | " + readError);
            return "The installer could not check the Echo VR that was installed (" + readError + "), so it cannot confirm the install.";
        }

        string message = NevrQuestRelease.CheckInstalled(
            expectedCert, expectedVersion, installed,
            installed ? details.VersionCode : 0, installed ? details.SignerCertSha256 : null);
        if (message != null)
            InstallerLog.Error("INSTALL_NOT_CONFIRMED | " + message);
        else
            InstallerLog.Mark("INSTALL_CONFIRMED",
                "versionCode=" + details.VersionCode + " | signer=" + details.SignerCertSha256[0]);

        return message;
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

            _downloadedBytes = 0;
            _totalBytes = apk.Size;
            var outcome = new NevrHttpOutcome();

            SetStatus("Connecting to the APK download...");
            yield return NevrHttpDownload.Get(
                NevrManifest.BuildArtifactUrl(apk), stream, apk.Size, true, 0,
                DownloadStallSeconds, DownloadTotalSeconds, outcome,
                received =>
                {
                    _downloadedBytes = received;
                    UpdateStatusText();
                });

            string failure = outcome.Failure;
            if (failure == null &&
                !string.Equals(outcome.Sha256, apk.Sha256, StringComparison.OrdinalIgnoreCase))
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
        _pendingApk = apk;
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

    private void Fail(string logEvent, string message, string prefix = "APK download failed: ")
    {
        InstallerLog.Error(logEvent + " | " + message);
        SetStatus(prefix + message);
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
            string.IsNullOrEmpty(_pendingApkPath) || _pendingApk == null)
            return;

        _installStarted = true;
        StartCoroutine(VerifyThenInstall(_pendingApk, _pendingApkPath));
    }

    // The APK sat on external storage while the game data downloaded, where anything with
    // file access could have swapped it. Hash it again, and read its package and signer
    // again, and hand it to Android in the same step: nothing waits between the last check
    // and the install intent.
    private IEnumerator VerifyThenInstall(NevrArtifact apk, string path)
    {
        SetStatus("Downloads complete. Checking the APK once more before installing...");
        string sha256 = null;
        string hashError = null;
        yield return Sha256Utility.CalculateFile(path, hash => sha256 = hash, error => hashError = error);

        string refusal = null;
        if (hashError != null)
            refusal = "The installer could not read the downloaded APK again (" + hashError + "), so it did not install it.";
        else if (TryGetLength(path) != apk.Size ||
                 !string.Equals(sha256, apk.Sha256, StringComparison.OrdinalIgnoreCase))
            refusal = "The downloaded APK changed on the headset after it was checked, so it was not installed. Try the install again.";
        else if (!NevrAndroid.TryReadApk(path, out NevrAndroid.PackageDetails details, out string readError))
            refusal = "Android could not read the downloaded APK again: " + readError;
        else
            refusal = NevrQuestRelease.CheckApk(apk, details.PackageName, details.VersionCode, details.SignerCertSha256);

        if (refusal != null)
        {
            TryDelete(path);
            _installStarted = false;
            _apkDownloadReady = false;
            _pendingApkPath = null;
            Fail("APK_REVERIFY_FAILED", refusal, "APK not installed: ");
            yield break;
        }

        InstallerLog.Mark("APK_INSTALL_LAUNCH", "path=" + path + " | sha256=" + sha256);
        SetStatus("Installing APK...");
        InstallApk(path);
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
