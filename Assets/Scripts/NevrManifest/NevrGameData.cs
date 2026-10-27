using System;
using System.Globalization;

// The Echo VR game data the signed manifest names: which artifact the channel installs, the
// address it is downloaded from, how a partial download is resumed, and what a finished
// download must match before it is extracted. Pure logic; the download itself is in Download.
public static class NevrGameData
{
    public const string Kind = "game-data";
    public const string Platform = "android-arm64";

    public static bool TryPick(
        NevrManifest manifest,
        string channel,
        out NevrArtifact artifact,
        out string error)
    {
        artifact = null;
        error = null;

        if (!manifest.Channels.TryGetValue(channel, out string version))
        {
            error = $"The update list has no Echo VR release on the '{channel}' channel yet.";
            return false;
        }

        if (!manifest.Releases.TryGetValue(version, out NevrRelease release))
        {
            error = $"The update list names release {version} but does not contain it.";
            return false;
        }

        NevrArtifact match = null;
        foreach (NevrArtifact candidate in release.Artifacts)
        {
            if (candidate.Kind != Kind || candidate.Platform != Platform)
                continue;

            if (match != null)
            {
                error = $"Release {version} lists more than one game-data file, so the installer cannot tell which to use.";
                return false;
            }

            match = candidate;
        }

        if (match == null)
        {
            error = $"Release {version} has no game data for the Quest.";
            return false;
        }

        artifact = match;
        return true;
    }

    // The artifact's path joined to the manifest's base address. The path is the one shape
    // NevrManifest.IsGameDataPath accepts, and the result is checked to still be an https
    // address under that base, so nothing the manifest says can point somewhere else.
    public static bool TryBuildUrl(NevrArtifact artifact, out string url, out string error)
    {
        url = null;
        error = null;
        if (artifact == null || artifact.Kind != Kind || !NevrManifest.IsGameDataPath(artifact.Path))
        {
            error = "The update list gave a game-data address the installer does not accept, so nothing was downloaded.";
            return false;
        }

        string joined = NevrManifest.BaseUrl + artifact.Path;
        if (!Uri.TryCreate(joined, UriKind.Absolute, out Uri uri) ||
            !Uri.TryCreate(NevrManifest.BaseUrl, UriKind.Absolute, out Uri baseUri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !uri.AbsolutePath.StartsWith(baseUri.AbsolutePath + "data/", StringComparison.Ordinal))
        {
            error = "The update list gave a game-data address the installer does not accept, so nothing was downloaded.";
            return false;
        }

        url = joined;
        return true;
    }

    // Where to carry on from, given how many bytes a partial file has. 0 starts over (nothing
    // kept, or the file is longer than the artifact so it cannot be a prefix of it). Exactly
    // `size` means every byte is there and only the checks remain.
    public static long ResumeOffset(long partialLength, long size)
    {
        if (partialLength <= 0 || partialLength > size)
            return 0;

        return partialLength;
    }

    public static string RangeHeader(long offset)
    {
        return "bytes=" + offset.ToString(CultureInfo.InvariantCulture) + "-";
    }

    // A server that does not do ranges answers 200 and sends the whole file from the start.
    public static bool ServerIgnoredRange(long responseCode)
    {
        return responseCode == 200;
    }

    // Returns null when a ranged answer is exactly the rest of the file ("206" and a
    // Content-Range of bytes <offset>-<size-1>/<size>), otherwise a sentence for the player.
    public static string CheckRangeResponse(long responseCode, string contentRange, long offset, long size)
    {
        if (responseCode != 206)
            return $"The server did not resume the download (HTTP {responseCode}).";

        string expected = string.Format(
            CultureInfo.InvariantCulture, "bytes {0}-{1}/{2}", offset, size - 1, size);
        if (!string.Equals(contentRange, expected, StringComparison.Ordinal))
            return $"The server resumed from the wrong place (it said '{contentRange}', the installer asked for '{expected}').";

        return null;
    }

    // What a finished download must be before it may be extracted: the size and the SHA-256
    // the signed manifest names. Returns null when it is, otherwise a sentence for the player.
    public static string CheckDownloaded(NevrArtifact artifact, long length, string sha256Hex)
    {
        if (length != artifact.Size)
            return $"The game data is {length} bytes but the update list says {artifact.Size}, so it was not used.";

        if (!string.Equals(sha256Hex, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            return "The downloaded game data is not the file the update list names (its checksum differs), so it was not used.";

        return null;
    }
}
