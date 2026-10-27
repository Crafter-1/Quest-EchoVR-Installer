using UnityEngine;
using TMPro;
using System.Collections;
using System;
using System.IO;


public class Download : MonoBehaviour
{
    [Header("UI")] public TMP_Text Output;
    public TMP_Text ServerData;
    public AfterInstallController afterInstallController;
    public ApkInstaller apkInstaller;
    public QuestUpdateManager updateManager;

    [Header("Config")]
    [Tooltip("The data download stops after this long without receiving a byte.")]
    public double DataStallSeconds = 60;

    [Tooltip("The data download stops after this long in all (6 hours).")]
    public double DataTotalSeconds = 6 * 3600;

    [Header("File")]
    [Tooltip("The unsigned zip name earlier installers saved. It is no longer downloaded; a copy found on the headset is removed.")]
    public string DownloadFileName = "_data.zip";

    [Tooltip("Where the zip itself is downloaded to. Leave blank to use persistentDataPath.")]
    public string DownloadDirectory;

    [Header("Echo VR target")]
    [Tooltip("The actual Echo VR app's package name - extracted data must land in this app's OBB folder, not our own.")]
    public string EchoPackageName = "com.readyatdawn.r15";

    private const string DataFilePattern = "echovr-data-*.zip";

    // The game-data artifact of the accepted manifest, and the address built from it.
    private NevrArtifact _gameData;
    private string _url;
    private string _title;
    private int _progress;
    private int _total;
    private string _unit;
    private int _stage = 1;
    private const int StageTotal = 4;
    private string _lastError;
    private string _downloadedZipPath;
    private bool _extractionStarted;
    private bool _extractionComplete;
    private bool _pathsInitialized;
    private bool _suppressAutomaticDataDownload;
    private bool _flowBegun;

    public void Start()
    {
        StartCoroutine(BeginWhenManifestAccepted());
    }

    // Nothing downloads, and nothing is offered to the installer, until the signed manifest
    // has been fetched and accepted.
    private IEnumerator BeginWhenManifestAccepted()
    {
        if (!NevrInstallGate.IsOpen)
            InstallerLog.Mark("DOWNLOAD_WAITING_FOR_MANIFEST");

        while (!NevrInstallGate.IsOpen)
            yield return null;

        BeginDataFlow();
    }

    private void BeginDataFlow()
    {
        _flowBegun = true;
        InstallerLog.Mark("DOWNLOAD_START");
        InitializePaths();

        if (_suppressAutomaticDataDownload)
        {
            SetProgress("Updating Echo APK", 2, 1, "");
            return;
        }

        if (!TryResolveGameData(out string resolveError))
        {
            FailDownload("DATA_DOWNLOAD_NOT_STARTED", resolveError);
            return;
        }

        // Recovery path: a completed ZIP survives if the user leaves after
        // installing the APK but before pressing Extract Data.
        if (HasDownloadedDataZip())
        {
            SetProgress("Data downloaded", 2, 1, "");
            _progress = _total;

            if (afterInstallController == null)
                afterInstallController = FindFirstObjectByType<AfterInstallController>();

            if (apkInstaller == null)
                apkInstaller = FindFirstObjectByType<ApkInstaller>();

            afterInstallController?.NotifyDataDownloaded();
            apkInstaller?.NotifyDataDownloadReady();
            return;
        }

        // Storage permission is now handled earlier by StoragePermissionGate,
        // before this screen is ever shown - safe to go straight to the download.
        StartCoroutine(DownloadData());
    }

