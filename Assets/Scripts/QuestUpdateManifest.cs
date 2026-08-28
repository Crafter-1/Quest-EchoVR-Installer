using System;
using System.Collections.Generic;

public enum QuestUpdateOperation
{
    Add,
    Delete
}

public sealed class QuestUpdateEntry
{
    public QuestUpdateOperation Operation { get; }
    public string AssetPath { get; }
    public string Sha256 { get; }

    public QuestUpdateEntry(QuestUpdateOperation operation, string assetPath, string sha256)
    {
        Operation = operation;
        AssetPath = assetPath;
        Sha256 = sha256;
    }
}

public sealed class QuestUpdateManifest
{
    public string BaseUrl { get; private set; }
    public string BaseApkFileName { get; private set; }
    public string BaseApkSha256 { get; private set; }
    public string TargetPath { get; private set; }
    public IReadOnlyList<QuestUpdateEntry> Entries => _entries;

    private readonly List<QuestUpdateEntry> _entries = new List<QuestUpdateEntry>();

    public static bool TryParse(string text, out QuestUpdateManifest manifest, out string error)
    {
        manifest = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "The update manifest was empty.";
            return false;
        }

        var parsed = new QuestUpdateManifest();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                string header = line.Substring(1).Trim();

                if (header.StartsWith("Base URL:", StringComparison.OrdinalIgnoreCase))
                {
                    parsed.BaseUrl = header.Substring("Base URL:".Length).Trim().TrimEnd('/');
                }
                else if (header.StartsWith("BASE_APK:", StringComparison.OrdinalIgnoreCase))
                {
                    string value = header.Substring("BASE_APK:".Length).Trim();
                    string[] parts = value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2 || !IsSha256(parts[1]))
                    {
                        error = $"Invalid BASE_APK header on line {index + 1}.";
                        return false;
                    }

                    parsed.BaseApkFileName = parts[0];
                    parsed.BaseApkSha256 = parts[1].ToLowerInvariant();
                }
                else if (header.StartsWith("Target:", StringComparison.OrdinalIgnoreCase))
                {
                    parsed.TargetPath = header.Substring("Target:".Length).Trim();
                }

                continue;
            }

            string[] entryParts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (entryParts.Length != 3)
            {
                error = $"Invalid update entry on line {index + 1}.";
                return false;
            }

            QuestUpdateOperation operation;
            if (entryParts[0].Equals("add", StringComparison.OrdinalIgnoreCase))
                operation = QuestUpdateOperation.Add;
            else if (entryParts[0].Equals("del", StringComparison.OrdinalIgnoreCase))
                operation = QuestUpdateOperation.Delete;
            else
            {
                error = $"Unknown update operation '{entryParts[0]}' on line {index + 1}.";
                return false;
            }

            if (!TryNormalizeRelativePath(entryParts[1], out string assetPath))
            {
                error = $"Unsafe asset path on line {index + 1}.";
                return false;
            }

            if (!IsSha256(entryParts[2]))
            {
                error = $"Invalid SHA256 on line {index + 1}.";
                return false;
            }

            if (!seenPaths.Add(assetPath))
            {
                error = $"Duplicate asset path '{assetPath}' on line {index + 1}.";
                return false;
            }

            parsed._entries.Add(new QuestUpdateEntry(
                operation,
                assetPath,
                entryParts[2].ToLowerInvariant()));
        }

        if (!Uri.TryCreate(parsed.BaseUrl, UriKind.Absolute, out Uri baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps)
        {
            error = "The manifest Base URL is missing or is not HTTPS.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.BaseApkFileName) ||
            parsed.BaseApkFileName.IndexOfAny(new[] { '/', '\\' }) >= 0 ||
            !parsed.BaseApkFileName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
        {
            error = "The manifest BASE_APK filename is invalid.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.TargetPath) ||
            !parsed.TargetPath.Replace('\\', '/').TrimEnd('/')
                .EndsWith("/Android/media/com.readyatdawn.r15", StringComparison.OrdinalIgnoreCase))
        {
            error = "The manifest Target does not point to Echo VR's media folder.";
            return false;
        }

        manifest = parsed;
        return true;
    }

    public static bool IsSha256(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 64)
            return false;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool isHex = (c >= '0' && c <= '9') ||
                         (c >= 'a' && c <= 'f') ||
                         (c >= 'A' && c <= 'F');
            if (!isHex)
                return false;
        }

        return true;
    }

    public static bool TryNormalizeRelativePath(string value, out string normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string candidate = value.Replace('\\', '/').Trim();
        if (candidate.StartsWith("/", StringComparison.Ordinal) ||
            candidate.Contains(":"))
            return false;

        string[] segments = candidate.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(segments[i]) ||
                segments[i] == "." || segments[i] == "..")
                return false;
        }

        normalized = string.Join("/", segments);
        return true;
    }
}
