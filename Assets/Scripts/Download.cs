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

    [Header("Server")] public string[] ServerLocations =
        { "http://evr.echo.taxi", "http://files.echovr.de"};

    // Unused now!
    // public string FallbackServer = "http://echo.avagoosa.com/main.4987570.com.readyatdawn.r15.zip";

    [Header("Config")] public int TestTimeMs = 2000;
    public int TimeoutMs = 2000; // only used for the quick server speed test now
    public int DownloadThreshold = 200;
    public int BatchSize = 5;

    [Header("File")] public string EvrVersion = "4987570";
    public string DownloadFileName = "_data.zip";
    // public string DownloadFileName = "main.${EvrV}.com.readyatdawn.r15.zip";

    [Tooltip("Leave blank to download to the ASD.")]
    public string DownloadDirectory;

    private string _url;
    private string _title;
    private int _progress;
    private int _total;
    private string _unit;
    private int _stage = 1;
    private const int StageTotal = 3;
    private string _lastError;

    public void Start()
    {
        DownloadFileName = DownloadFileName.Replace("${EvrV}", EvrVersion);
        ServerData.text = ServerData.text.Replace("${evrV}", EvrVersion);
        if (DownloadDirectory == "") DownloadDirectory = Application.persistentDataPath;

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
        // I know, this is the dumbest thing ever. I could use proper regex, but I'm lazy and this works for the use case.
        ServerData.text = ServerData.text.Replace("Server download location: Loading...", $"Server location: {server}");
        // ServerData.text = ServerData.text.Replace("${svLoc}", server);
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
        request.timeout = 0; // fuck the timeout.
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

        StartCoroutine(UnzipData(path));
    }

    private UnityWebRequest CreateTestRequest(UnityWebRequest request)
    {
        request.timeout = TimeoutMs / 1000;
        return request;
    }

    #endregion

    #region Extraction

    private IEnumerator UnzipData(string zipPath)
{
    var extractPath = Path.Combine(DownloadDirectory, "Extracted");

    if (!Directory.Exists(extractPath))
        Directory.CreateDirectory(extractPath);

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