    // True when a game-data zip that was checked against the signed manifest is on the headset.
    // A zip only gets its final name after its size and SHA-256 matched, so the name is the
    // record. Once the manifest is accepted it must be the one the manifest names now; before
    // that (an existing install being offered Extract) the newest such zip is used.
    public bool HasDownloadedDataZip()
    {
        InitializePaths();
        if (_gameData != null)
        {
            string expected = Path.Combine(DownloadDirectory, _gameData.Name);
            if (TryGetLength(expected) != _gameData.Size)
                return false;

            _downloadedZipPath = expected;
            return true;
        }

        string newest = null;
        DateTime newestTime = DateTime.MinValue;
        try
        {
            foreach (string file in Directory.GetFiles(DownloadDirectory, DataFilePattern))
            {
                if (!NevrManifest.IsGameDataPath("data/" + Path.GetFileName(file)) || TryGetLength(file) < 1)
                    continue;

                DateTime written = File.GetLastWriteTimeUtc(file);
                if (newest == null || written > newestTime)
                {
                    newest = file;
                    newestTime = written;
                }
            }
        }
        catch (Exception exception)
        {
            InstallerLog.Warning("DATA_ZIP_SEARCH_FAILED | " + exception.Message);
        }

        if (newest == null)
            return false;

        _downloadedZipPath = newest;
        return true;
    }

    // Moving to the official build tells the player the game data is downloaded again, so the
    // earlier download is removed and the whole download runs, whatever came before. Returns
    // false, with the reason, if that cannot be done; the caller then changes nothing.
    public bool PrepareForFreshDataDownload(out string error)
    {
        error = null;
        InstallerLog.Mark("FRESH_DATA_DOWNLOAD_PREPARE");
        InitializePaths();
        _suppressAutomaticDataDownload = false;

        // The download screen already ran once: it starts the download again here, so the
        // manifest's game data must be known before anything is removed. If it has not run
        // yet, its own start does all of this.
        if (_flowBegun)
        {
            if (!TryResolveGameData(out string resolveError))
            {
                error = resolveError;
                return false;
            }

            if (!isActiveAndEnabled)
            {
                error = "The download screen is not active.";
                return false;
            }
        }

        try
        {
            RemoveDataFiles(null);
        }
        catch (Exception exception)
        {
            error = "Could not remove the earlier game data download (" + exception.Message + ").";
            return false;
        }

        ClearDataReadyMarker();
        _extractionStarted = false;
        _extractionComplete = false;
        _downloadedZipPath = null;

        if (!_flowBegun)
            return true;

        StartCoroutine(DownloadData());
        return true;
    }

    public void PrepareForApkOnlyUpdateDisplay()
    {
        InstallerLog.Mark("APK_ONLY_UPDATE_DISPLAY");
        _suppressAutomaticDataDownload = true;
    }

    public void FixedUpdate()
    {
        var percent = _total > 0 ? (float)_progress / _total * 100f : 0f;

        Output.text =
            $"{_title}\n" +
            $">>> {_progress}/{_total}{_unit} <<<\n" +
            $"Stage {_stage}/{StageTotal} | {percent:0.0}%" +
            (string.IsNullOrEmpty(_lastError) ? "" : $"\n\n{_lastError}");
    }

    #region Download

    // The game data comes from the signed manifest: the accepted manifest's game-data artifact
    // names the file, its size and its SHA-256. Nothing is chosen from a list of servers.
    private bool TryResolveGameData(out string error)
    {
        error = null;
        NevrReleaseManager manager = FindFirstObjectByType<NevrReleaseManager>(FindObjectsInactive.Include);
        NevrArtifact artifact = manager != null ? manager.CurrentGameData : null;
        if (artifact == null)
        {
            error = manager != null && !string.IsNullOrEmpty(manager.GameDataError)
                ? manager.GameDataError
                : "The Echo update list has not been checked, so the game data cannot be downloaded.";
            ReplaceVersionPlaceholder("unknown");
            return false;
        }

        if (!NevrGameData.TryBuildUrl(artifact, out string url, out error))
        {
            ReplaceVersionPlaceholder("unknown");
            return false;
        }

        _gameData = artifact;
        _url = url;
        ReplaceVersionPlaceholder(artifact.EchoBuild);
        if (ServerData != null)
            ServerData.text = ServerData.text.Replace(
                "Server download location: Loading...", "Server location: " + new Uri(url).Host);

        return true;
    }

