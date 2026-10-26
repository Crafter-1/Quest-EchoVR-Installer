using System;

// What the unsigned game-data update list (files.echovr.de/updates/quest/update.manifest)
// may ask the installer to do. The list is not signed, so it is held to a fixed host, fixed
// size limits and a short list of places it may write, until game-data artifacts are in the
// signed manifest (nevr-runtime #965).
public static class NevrGameDataPolicy
{
    public const string PinnedHost = "files.echovr.de";
    public const string PinnedBasePath = "/updates/quest";
    public const string AssetBaseUrl = "https://" + PinnedHost + PinnedBasePath + "/";
    public const string ManifestUrl = AssetBaseUrl + "update.manifest";

    public const long MaxManifestBytes = 256 * 1024;
    public const int MaxEntries = 1000;
    public const long MaxAssetBytes = 128L * 1024 * 1024;
    public const long MaxTotalBytes = 512L * 1024 * 1024;

    private static readonly string[] AllowedRoots = { "asset_patches/", "files/" };

    private static readonly string[] RefusedExtensions =
    {
        ".so", ".dex", ".apk", ".jar", ".dll", ".odex", ".vdex", ".oat", ".art", ".sh", ".exe", ".bat", ".cmd"
    };

    public static string BuildAssetUrl(string relativePath)
    {
        return AssetBaseUrl + relativePath;
    }

    // Returns null when the list may be used, otherwise a sentence for the player.
    public static string Check(QuestUpdateManifest manifest)
    {
        if (!IsPinnedBaseUrl(manifest.BaseUrl))
            return "The game-data update list points at a server the installer does not use (" +
                   manifest.BaseUrl + "), so it was not used.";

        if (manifest.Entries.Count > MaxEntries)
            return $"The game-data update list has {manifest.Entries.Count} entries (the most allowed is {MaxEntries}), so it was not used.";

        foreach (QuestUpdateEntry entry in manifest.Entries)
        {
            if (!IsAllowedPath(entry.AssetPath, out string reason))
                return $"The game-data update list asked for '{entry.AssetPath}': {reason} It was not used.";
        }

        return null;
    }

    public static bool IsPinnedBaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri uri))
            return false;

        return uri.Scheme == Uri.UriSchemeHttps &&
               string.Equals(uri.Host, PinnedHost, StringComparison.OrdinalIgnoreCase) &&
               uri.IsDefaultPort &&
               string.IsNullOrEmpty(uri.UserInfo) &&
               string.IsNullOrEmpty(uri.Query) &&
               string.IsNullOrEmpty(uri.Fragment) &&
               string.Equals(uri.AbsolutePath.TrimEnd('/'), PinnedBasePath, StringComparison.Ordinal);
    }

    // `normalizedPath` is relative to Echo's media folder, already past the traversal checks.
    public static bool IsAllowedPath(string normalizedPath, out string reason)
    {
        reason = null;
        if (string.IsNullOrEmpty(normalizedPath))
        {
            reason = "The path is empty.";
            return false;
        }

        bool inRoot = false;
        foreach (string root in AllowedRoots)
            inRoot |= normalizedPath.StartsWith(root, StringComparison.Ordinal) && normalizedPath.Length > root.Length;

        if (!inRoot)
        {
            reason = "It is outside the game-data folders.";
            return false;
        }

        foreach (string segment in normalizedPath.Split('/'))
        {
            if (segment.StartsWith(".", StringComparison.Ordinal))
            {
                reason = "Hidden files are not allowed.";
                return false;
            }

            foreach (char c in segment)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                          c == '.' || c == '_' || c == '-';
                if (!ok)
                {
                    reason = "The path has characters that game data does not use.";
                    return false;
                }
            }
        }

        foreach (string extension in RefusedExtensions)
        {
            if (normalizedPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                reason = "Program files are not game data.";
                return false;
            }
        }

        return true;
    }
}
