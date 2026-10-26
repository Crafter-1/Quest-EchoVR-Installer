using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class QuestUpdateManager : MonoBehaviour
{
    public const string EchoPackageName = "com.readyatdawn.r15";
    public const string DefaultManifestUrl = NevrGameDataPolicy.ManifestUrl;

    // Kept so the scene's serialized value stays valid. It is not used: the game-data list
    // is fetched from the pinned address only (NevrGameDataPolicy).
    [Header("Update manifest (address is pinned in code)")]
    public string manifestUrl = DefaultManifestUrl;

    private const double AssetStallSeconds = 30;
    private const double AssetTotalSeconds = 1800;
    private const double ManifestStallSeconds = 15;

    public QuestUpdateManifest CurrentManifest { get; private set; }
    public string TargetRoot => GetTargetRoot();

    private bool _manifestRequestRunning;
    private readonly List<Action<bool, string>> _manifestCallbacks =
        new List<Action<bool, string>>();

    public void EnsureManifest(Action<bool, string> onComplete)
    {
        InstallerLog.Mark("MANIFEST_REQUEST", "url=" + NevrGameDataPolicy.ManifestUrl);
        if (CurrentManifest != null)
        {
            onComplete?.Invoke(true, null);
            return;
        }

        if (onComplete != null)
            _manifestCallbacks.Add(onComplete);

        if (!_manifestRequestRunning)
            StartCoroutine(FetchManifest());
    }

    public void RefreshManifest(Action<bool, string> onComplete)
    {
        CurrentManifest = null;
        EnsureManifest(onComplete);
    }

    public IEnumerator SynchronizeAssets(
        Action<bool, string> onComplete,
        Action<string, int, int> onProgress = null)
    {
        InstallerLog.Mark("ASSET_SYNC_START");
        if (CurrentManifest == null)
        {
            bool manifestDone = false;
            bool manifestSucceeded = false;
            string manifestError = null;
            EnsureManifest((success, error) =>
            {
                manifestSucceeded = success;
                manifestError = error;
                manifestDone = true;
            });

            yield return new WaitUntil(() => manifestDone);
            if (!manifestSucceeded)
            {
                onComplete?.Invoke(false, manifestError);
                yield break;
            }
        }

        string root = GetTargetRoot();
        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception exception)
        {
            onComplete?.Invoke(false, $"Could not access Echo's data folder: {exception.Message}");
            yield break;
        }

        int total = CurrentManifest.Entries.Count;
        long downloadedTotal = 0;
        for (int index = 0; index < total; index++)
        {
            QuestUpdateEntry entry = CurrentManifest.Entries[index];
            onProgress?.Invoke(
                entry.Operation == QuestUpdateOperation.Add
                    ? $"Checking {entry.AssetPath}"
                    : $"Removing {entry.AssetPath}",
                index,
                total);

            if (!TryResolveTargetPath(root, entry.AssetPath, out string destination) ||
                !NevrGameDataPolicy.IsAllowedPath(entry.AssetPath, out _))
            {
                InstallerLog.Error("ASSET_PATH_REFUSED | " + entry.AssetPath);
                onComplete?.Invoke(false, $"The manifest contained an unsafe path: {entry.AssetPath}");
                yield break;
            }

            if (entry.Operation == QuestUpdateOperation.Delete)
            {
                try
                {
                    if (File.Exists(destination))
                        File.Delete(destination);
                }
                catch (Exception exception)
                {
                    onComplete?.Invoke(false, $"Could not delete {entry.AssetPath}: {exception.Message}");
                    yield break;
                }

                continue;
            }

            bool destinationMatches = false;
            if (File.Exists(destination))
            {
                string localHash = null;
                string hashError = null;
                yield return Sha256Utility.CalculateFile(
                    destination,
                    hash => localHash = hash,
                    error => hashError = error);

                if (hashError != null)
                {
                    onComplete?.Invoke(false, $"Could not verify {entry.AssetPath}: {hashError}");
                    yield break;
                }

                destinationMatches = string.Equals(
                    localHash, entry.Sha256, StringComparison.OrdinalIgnoreCase);
            }

            if (destinationMatches)
                continue;

            string temporaryPath = destination + ".echo-part";
            try
            {
                string parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);

                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (Exception exception)
            {
                onComplete?.Invoke(false, $"Could not prepare {entry.AssetPath}: {exception.Message}");
                yield break;
            }

            FileStream assetStream;
            try
            {
                assetStream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
            }
            catch (Exception exception)
            {
                onComplete?.Invoke(false, $"Could not prepare {entry.AssetPath}: {exception.Message}");
                yield break;
            }

            long allowed = Math.Min(
                NevrGameDataPolicy.MaxAssetBytes, NevrGameDataPolicy.MaxTotalBytes - downloadedTotal);
            if (allowed <= 0)
            {
                assetStream.Dispose();
                TryDelete(temporaryPath);
                onComplete?.Invoke(false, "The game-data update is larger than the installer allows, so it stopped.");
                yield break;
            }

            var outcome = new NevrHttpOutcome();
            yield return NevrHttpDownload.Get(
                NevrGameDataPolicy.BuildAssetUrl(entry.AssetPath), assetStream, allowed, false, 0,
                AssetStallSeconds, AssetTotalSeconds, outcome);

            if (outcome.Failure != null)
            {
                InstallerLog.Error("ASSET_DOWNLOAD_FAILED | " + entry.AssetPath + " | http=" +
                                   outcome.ResponseCode + " | " + outcome.Failure);
                TryDelete(temporaryPath);
                onComplete?.Invoke(false, $"Could not download {entry.AssetPath}: {outcome.Failure}");
                yield break;
            }

            downloadedTotal += outcome.Received;

            string downloadedHash = null;
            string downloadedHashError = null;
            yield return Sha256Utility.CalculateFile(
                temporaryPath,
                hash => downloadedHash = hash,
                error => downloadedHashError = error);

            if (downloadedHashError != null ||
                !string.Equals(downloadedHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(temporaryPath);
                onComplete?.Invoke(false,
                    downloadedHashError != null
                        ? $"Could not verify {entry.AssetPath}: {downloadedHashError}"
                        : $"SHA256 check failed for {entry.AssetPath}.");
                yield break;
            }

            if (!TryReplaceFile(temporaryPath, destination, out string replaceError))
            {
                TryDelete(temporaryPath);
                onComplete?.Invoke(false, $"Could not install {entry.AssetPath}: {replaceError}");
                yield break;
            }
        }

        onProgress?.Invoke("Echo updates ready", total, total);
        InstallerLog.Mark("ASSET_SYNC_COMPLETE", "entries=" + total);
        onComplete?.Invoke(true, null);
    }

    public bool FinalizePendingInstall(out string error)
    {
        return InstallVersionMarker.FinalizePending(
            GetTargetRoot(),
            GetInstalledEchoVersionCode(),
            out error);
    }

    public bool TryGetInstalledMarker(out InstallVersionMarkerData marker)
    {
        return InstallVersionMarker.TryReadFinal(GetTargetRoot(), out marker);
    }

    public static int GetInstalledEchoVersionCode()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var packageManager = activity.Call<AndroidJavaObject>("getPackageManager"))
            using (var packageInfo = packageManager.Call<AndroidJavaObject>(
                       "getPackageInfo", EchoPackageName, 0))
                return packageInfo == null ? 0 : packageInfo.Get<int>("versionCode");
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[QuestUpdateManager] Could not read Echo version code: {exception.Message}");
            return 0;
        }
