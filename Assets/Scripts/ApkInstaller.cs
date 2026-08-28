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

    private string _apkFileName = "install.apk";
    private long _downloadedBytes;
    private long _totalBytes;
    private string _pendingApkPath;
    private bool _apkDownloadReady;
    private bool _dataDownloadReady;
    private bool _installStarted;
    private bool _apkOnlyUpdate;
    private int _pendingPackageVersionCode;

    // Kept for callers that do not use the update manifest.
    public void DownloadAndInstallFromUrl(string url)
    {
        BeginDownload(new[] { url }, null, null, patched: false, apkOnlyUpdate: false);
    }

    public void DownloadAndInstallFromManifest(
        QuestUpdateManifest manifest,
        string[] mirrors)
    {
        if (manifest == null)
        {
            SetStatus("APK download failed: the update manifest is missing.");
            return;
        }

        BeginDownload(
            mirrors,
            manifest.BaseApkSha256,
            manifest,
            patched: false,
            apkOnlyUpdate: false);
    }

    public void DownloadAndInstallPatchedFromUrl(
        string url,
        QuestUpdateManifest manifest)
    {
        BeginDownload(
            new[] { url }, null, manifest, patched: true, apkOnlyUpdate: false);
    }

    public void DownloadAndInstallUpdateFromManifest(
        QuestUpdateManifest manifest,
        string[] mirrors)
    {
        if (manifest == null)
        {
            SetStatus("APK update failed: the update manifest is missing.");
            return;
        }

        BeginDownload(
            mirrors,
            manifest.BaseApkSha256,
            manifest,
            patched: false,
            apkOnlyUpdate: true);
    }

    public void DownloadAndInstallPatchedUpdateFromUrl(
        string url,
        QuestUpdateManifest manifest)
    {
        BeginDownload(
            new[] { url }, null, manifest, patched: true, apkOnlyUpdate: true);
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

    private void BeginDownload(
        string[] urls,
        string expectedSha256,
        QuestUpdateManifest manifest,
        bool patched,
        bool apkOnlyUpdate)
    {
        if (urls == null || urls.Length == 0)
        {
            SetStatus("APK download failed: no download location was supplied.");
            return;
        }

        _apkDownloadReady = false;
        _installStarted = false;
        _pendingApkPath = null;
        _pendingPackageVersionCode = 0;
        _apkOnlyUpdate = apkOnlyUpdate;
        if (apkOnlyUpdate)
            _dataDownloadReady = true;
        StartCoroutine(DownloadAndInstall(urls, expectedSha256, manifest, patched));
    }

    private IEnumerator DownloadAndInstall(
        string[] urls,
        string expectedSha256,
        QuestUpdateManifest manifest,
        bool patched)
    {
        if (manifest != null)
        {
            _apkFileName = manifest.BaseApkFileName;
        }
        else if (TryGetApkFileName(urls[0], out string fileNameFromUrl))
        {
            _apkFileName = fileNameFromUrl;
        }

        string savePath = Path.Combine(Application.persistentDataPath, _apkFileName);
        string downloadedSha256 = null;
        bool downloadSucceeded = false;
        string lastError = null;

        // A previously verified clean APK can be reused after a retry or restart.
        if (QuestUpdateManifest.IsSha256(expectedSha256) && File.Exists(savePath))
        {
            SetStatus("Checking the downloaded APK...");
            string existingHashError = null;
            yield return Sha256Utility.CalculateFile(
                savePath,
                hash => downloadedSha256 = hash,
                error => existingHashError = error);

            downloadSucceeded = existingHashError == null &&
                                string.Equals(
                                    downloadedSha256,
                                    expectedSha256,
                                    StringComparison.OrdinalIgnoreCase);
        }

        for (int index = 0; index < urls.Length && !downloadSucceeded; index++)
        {
            string url = urls[index];
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri apkUri) ||
                (apkUri.Scheme != Uri.UriSchemeHttps && apkUri.Scheme != Uri.UriSchemeHttp))
            {
                lastError = "Invalid APK download URL.";
                continue;
            }

            _downloadedBytes = 0;
            _totalBytes = 0;
            SetStatus(urls.Length > 1
                ? $"Connecting to APK mirror {index + 1}/{urls.Length}..."
                : "Connecting to APK download...");
            yield return GetFileSize(url, size => _totalBytes = size);

            string temporaryPath = savePath + ".part";
            TryDelete(temporaryPath);

            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 0;
                request.downloadHandler = new DownloadHandlerFile(temporaryPath, true);
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();

                while (!operation.isDone)
                {
                    _downloadedBytes = (long)request.downloadedBytes;
                    UpdateStatusText();
                    yield return null;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    lastError = request.error;
                    TryDelete(temporaryPath);
                    continue;
                }
            }

            SetStatus("Verifying APK download...");
            string hashError = null;
            downloadedSha256 = null;
            yield return Sha256Utility.CalculateFile(
                temporaryPath,
                hash => downloadedSha256 = hash,
                error => hashError = error);

            if (hashError != null)
            {
                lastError = "Could not calculate APK SHA256: " + hashError;
                TryDelete(temporaryPath);
                continue;
            }

            if (QuestUpdateManifest.IsSha256(expectedSha256) &&
                !string.Equals(
                    downloadedSha256,
                    expectedSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                lastError = "APK SHA256 did not match the update manifest.";
                TryDelete(temporaryPath);
                continue;
            }

            if (!TryReplaceFile(temporaryPath, savePath, out lastError))
            {
                TryDelete(temporaryPath);
                continue;
            }

            downloadSucceeded = true;
        }

        if (!downloadSucceeded)
        {
            SetStatus("APK download failed: " + (lastError ?? "all mirrors failed."));
            yield break;
        }

        if (string.IsNullOrEmpty(downloadedSha256))
        {
            string hashError = null;
            yield return Sha256Utility.CalculateFile(
                savePath,
                hash => downloadedSha256 = hash,
                error => hashError = error);

            if (hashError != null)
            {
                SetStatus("APK verification failed: " + hashError);
                yield break;
            }
        }

        if (!IsEchoVrApk(
                savePath,
                out string apkValidationError,
                out _pendingPackageVersionCode))
        {
            SetStatus("APK verification failed: " + apkValidationError);
            yield break;
        }

        if (manifest != null &&
            !InstallVersionMarker.SavePending(
                manifest,
                downloadedSha256,
                patched,
                trusted: true,
                packageVersionCode: _pendingPackageVersionCode,
                error: out string markerError))
        {
            // The APK and its SHA are valid, so marker failure should be visible
            // but should not prevent installation.
            Debug.LogWarning($"[ApkInstaller] Could not save pending install marker: {markerError}");
        }

        _pendingApkPath = savePath;
        _apkDownloadReady = true;
        SetStatus(_apkOnlyUpdate
            ? "APK update downloaded and verified. Opening Android installer..."
            : "APK downloaded and verified. Waiting for game data download...");
        TryBeginInstall();
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
        SetStatus("Downloads complete. Installing APK...");
        InstallApk(_pendingApkPath);
    }

    private static IEnumerator GetFileSize(string url, Action<long> onComplete)
    {
        using (var request = UnityWebRequest.Head(url))
        {
            yield return request.SendWebRequest();

            if (!long.TryParse(request.GetResponseHeader("Content-Length"), out long size))
                size = 0;

            onComplete?.Invoke(size);
        }
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

    private static bool TryGetApkFileName(string url, out string fileName)
    {
        fileName = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
            return false;

        string candidate = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrEmpty(candidate) ||
            !candidate.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            return false;

        fileName = candidate;
        return true;
    }

    private static bool IsEchoVrApk(
        string apkPath,
        out string error,
        out int packageVersionCode)
    {
        error = null;
        packageVersionCode = 0;
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var packageManager = activity.Call<AndroidJavaObject>("getPackageManager"))
            using (var packageInfo = packageManager.Call<AndroidJavaObject>(
                       "getPackageArchiveInfo", apkPath, 0))
            {
                if (packageInfo == null)
                {
                    error = "Android could not read the downloaded APK.";
                    return false;
                }

                string packageName = packageInfo.Get<string>("packageName");
                packageVersionCode = packageInfo.Get<int>("versionCode");
                if (!string.Equals(
                        packageName,
                        QuestUpdateManager.EchoPackageName,
                        StringComparison.Ordinal))
                {
                    error = $"expected {QuestUpdateManager.EchoPackageName}, got {packageName}.";
                    return false;
                }
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
#else
        if (!File.Exists(apkPath) ||
            !apkPath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
        {
            error = "The downloaded file was not an APK.";
            return false;
        }
#endif
        return true;
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