    private void ReplaceVersionPlaceholder(string version)
    {
        if (ServerData != null)
            ServerData.text = ServerData.text.Replace("${evrV}", version);
    }

    private IEnumerator DownloadData()
    {
        NevrArtifact artifact = _gameData;
        string url = _url;
        InstallerLog.Mark("DATA_DOWNLOAD_START",
            "url=" + url + " | size=" + artifact.Size + " | sha256=" + artifact.Sha256);
        int totalMegabytes = (int)(artifact.Size / 1048576);
        SetProgress("Downloading required data", 2, totalMegabytes, "MB");
        _lastError = null;

        string path = Path.Combine(DownloadDirectory, artifact.Name);
        string temporaryPath = path + ".part";

        try
        {
            Directory.CreateDirectory(DownloadDirectory);
            RemoveDataFiles(artifact.Name);
        }
        catch (Exception exception)
        {
            FailDownload("DATA_DOWNLOAD_FAILED", exception.Message);
            yield break;
        }

        // The first try carries on from a partial file an earlier run left. If the server
        // ignores the Range request, the second starts the file over.
        string freshSha256 = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            long resumeFrom = attempt == 0
                ? NevrGameData.ResumeOffset(TryGetLength(temporaryPath), artifact.Size)
                : 0;

            if (resumeFrom == artifact.Size)
            {
                // Every byte is already here; only the checks below remain.
                InstallerLog.Mark("DATA_DOWNLOAD_PARTIAL_COMPLETE", "bytes=" + resumeFrom);
                _progress = _total;
                break;
            }

            FileStream target = null;
            try
            {
                if (resumeFrom > 0)
                {
                    target = new FileStream(temporaryPath, FileMode.Open, FileAccess.Write, FileShare.None);
                    target.Seek(resumeFrom, SeekOrigin.Begin);
                }
                else
                {
                    target = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);
                }
            }
            catch (Exception exception)
            {
                target?.Dispose();
                FailDownload("DATA_DOWNLOAD_FAILED", exception.Message);
                yield break;
            }

            if (resumeFrom > 0)
            {
                InstallerLog.Mark("DATA_DOWNLOAD_RESUME", "offset=" + resumeFrom);
                _progress = (int)(resumeFrom / 1048576);
            }

            // The same download rules as the APK: https only, no redirect followed, a stall
            // watchdog and a byte count capped at the manifest's size. There is no request
            // timeout - large files need to run to completion.
            var outcome = new NevrHttpOutcome();
            yield return NevrHttpDownload.Get(
                url, target, artifact.Size, true, 0, DataStallSeconds, DataTotalSeconds, outcome,
                received => _progress = (int)(received / 1048576),
                resumeFrom);

            if (outcome.Failure != null)
            {
                InstallerLog.Error("DATA_DOWNLOAD_FAILED | HTTP " + outcome.ResponseCode + " | " + outcome.Failure);
                Debug.LogError($"[Download] Data download failed: {outcome.Failure} (url: {url})");
                if (outcome.RangeIgnored && attempt == 0)
                {
                    TryDelete(temporaryPath);
                    continue;
                }

                // Keep the bytes known to be the start of the file, so the next run resumes.
                KeepOnlyTrustedBytes(temporaryPath, outcome.TrustedBytes);
                _title = "Download failed";
                _lastError = outcome.Failure + (outcome.TrustedBytes > 0
                    ? " Close and reopen the installer to carry on where it stopped."
                    : "");
                yield break;
            }

