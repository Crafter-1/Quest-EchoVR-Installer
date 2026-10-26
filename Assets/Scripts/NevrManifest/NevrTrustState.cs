using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// What the installer remembers between runs: the highest manifest_version it has
// accepted, the keys it must never trust again, and the exact bytes of the last
// verified manifest and signature.
public sealed class NevrTrustState
{
    public long HighestManifestVersion { get; set; }
    public string HighestManifestSha256 { get; set; }
    public SortedSet<string> RevokedKeys { get; } = new SortedSet<string>(StringComparer.Ordinal);
    public byte[] ManifestBytes { get; set; }
    public string SignatureText { get; set; }

    public static string Sha256Hex(byte[] bytes)
    {
        using (SHA256 sha = SHA256.Create())
            return NevrHex.Encode(sha.ComputeHash(bytes));
    }
}

public enum NevrTrustLoadStatus
{
    Missing,
    Loaded,
    Damaged
}

// Reads and writes the trust state as one file, so the manifest, its signature and the
// highest version can never disagree after a crash: the file is written to a temporary
// name in the same folder, flushed to disk, then renamed over the old one.
public static class NevrTrustStore
{
    public const string FileName = "nevr_manifest_state.json";

    public static NevrTrustLoadStatus Load(string directory, out NevrTrustState state, out string error)
    {
        state = null;
        error = null;
        string path = Path.Combine(directory, FileName);
        try
        {
            if (!File.Exists(path))
                return NevrTrustLoadStatus.Missing;

            JObject root;
            using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path, Encoding.UTF8))))
            {
                reader.DateParseHandling = DateParseHandling.None;
                root = JObject.Load(reader);
            }

            var loaded = new NevrTrustState();
            if (root["format"]?.Type != JTokenType.Integer || (long)root["format"] != 1 ||
                root["highest_manifest_version"]?.Type != JTokenType.Integer ||
                root["manifest_sha256"]?.Type != JTokenType.String ||
                root["manifest_b64"]?.Type != JTokenType.String ||
                root["signature"]?.Type != JTokenType.String ||
                !(root["revoked_keys"] is JArray revoked))
            {
                error = "is missing members";
                return NevrTrustLoadStatus.Damaged;
            }

            loaded.HighestManifestVersion = (long)root["highest_manifest_version"];
            loaded.HighestManifestSha256 = (string)root["manifest_sha256"];
            loaded.ManifestBytes = Convert.FromBase64String((string)root["manifest_b64"]);
            loaded.SignatureText = (string)root["signature"];
            foreach (JToken id in revoked)
                loaded.RevokedKeys.Add((string)id);

            if (loaded.HighestManifestVersion < 1 ||
                NevrTrustState.Sha256Hex(loaded.ManifestBytes) != loaded.HighestManifestSha256)
            {
                error = "does not match its own checksum";
                return NevrTrustLoadStatus.Damaged;
            }

            state = loaded;
            return NevrTrustLoadStatus.Loaded;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return NevrTrustLoadStatus.Damaged;
        }
    }

    public static bool TrySave(string directory, NevrTrustState state, out string error)
    {
        error = null;
        string path = Path.Combine(directory, FileName);
        string temporaryPath = path + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            var root = new JObject
            {
                ["format"] = 1,
                ["highest_manifest_version"] = state.HighestManifestVersion,
                ["manifest_sha256"] = state.HighestManifestSha256,
                ["revoked_keys"] = new JArray(state.RevokedKeys),
                ["manifest_b64"] = Convert.ToBase64String(state.ManifestBytes),
                ["signature"] = state.SignatureText
            };

            byte[] bytes = new UTF8Encoding(false).GetBytes(root.ToString(Formatting.Indented));
            using (var stream = new FileStream(
                       temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            if (File.Exists(path))
                File.Replace(temporaryPath, path, null);
            else
                File.Move(temporaryPath, path);
            return true;
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
                // Keep the original failure.
            }

            return false;
        }
    }
}
