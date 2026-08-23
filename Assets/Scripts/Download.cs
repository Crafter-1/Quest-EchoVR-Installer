using UnityEngine;
using TMPro;
using UnityEngine.Networking;
using System.Collections;
using System;
using System.IO;


public class Download : MonoBehaviour
{
    [Header("UI")] public TMP_Text Output;
    public TMP_Text ServerData;
    public AfterInstallController afterInstallController;
    public ApkInstaller apkInstaller;

    [Header("Server")] public string[] ServerLocations =
        { "http://evr.echo.taxi", "http://files.echovr.de"};

    [Header("Config")] public int TestTimeMs = 2000;
    public int TimeoutMs = 2000; // only used for the quick server speed test
    public int DownloadThreshold = 200;
    public int BatchSize = 5;

    [Header("File")] public string EvrVersion = "4987570";
    public string DownloadFileName = "_data.zip";

    [Tooltip("Where the zip itself is downloaded to. Leave blank to use persistentDataPath.")]
    public string DownloadDirectory;

    [Header("Echo VR target")]
    [Tooltip("The actual Echo VR app's package name - extracted data must land in this app's OBB folder, not our own.")]
    public string EchoPackageName = "com.readyatdawn.r15";

    private string _url;
    private string _title;
    private int _progress;
    private int _total;
    private string _unit;
    private int _stage = 1;
    private const int StageTotal = 3;
    private string _lastError;
    private string _downloadedZipPath;
    private bool _extractionStarted;

    public void Start()
    {
        DownloadFileName = DownloadFileName.Replace("${EvrV}", EvrVersion);
        ServerData.text = ServerData.text.Replace("${evrV}", EvrVersion);
        if (DownloadDirectory == "") DownloadDirectory = Application.persistentDataPath;

        // Storage permission is now handled earlier by StoragePermissionGate,
        // before this screen is ever shown - safe to go straight to the server test.
        StartCoroutine(TestServers());
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

    #region Server Selection

    private IEnumerator TestServers()
    {
        SetProgress("Testing servers", 1, ServerLocations.Length, "");

        var bestSpeed = 0f;
        var bestServer = ServerLocations[0];

        for (var i = 0; i < ServerLocations.Length; i += BatchSize)
        {
            var batchCount = Mathf.Min(BatchSize, ServerLocations.Length - i);
            var completed = 0;
            var foundFastServer = false;

            for (var j = 0; j < batchCount; j++)
            {
                var index = i + j;
                _progress = index;

                StartCoroutine(TestServer(ServerLocations[index], speed =>
                {
                    if (speed > bestSpeed)
                    {
                        bestSpeed = speed;
                        bestServer = ServerLocations[index];
                    }

                    if (speed >= DownloadThreshold)
                        foundFastServer = true;

                    completed++;
                }));
            }

            yield return new WaitUntil(() => completed >= batchCount || foundFastServer);

            if (foundFastServer)
                break;
        }

        SetServer(bestServer);
        StartCoroutine(DownloadData());
    }

    private IEnumerator TestServer(string server, Action<float> onComplete)
    {
        var url = BuildUrl(server);

        using var request = CreateTestRequest(UnityWebRequest.Get(url));

        var start = Time.time;
        request.SendWebRequest();

        while (!request.isDone)
        {
            if ((Time.time - start) * 1000f > TestTimeMs)
            {
                request.Abort();
                break;
            }

            yield return null;
        }

        var speed = 0f;

        if (request.result == UnityWebRequest.Result.Success)
        {
            var duration = Time.time - start;
            if (duration > 0f)
                speed = (request.downloadedBytes / 1024f) / duration;
        }

        onComplete?.Invoke(speed);
    }

    private void SetServer(string server)
    {
        ServerData.text = ServerData.text.Replace("Server download location: Loading...", $"Server location: {server}");
        _url = BuildUrl(server);
    }

    private string BuildUrl(string server) => $"{server}/{DownloadFileName}";

    #endregion

    #region Download

    private IEnumerator DownloadData()
    {
        var fileSize = 0;
        yield return GetFileSize(_url, size => fileSize = size);

        SetProgress("Downloading required data", 2, fileSize / 1048576, "MB");
        _lastError = null;

        using var request = UnityWebRequest.Get(_url);
        request.timeout = 0; // no timeout - large files need to run to completion
        request.SendWebRequest();

        while (!request.isDone)
        {
            if (fileSize > 0)
                _progress = (int)(request.downloadedBytes / 1048576);

            yield return null;
        }

        if (request.result != UnityWebRequest.Result.Success)
        {
            _title = "Download failed";
            _lastError = $"{request.result}: {request.error}";
            Debug.LogError($"[Download] Data download failed: {request.result} - {request.error} (url: {_url})");
            yield break;
        }

        var path = Path.Combine(DownloadDirectory, DownloadFileName);
        File.WriteAllBytes(path, request.downloadHandler.data);

        _progress = _total;
        _downloadedZipPath = path;
        _title = "Data downloaded";

        if (afterInstallController == null)
            afterInstallController = FindObjectOfType<AfterInstallController>();

        if (apkInstaller == null)
            apkInstaller = FindObjectOfType<ApkInstaller>();

        afterInstallController?.NotifyDataDownloaded();
        apkInstaller?.NotifyDataDownloadReady();
    }

    public void ExtractDownloadedData()
    {
        if (_extractionStarted)
            return;

        if (string.IsNullOrEmpty(_downloadedZipPath))
            _downloadedZipPath = Path.Combine(DownloadDirectory, DownloadFileName);

        if (!File.Exists(_downloadedZipPath))
        {
            _title = "Extraction failed";
            _lastError = "The downloaded data ZIP could not be found.";
            Debug.LogError($"[Download] Missing data ZIP: {_downloadedZipPath}");
            return;
        }

        _extractionStarted = true;
        StartCoroutine(UnzipData(_downloadedZipPath));
    }

    private UnityWebRequest CreateTestRequest(UnityWebRequest request)
    {
        request.timeout = TimeoutMs / 1000;
        return request;
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
            _title = "Extraction failed";
            _lastError = $"Could not create OBB folder ({extractPath}): {e.Message}. " +
                          "Make sure 'All files access' is granted for this app.";
            Debug.LogError($"[Download] {_lastError}");
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
            var fullPath = Path.Combine(extractPath, entry.Name);

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(fullPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            using var zipStream = zip.GetInputStream(entry);
            using var output = File.Create(fullPath);

            zipStream.CopyTo(output);

            processed++;
            _progress = processed;

            yield return null;
        }

        SetProgress("Data ready!", StageTotal, processed, " files");

#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            File.WriteAllText(Path.Combine(extractPath, ".quest_evr_data_ready"), EvrVersion);
        }
        catch (Exception e)
        {
            // Extraction itself succeeded, so a marker failure must not block completion.
            Debug.LogWarning($"[Download] Could not write the data-ready marker: {e.Message}");
        }
#endif

        if (afterInstallController == null)
            afterInstallController = FindObjectOfType<AfterInstallController>();

        afterInstallController?.NotifyDataReady();
    }

    #endregion

    #region Utilities

    private void SetProgress(string title, int stage, int total, string unit)
    {
        _title = title;
        _stage = stage;
        _progress = 0;
        _total = total;
        _unit = unit;
        _lastError = null;
    }

    private static IEnumerator GetFileSize(string url, Action<int> onComplete)
    {
        using var request = UnityWebRequest.Head(url);
        yield return request.SendWebRequest();

        if (!int.TryParse(request.GetResponseHeader("Content-Length"), out var size))
            size = 0;

        onComplete?.Invoke(size);
    }

    #endregion
}