            // The hash the download computed covers the whole file only if it started at 0.
            if (resumeFrom == 0)
                freshSha256 = outcome.Sha256;
            break;
        }

        string sha256 = freshSha256;
        if (sha256 == null)
        {
            SetProgress("Checking the downloaded data", 2, totalMegabytes, "MB");
            string hashError = null;
            yield return Sha256Utility.CalculateFile(
                temporaryPath, hash => sha256 = hash, error => hashError = error);
            if (hashError != null)
            {
                FailDownload("DATA_VERIFY_FAILED", "Could not check the downloaded game data: " + hashError);
                yield break;
            }
        }

        // The zip is only given its final name, and only ever extracted, once the size and
        // the SHA-256 are the ones the signed manifest names.
        string problem = NevrGameData.CheckDownloaded(artifact, TryGetLength(temporaryPath), sha256);
        if (problem != null)
        {
            TryDelete(temporaryPath);
            FailDownload("DATA_VERIFY_FAILED", problem);
            yield break;
        }

        InstallerLog.Mark("DATA_VERIFIED",
            "name=" + artifact.Name + " | size=" + artifact.Size + " | sha256=" + sha256);

        if (!TryReplaceDownloadedFile(temporaryPath, path, out string replaceError))
        {
            FailDownload("DATA_STORE_FAILED", "Could not store the game data: " + replaceError);
            yield break;
        }

        SetProgress("Data downloaded", 2, totalMegabytes, "MB");
        _progress = _total;
        _downloadedZipPath = path;
        ClearDataReadyMarker();

        if (afterInstallController == null)
            afterInstallController = FindFirstObjectByType<AfterInstallController>();

        if (apkInstaller == null)
            apkInstaller = FindFirstObjectByType<ApkInstaller>();

        afterInstallController?.NotifyDataDownloaded();
        apkInstaller?.NotifyDataDownloadReady();
    }

    public void ExtractDownloadedData()
    {
        InstallerLog.Mark("DATA_EXTRACTION_REQUESTED");
        if (_extractionStarted)
            return;

        InitializePaths();

        if (string.IsNullOrEmpty(_downloadedZipPath))
            HasDownloadedDataZip();

        if (string.IsNullOrEmpty(_downloadedZipPath) || !File.Exists(_downloadedZipPath))
        {
            _title = "Extraction failed";
            _lastError = "The downloaded data ZIP could not be found.";
            Debug.LogError($"[Download] Missing data ZIP: {_downloadedZipPath}");
            return;
        }

        // Once the manifest is accepted, only the file it names is extracted.
        if (_gameData != null &&
            (Path.GetFileName(_downloadedZipPath) != _gameData.Name ||
             TryGetLength(_downloadedZipPath) != _gameData.Size))
        {
            _title = "Extraction failed";
            _lastError = "The downloaded data ZIP is not the one the update list names.";
            InstallerLog.Error("DATA_EXTRACT_REFUSED | " + _downloadedZipPath);
            return;
        }

        _extractionStarted = true;
        StartCoroutine(_extractionComplete
            ? FinishDataSetup()
            : UnzipData(_downloadedZipPath));
    }

    #endregion

    #region Extraction

    private string GetDataExtractPath()
    {
        // Confirmed from the real installer's source: data goes under
        // Android/media/<package>/files - NOT Android/obb. The zip's internal
        // "_data/..." nesting is expected and must be preserved, not flattened.
        return Path.Combine("/storage/emulated/0/Android/media", EchoPackageName, "files");
    }

    private IEnumerator UnzipData(string zipPath)
    {
        string extractPath;

#if UNITY_ANDROID && !UNITY_EDITOR
        extractPath = GetDataExtractPath();

        try
        {
            if (!Directory.Exists(extractPath))
                Directory.CreateDirectory(extractPath);
        }
        catch (Exception e)
        {
            FailDataSetup($"Could not create Echo data folder ({extractPath}): {e.Message}. " +
                          "Make sure 'All files access' is granted for this app.");
            yield break;
        }
#else
        // Editor fallback - just extract locally so testing in Play Mode doesn't crash
        extractPath = Path.Combine(DownloadDirectory, "Extracted");
        if (!Directory.Exists(extractPath))
            Directory.CreateDirectory(extractPath);
#endif

        using var fs = File.OpenRead(zipPath);
        using var zip = new Unity.SharpZipLib.Zip.ZipFile(fs);

        var fileCount = 0;
        foreach (Unity.SharpZipLib.Zip.ZipEntry entry in zip)
        {
            if (!entry.IsDirectory)
                fileCount++;
        }

        SetProgress("Extracting data", 3, fileCount, " files");

        var processed = 0;

        foreach (Unity.SharpZipLib.Zip.ZipEntry entry in zip)
        {
            if (!TryResolveZipEntryPath(extractPath, entry.Name, out string fullPath))
            {
                FailDataSetup($"The data ZIP contained an unsafe path: {entry.Name}");
                yield break;
            }

            if (entry.IsDirectory)
            {
                try
                {
                    Directory.CreateDirectory(fullPath);
                }
                catch (Exception exception)
                {
                    FailDataSetup($"Could not create {entry.Name}: {exception.Message}");
                    yield break;
                }
                continue;
            }

            string extractionError = null;
            try
            {
                string parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);

                using (var zipStream = zip.GetInputStream(entry))
                using (var output = File.Create(fullPath))
                    zipStream.CopyTo(output);
            }
            catch (Exception exception)
            {
                extractionError = exception.Message;
            }

            if (extractionError != null)
            {
                FailDataSetup($"Could not extract {entry.Name}: {extractionError}");
                yield break;
            }

            processed++;
            _progress = processed;

            yield return null;
        }

        _extractionComplete = true;
        yield return FinishDataSetup();
    }

    private IEnumerator FinishDataSetup()
    {
        InstallerLog.Mark("DATA_SETUP_FINISH_START");
        if (updateManager == null)
            updateManager = FindFirstObjectByType<QuestUpdateManager>();

        if (updateManager == null)
            updateManager = gameObject.AddComponent<QuestUpdateManager>();

        SetProgress("Applying Echo updates", 4, 0, " files");
        bool updateFinished = false;
        bool updateSucceeded = false;
        string updateError = null;

        yield return updateManager.SynchronizeAssets(
            (success, error) =>
            {
                updateSucceeded = success;
                updateError = error;
                updateFinished = true;
            },
            (message, completed, total) =>
            {
                _title = message;
                _stage = 4;
                _progress = completed;
                _total = total;
                _unit = " files";
            });

        if (!updateFinished || !updateSucceeded)
        {
            InstallerLog.Error("DATA_SETUP_UPDATE_FAILED | " + updateError);
            FailDataSetup(updateError ?? "The Echo asset update did not complete.");
            yield break;
        }

        SetProgress("Data ready!", StageTotal, 1, "");
        InstallerLog.Mark("DATA_SETUP_COMPLETE");
        _progress = _total;

#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            File.WriteAllText(
                Path.Combine(GetDataExtractPath(), ".quest_evr_data_ready"),
                _gameData != null ? _gameData.EchoBuild : Path.GetFileName(_downloadedZipPath));
        }
        catch (Exception e)
        {
            // Extraction itself succeeded, so a marker failure must not block completion.
            Debug.LogWarning($"[Download] Could not write the data-ready marker: {e.Message}");
        }
