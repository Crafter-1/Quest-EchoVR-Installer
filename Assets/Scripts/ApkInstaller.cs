using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.IO;

public class ApkInstaller : MonoBehaviour
{
    [Header("Optional UI references for progress display")]
    public UnityEngine.UI.Slider progressBar;      
    public TMPro.TMP_Text statusText;              

    private string _apkFileName = "install.apk";

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

        SetStatus("Downloading APK...");

        using (UnityWebRequest req = UnityWebRequest.Get(url))
        {
            req.downloadHandler = new DownloadHandlerFile(savePath);

            var operation = req.SendWebRequest();

            while (!operation.isDone)
            {
                UpdateProgress(req.downloadProgress);
                yield return null;
            }

            if (req.result != UnityWebRequest.Result.Success)
            {
                SetStatus("Download failed: " + req.error);
                yield break;
            }
        }

        SetStatus("Download complete. Installing...");
        InstallApk(savePath);
    }

    private void UpdateProgress(float progress)
    {
        if (progressBar != null)
            progressBar.value = progress;

        SetStatus($"Downloading APK... {(progress * 100f):F0}%");
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