#else
        return 0;
#endif
    }

    private IEnumerator FetchManifest()
    {
        InstallerLog.Mark("MANIFEST_FETCH_START", "url=" + manifestUrl);
        _manifestRequestRunning = true;
        bool success = false;
        string error = null;

        var buffer = new MemoryStream();
        var outcome = new NevrHttpOutcome();
        yield return NevrHttpDownload.Get(
            NevrGameDataPolicy.ManifestUrl, buffer, NevrGameDataPolicy.MaxManifestBytes, false, 20,
            ManifestStallSeconds, 20, outcome);

        if (outcome.Failure != null)
        {
            InstallerLog.Error("MANIFEST_FETCH_FAILED | http=" + outcome.ResponseCode + " | " + outcome.Failure);
            error = "Could not download the Echo update manifest: " + outcome.Failure;
        }
        else if (!QuestUpdateManifest.TryParse(
                     System.Text.Encoding.UTF8.GetString(buffer.ToArray()),
                     out QuestUpdateManifest parsed,
                     out error))
        {
            error = "Invalid Echo update manifest: " + error;
        }
        else if ((error = NevrGameDataPolicy.Check(parsed)) != null)
        {
            InstallerLog.Error("MANIFEST_REFUSED | " + error);
        }
        else
        {
            CurrentManifest = parsed;
            success = true;
            InstallerLog.Mark("MANIFEST_FETCH_SUCCESS", "entries=" + parsed.Entries.Count);
        }

        _manifestRequestRunning = false;
        Action<bool, string>[] callbacks = _manifestCallbacks.ToArray();
        _manifestCallbacks.Clear();
        foreach (Action<bool, string> callback in callbacks)
            callback?.Invoke(success, error);
    }

    private static string GetTargetRoot()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return Path.Combine("/storage/emulated/0/Android/media", EchoPackageName);
#else
        return Path.Combine(Application.persistentDataPath, "EchoUpdateTest", EchoPackageName);
#endif
    }

    private static bool TryResolveTargetPath(string root, string assetPath, out string resolved)
    {
        resolved = null;
        if (!QuestUpdateManifest.TryNormalizeRelativePath(assetPath, out string normalized))
            return false;

        string fullRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(
            fullRoot,
            normalized.Replace('/', Path.DirectorySeparatorChar)));

        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        resolved = candidate;
        return true;
    }

    private static bool TryReplaceFile(string source, string destination, out string error)
    {
        error = null;
        string backup = destination + ".echo-backup";

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
}
