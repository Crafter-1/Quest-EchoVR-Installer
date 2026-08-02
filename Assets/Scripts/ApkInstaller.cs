using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.IO;

public class ApkInstaller : MonoBehaviour
{
    [Header("UI - matches Download.cs style")]
    public TMPro.TMP_Text statusText;             

    private string _apkFileName = "install.apk";
    private long _downloadedBytes;
    private long _totalBytes;

    public void DownloadAndInstallFromUrl(string url)
    {
        StartCoroutine(DownloadAndInstall(url));
    }

    private IEnumerator DownloadAndInstall(string url)
    {
        // Try to keep the original filename from the URL if possible
        string fileNameFromUrl = Path.GetFileName(new System.Uri(url).LocalPath);
        if (!string.IsNullOrEmpty(fileNameFromUrl) && fileNameFromUrl.EndsWith(".apk"))
        {
            _apkFileName = fileNameFromUrl;
        }

        string savePath = Path.Combine(Application.persistentDataPath, _apkFileName);

        // Get file size first, same approach as Download.cs's GetFileSize
        yield return GetFileSize(url, size => _totalBytes = size);

        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            req.downloadHandler = new DownloadHandlerFile(savePath);

            var operation = req.SendWebRequest();

            while (!operation.isDone)
            {
                _downloadedBytes = (long)req.downloadedBytes;
                UpdateStatusText();
                yield return null;
            }

            if (req.result != UnityWebRequest.Result.Success)
            {
                SetStatus($"APK download failed: {req.error}");
                yield break;
            }
        }

        SetStatus("APK downloaded. Installing...");
        InstallApk(savePath);
    }

    private static IEnumerator GetFileSize(string url, System.Action<long> onComplete)
    {
        using var request = UnityWebRequest.Head(url);
        yield return request.SendWebRequest();

        if (!long.TryParse(request.GetResponseHeader("Content-Length"), out var size))
            size = 0;

        onComplete?.Invoke(size);
    }

    private void UpdateStatusText()
    {
        var downloadedMb = _downloadedBytes / 1048576f;
        var totalMb = _totalBytes / 1048576f;
        var percent = _totalBytes > 0 ? (_downloadedBytes / (float)_totalBytes) * 100f : 0f;

        SetStatus(
            $"Downloading APK\n" +
            $">>> {downloadedMb:0.0}/{totalMb:0.0}MB <<<\n" +
            $"{percent:0.0}%"
        );
    }

    private void SetStatus(string message)
    {
        if (statusText != null)
            statusText.text = message;

        Debug.Log("[ApkInstaller] " + message);
    }

    private void InstallApk(string apkPath)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaClass playerClass = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
        AndroidJavaObject activity = playerClass.GetStatic<AndroidJavaObject>("currentActivity");
        AndroidJavaObject context = activity.Call<AndroidJavaObject>("getApplicationContext");

        AndroidJavaClass fileProviderClass = new AndroidJavaClass("androidx.core.content.FileProvider");
        AndroidJavaObject javaFile = new AndroidJavaObject("java.io.File", apkPath);
        string authority = Application.identifier + ".fileprovider";
        AndroidJavaObject uri = fileProviderClass.CallStatic<AndroidJavaObject>(
            "getUriForFile", context, authority, javaFile);

        AndroidJavaObject intent = new AndroidJavaObject("android.content.Intent", "android.intent.action.VIEW");
        intent.Call<AndroidJavaObject>("setDataAndType", uri, "application/vnd.android.package-archive");
        intent.Call<AndroidJavaObject>("addFlags", 1);          // FLAG_GRANT_READ_URI_PERMISSION
        intent.Call<AndroidJavaObject>("addFlags", 0x10000000); // FLAG_ACTIVITY_NEW_TASK

        activity.Call("startActivity", intent);
#else
        SetStatus("Install intent only runs on-device (Android build), not in the Editor.");
#endif
    }
}