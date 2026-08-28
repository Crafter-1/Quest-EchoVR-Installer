using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public sealed class InstallVersionMarkerData
{
    public string BaseApk;
    public string BaseSha256;
    public string InstalledSha256;
    public bool Patched;
    public string InstalledAt;
    public string InstallerVersion;
    public bool Trusted;
    public int PackageVersionCode;
}

public static class InstallVersionMarker
{
    public const string MarkerFileName = ".echo_installer_version";
    private const string PendingFileName = "echo_pending_install";

    private static string PendingPath =>
        Path.Combine(Application.persistentDataPath, PendingFileName);

    public static bool SavePending(
        QuestUpdateManifest manifest,
        string installedSha256,
        bool patched,
        bool trusted,
        int packageVersionCode,
        out string error)
    {
        var data = new InstallVersionMarkerData
        {
            BaseApk = manifest.BaseApkFileName,
            BaseSha256 = manifest.BaseApkSha256,
            InstalledSha256 = installedSha256,
            Patched = patched,
            InstalledAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            InstallerVersion = string.IsNullOrWhiteSpace(Application.version)
                ? "unknown"
                : Application.version,
            Trusted = trusted,
            PackageVersionCode = packageVersionCode
        };

        return WriteAtomic(PendingPath, Serialize(data, includeTrusted: true), out error);
    }

    public static bool FinalizePending(
        string targetRoot,
        int installedPackageVersionCode,
        out string error)
    {
        error = null;
        if (!TryRead(PendingPath, out InstallVersionMarkerData data))
            return true;

        // This fallback is retained for any future caller that explicitly marks
        // an install as untrusted. Normal legacy and patched flows follow the
        // supplied format and create the final marker.
        if (!data.Trusted)
            return true;

        if (data.PackageVersionCode > 0 &&
            installedPackageVersionCode != data.PackageVersionCode)
        {
            error = "The installed Echo APK does not match the downloaded update.";
            return false;
        }

        data.InstalledAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

        string markerPath = Path.Combine(targetRoot, MarkerFileName);
        if (!WriteAtomic(markerPath, Serialize(data, includeTrusted: false), out error))
            return false;

        try
        {
            File.Delete(PendingPath);
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[InstallVersionMarker] Could not remove pending marker: {exception.Message}");
        }

        return true;
    }

    public static bool TryReadFinal(string targetRoot, out InstallVersionMarkerData data)
    {
        return TryRead(Path.Combine(targetRoot, MarkerFileName), out data);
    }

    public static bool TryReadPending(out InstallVersionMarkerData data)
    {
        return TryRead(PendingPath, out data);
    }

    private static bool TryRead(string path, out InstallVersionMarkerData data)
    {
        data = null;
        if (!File.Exists(path))
            return false;

        try
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rawLine in File.ReadAllLines(path))
            {
                int equals = rawLine.IndexOf('=');
                if (equals <= 0)
                    continue;

                values[rawLine.Substring(0, equals).Trim()] =
                    rawLine.Substring(equals + 1).Trim();
            }

            if (!values.TryGetValue("base_apk", out string baseApk) ||
                !values.TryGetValue("base_sha256", out string baseSha) ||
                !values.TryGetValue("installed_sha256", out string installedSha) ||
                !QuestUpdateManifest.IsSha256(baseSha) ||
                !QuestUpdateManifest.IsSha256(installedSha))
                return false;

            data = new InstallVersionMarkerData
            {
                BaseApk = baseApk,
                BaseSha256 = baseSha.ToLowerInvariant(),
                InstalledSha256 = installedSha.ToLowerInvariant(),
                Patched = values.TryGetValue("patched", out string patched) &&
                          bool.TryParse(patched, out bool isPatched) && isPatched,
                InstalledAt = values.TryGetValue("installed_at", out string installedAt)
                    ? installedAt : string.Empty,
                InstallerVersion = values.TryGetValue("installer_version", out string version)
                    ? version : string.Empty,
                Trusted = !values.TryGetValue("trusted", out string trusted) ||
                          !bool.TryParse(trusted, out bool isTrusted) || isTrusted,
                PackageVersionCode = values.TryGetValue(
                                         "package_version_code",
                                         out string packageVersion) &&
                                     int.TryParse(packageVersion, out int parsedVersion)
                    ? parsedVersion
                    : 0
            };
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[InstallVersionMarker] Could not read marker: {exception.Message}");
            return false;
        }
    }

    private static string Serialize(InstallVersionMarkerData data, bool includeTrusted)
    {
        string text =
            "version=1\n" +
            $"base_apk={Sanitize(data.BaseApk)}\n" +
            $"base_sha256={data.BaseSha256}\n" +
            $"installed_sha256={data.InstalledSha256}\n" +
            $"patched={data.Patched.ToString().ToLowerInvariant()}\n" +
            $"installed_at={Sanitize(data.InstalledAt)}\n" +
            $"installer_version={Sanitize(data.InstallerVersion)}\n";

        if (includeTrusted)
        {
            text += $"trusted={data.Trusted.ToString().ToLowerInvariant()}\n";
            text += $"package_version_code={data.PackageVersionCode}\n";
        }

        return text;
    }

    private static string Sanitize(string value)
    {
        return (value ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
    }

    private static bool WriteAtomic(string path, string contents, out string error)
    {
        error = null;
        string temporaryPath = path + ".tmp";
        string backupPath = path + ".backup";

        try
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(temporaryPath, contents);
            if (File.Exists(backupPath))
                File.Delete(backupPath);
            if (File.Exists(path))
                File.Move(path, backupPath);

            try
            {
                File.Move(temporaryPath, path);
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
                return true;
            }
            catch
            {
                if (!File.Exists(path) && File.Exists(backupPath))
                    File.Move(backupPath, path);
                throw;
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Preserve the original failure.
            }

            return false;
        }
    }
}
