using System;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Central installer diagnostics. Existing Unity Debug.Log calls are captured
/// automatically, while installer state changes can use Log/Mark explicitly.
/// </summary>
public static class InstallerLog
{
    private const string FileName = "installer.log";
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private static readonly object Sync = new object();
    private static bool _initialized;

    public static string PathOnDevice =>
        System.IO.Path.Combine(Application.persistentDataPath, FileName);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void InitializeOnLoad()
    {
        Initialize();
    }

    public static void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;
        Application.logMessageReceivedThreaded += CaptureUnityMessage;

        try
        {
            string directory = System.IO.Path.GetDirectoryName(PathOnDevice);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            RotateIfNeeded();
            Write("INFO", "somewhere in the heavens, bugs are waiting. Installer logging started");
            Write("INFO", "App version=" +
                (string.IsNullOrWhiteSpace(Application.version) ? "unknown" : Application.version));
        }
        catch (Exception exception)
        {
            UnityEngine.Debug.LogWarning("[InstallerLog] Could not initialize file logging: " + exception.Message);
        }
    }

    public static void Log(string message) => Write("INFO", message);
    public static void Warning(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Exception(string context, System.Exception exception)
    {
        Write("ERROR", context + ": " + exception);
    }

    public static void Mark(string eventName, string details = null)
    {
        Write("EVENT", string.IsNullOrEmpty(details) ? eventName : eventName + " | " + details);
    }

    public static bool TryRead(out string contents, out string error)
    {
        contents = null;
        error = null;
        try
        {
            if (!File.Exists(PathOnDevice))
            {
                error = "The installer log does not exist yet.";
                return false;
            }

            contents = File.ReadAllText(PathOnDevice);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public static void Clear()
    {
        try
        {
            lock (Sync)
            {
                if (File.Exists(PathOnDevice))
                    File.Delete(PathOnDevice);
            }
            Log("Log cleared by request.");
        }
        catch (Exception exception)
        {
            UnityEngine.Debug.LogWarning("[InstallerLog] Could not clear log: " + exception.Message);
        }
    }

    private static void CaptureUnityMessage(string condition, string stackTrace, LogType type)
    {
        string level;
        switch (type)
        {
            case LogType.Error:
            case LogType.Assert:
            case LogType.Exception:
                level = "UNITY-ERROR";
                break;
            case LogType.Warning:
                level = "UNITY-WARN";
                break;
            default:
                level = "UNITY";
                break;
        }

        Write(level, condition +
            (string.IsNullOrEmpty(stackTrace) ? string.Empty : "\n" + stackTrace));
    }

    private static void Write(string level, string message)
    {
        if (!_initialized)
            Initialize();

        string line = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") +
                      " [" + level + "] " + (message ?? string.Empty) + Environment.NewLine;
        try
        {
            lock (Sync)
            {
                string directory = System.IO.Path.GetDirectoryName(PathOnDevice);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.AppendAllText(PathOnDevice, line, Encoding.UTF8);
                RotateIfNeeded();
            }
        }
        catch
        {
            // Diagnostics must never interrupt installation.
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            FileInfo info = new FileInfo(PathOnDevice);
            if (!info.Exists || info.Length <= MaxLogBytes)
                return;

            string backupPath = PathOnDevice + ".old";
            if (File.Exists(backupPath))
                File.Delete(backupPath);
            File.Move(PathOnDevice, backupPath);
        }
        catch
        {
            // Rotation is best effort only.
        }
    }
}