#endif

        if (!updateManager.FinalizePendingInstall(out string markerError))
            Debug.LogWarning($"[Download] Could not write install version marker: {markerError}");

        if (afterInstallController == null)
            afterInstallController = FindFirstObjectByType<AfterInstallController>();

        afterInstallController?.NotifyDataReady();
    }

    #endregion

    #region Utilities

    private void InitializePaths()
    {
        if (_pathsInitialized)
            return;

        if (string.IsNullOrWhiteSpace(DownloadDirectory))
            DownloadDirectory = Application.persistentDataPath;

        _pathsInitialized = true;
    }

    private void SetProgress(string title, int stage, int total, string unit)
    {
        _title = title;
        _stage = stage;
        _progress = 0;
        _total = total;
        _unit = unit;
        _lastError = null;
    }

    private void FailDownload(string logEvent, string message)
    {
        InstallerLog.Error(logEvent + " | " + message);
        _title = "Download failed";
        _lastError = message;
        Debug.LogError($"[Download] {message}");
    }

    // Removes game-data zips this installer saved (and their .part and .backup files), and
    // the unsigned zip earlier installers saved, except the files named `keepName`. Null
    // keeps nothing. Throws if a file cannot be removed.
    private void RemoveDataFiles(string keepName)
    {
        if (!Directory.Exists(DownloadDirectory))
            return;

        if (!string.IsNullOrWhiteSpace(DownloadFileName))
        {
            foreach (string suffix in new[] { "", ".part", ".backup" })
                DeleteIfExists(Path.Combine(DownloadDirectory, DownloadFileName + suffix));
        }

        foreach (string file in Directory.GetFiles(DownloadDirectory, DataFilePattern + "*"))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".part", StringComparison.Ordinal))
                name = name.Substring(0, name.Length - ".part".Length);
            else if (name.EndsWith(".backup", StringComparison.Ordinal))
                name = name.Substring(0, name.Length - ".backup".Length);

            if (!NevrManifest.IsGameDataPath("data/" + name) || name == keepName)
                continue;

            DeleteIfExists(file);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
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

    private static void TryDelete(string path)
    {
        try
        {
            DeleteIfExists(path);
        }
        catch (Exception exception)
        {
            InstallerLog.Warning("DATA_FILE_DELETE_FAILED | " + path + " | " + exception.Message);
        }
    }

    // After a failed try: cut the partial file back to the bytes known to be the start of the
    // game data, so an error page or a wrong answer is never kept in front of a later resume.
    private static void KeepOnlyTrustedBytes(string path, long trusted)
    {
        try
        {
            if (!File.Exists(path))
                return;

            if (trusted <= 0)
            {
                File.Delete(path);
                return;
            }

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                if (stream.Length > trusted)
                    stream.SetLength(trusted);
            }
        }
        catch (Exception exception)
        {
            InstallerLog.Warning("DATA_PARTIAL_TRIM_FAILED | " + exception.Message);
            TryDelete(path);
        }
    }

    private void FailDataSetup(string error)
    {
        InstallerLog.Error("DATA_SETUP_FAILED | " + error);
        _title = "Data setup failed";
        _lastError = error;
        _extractionStarted = false;
        Debug.LogError("[Download] " + error);

        if (afterInstallController == null)
            afterInstallController = FindFirstObjectByType<AfterInstallController>();

        afterInstallController?.NotifyDataSetupFailed(error);
    }

    private static bool TryResolveZipEntryPath(
        string extractionRoot,
        string entryName,
        out string resolved)
    {
        resolved = null;
        string candidateEntry = (entryName ?? string.Empty)
            .Replace('\\', '/')
            .TrimEnd('/');

        if (candidateEntry.Length == 0)
            return false;

        if (!QuestUpdateManifest.TryNormalizeRelativePath(
                candidateEntry,
                out string normalized))
            return false;

        string fullRoot = Path.GetFullPath(extractionRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(
            fullRoot,
            normalized.Replace('/', Path.DirectorySeparatorChar)));

        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        resolved = fullPath;
        return true;
    }

    private static bool TryReplaceDownloadedFile(
        string source,
        string destination,
        out string error)
    {
        error = null;
        string backup = destination + ".backup";

        try
        {
            if (File.Exists(backup))
                File.Delete(backup);
            if (File.Exists(destination))
                File.Move(destination, backup);

            try
            {
                File.Move(source, destination);
                if (File.Exists(backup))
                    File.Delete(backup);
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

    private void ClearDataReadyMarker()
    {
        PlayerPrefs.SetInt("EchoDataExtractionComplete", 0);
        PlayerPrefs.Save();

#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            string marker = Path.Combine(
                GetDataExtractPath(),
                ".quest_evr_data_ready");
            if (File.Exists(marker))
                File.Delete(marker);
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[Download] Could not clear old data marker: {exception.Message}");
        }
#endif
    }

    #endregion
}
