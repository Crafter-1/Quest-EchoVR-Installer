using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public sealed class NevrArtifact
{
    public string Name { get; set; }
    public string Kind { get; set; }
    public string Platform { get; set; }
    public string Path { get; set; }
    public string Sha256 { get; set; }
    public long Size { get; set; }
    public string AndroidPackage { get; set; }
    public long AndroidVersionCode { get; set; }

    // Null when the manifest does not carry a signer certificate for this APK.
    public string AndroidSignerCertSha256 { get; set; }

    // Set for a game-data artifact only: the data version of the files inside the zip and the
    // Echo build they belong to, both whole-number text.
    public string DataVersion { get; set; }
    public string EchoBuild { get; set; }
}

public sealed class NevrRelease
{
    public string Version { get; set; }
    public string Tag { get; set; }
    public string Commit { get; set; }
    public string BuildId { get; set; }
    public List<NevrArtifact> Artifacts { get; } = new List<NevrArtifact>();
}

// The release manifest, parsed from bytes whose signature has already verified.
// Members this build does not know are ignored; members it relies on are checked
// strictly, and anything off refuses the whole manifest.
public sealed class NevrManifest
{
    public const string BaseUrl = "https://evr.echo.taxi/nevr-runtime/";
    public const string ManifestUrl = BaseUrl + "manifest.json";
    public const string SignatureUrl = BaseUrl + "manifest.json.sig";
    public const long MaxManifestBytes = 1024 * 1024;

    // The game-data zip is the largest thing the manifest names (about 0.9 GB in 2026). A
    // manifest that names more than this is refused before anything is downloaded.
    public const long MaxGameDataBytes = 4L * 1024 * 1024 * 1024;

    public long ManifestVersion { get; private set; }
    public DateTime GeneratedUtc { get; private set; }
    public DateTime ExpiresUtc { get; private set; }
    public List<string> RevokedKeys { get; } = new List<string>();
    public Dictionary<string, string> Channels { get; } = new Dictionary<string, string>();
    public Dictionary<string, NevrRelease> Releases { get; } = new Dictionary<string, NevrRelease>();

    private const long MaxJsonInteger = 9007199254740991;
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private static readonly Regex VersionPattern =
        new Regex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z");
    private static readonly Regex Sha256Pattern = new Regex(@"^[0-9a-f]{64}\z");
    private static readonly Regex CommitPattern = new Regex(@"^[0-9a-f]{40}\z");
    private static readonly Regex KeyIdPattern = new Regex(@"^[0-9a-f]{16}\z");
    private static readonly Regex ChannelPattern = new Regex(@"^(stable|beta)\z");
    private static readonly Regex ArtifactPathPattern = new Regex(
        @"^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)/nevr-runtime-v[0-9]+\.[0-9]+\.[0-9]+-[a-z0-9.-]+\z");
    private static readonly Regex GameDataPathPattern = new Regex(@"^data/echovr-data-[0-9a-f]{12}\.zip\z");
    private static readonly Regex DigitsPattern = new Regex(@"^[0-9]{1,20}\z");

    // True for the one shape of path a game-data artifact may have: a file directly under
    // data/, named for the first 12 digits of its hash. No "..", no slashes beyond that one,
    // no scheme or host.
    public static bool IsGameDataPath(string path)
    {
        return path != null && GameDataPathPattern.IsMatch(path);
    }

    // Never throws. The caller must have verified the signature over these exact bytes.
    public static bool TryParse(byte[] bytes, out NevrManifest manifest, out string error)
    {
        manifest = null;
        error = null;
        try
        {
            return TryParseCore(bytes, out manifest, out error);
        }
        catch (Exception exception)
        {
            manifest = null;
            error = "could not be read: " + exception.Message;
            return false;
        }
    }

    public static string BuildArtifactUrl(NevrArtifact artifact)
    {
        return BaseUrl + artifact.Path;
    }

    private static bool TryParseCore(byte[] bytes, out NevrManifest manifest, out string error)
    {
        manifest = null;
        error = null;
        if (bytes == null || bytes.Length == 0 || bytes.Length > MaxManifestBytes)
        {
            error = "is empty or too large";
            return false;
        }

        string text = new UTF8Encoding(false, true).GetString(bytes);
        JObject root;
        var settings = new JsonLoadSettings
        {
            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
            CommentHandling = CommentHandling.Ignore
        };

        using (var reader = new JsonTextReader(new StringReader(text)))
        {
            reader.DateParseHandling = DateParseHandling.None;
            reader.FloatParseHandling = FloatParseHandling.Decimal;
            reader.MaxDepth = 16;
            root = JObject.Load(reader, settings);
            if (reader.Read())
            {
                error = "has data after the end of the JSON document";
                return false;
            }
        }

        var parsed = new NevrManifest();
        if (!RequireLong(root, "schema_version", out long schema, out error))
            return false;
        if (schema != 1)
        {
            error = "uses a format version this installer does not know (" + schema + ")";
            return false;
        }

        if (!RequireString(root, "product", out string product, out error))
            return false;
        if (product != "nevr-runtime")
        {
            error = "is for another product";
            return false;
        }

        if (!RequireLong(root, "manifest_version", out long manifestVersion, out error))
            return false;
        if (manifestVersion < 1 || manifestVersion > MaxJsonInteger)
        {
            error = "has an invalid manifest_version";
            return false;
        }

        parsed.ManifestVersion = manifestVersion;
        if (!RequireTimestamp(root, "generated", out DateTime generated, out error) ||
            !RequireTimestamp(root, "expires", out DateTime expires, out error))
            return false;
        if (expires <= generated)
        {
            error = "expires before it was generated";
            return false;
        }

        parsed.GeneratedUtc = generated;
        parsed.ExpiresUtc = expires;

        if (root.TryGetValue("revoked_keys", out JToken revoked))
        {
            if (!(revoked is JArray revokedArray))
            {
                error = "has a revoked_keys member that is not a list";
                return false;
            }

            foreach (JToken item in revokedArray)
            {
                string id = item.Type == JTokenType.String ? (string)item : null;
                if (id == null || !KeyIdPattern.IsMatch(id))
                {
                    error = "has a revoked key id that is not 16 lowercase hex digits";
                    return false;
                }

                if (!parsed.RevokedKeys.Contains(id))
                    parsed.RevokedKeys.Add(id);
            }
        }

        if (!(root["releases"] is JObject releases) || releases.Count == 0)
        {
            error = "has no releases";
            return false;
        }

        foreach (JProperty property in releases.Properties())
        {
            if (!VersionPattern.IsMatch(property.Name) ||
                !TryParseRelease(property.Name, property.Value as JObject, out NevrRelease release, out error))
            {
                error = error ?? "has a release with an invalid version";
                return false;
            }

            parsed.Releases[property.Name] = release;
        }

        if (!(root["channels"] is JObject channels) || channels.Count == 0)
        {
            error = "has no channels";
            return false;
        }

        foreach (JProperty property in channels.Properties())
        {
            string version = property.Value.Type == JTokenType.String ? (string)property.Value : null;
            if (!ChannelPattern.IsMatch(property.Name) || version == null ||
                !parsed.Releases.ContainsKey(version))
            {
                error = "names a channel or a release that does not exist (" + property.Name + ")";
                return false;
            }

            parsed.Channels[property.Name] = version;
        }

        manifest = parsed;
        return true;
    }

    private static bool TryParseRelease(
        string version,
        JObject node,
        out NevrRelease release,
        out string error)
    {
        release = null;
        error = null;
        if (node == null)
        {
            error = "has a release that is not an object (" + version + ")";
            return false;
        }

        if (!RequireString(node, "version", out string declared, out error) ||
            !RequireString(node, "tag", out string tag, out error) ||
            !RequireString(node, "commit", out string commit, out error) ||
            !RequireString(node, "build_id", out string buildId, out error))
            return false;

        if (declared != version || tag != "v" + version || !CommitPattern.IsMatch(commit) ||
            buildId != "NEVR-BUILD " + version + " " + commit)
        {
            error = "has a release whose version, tag, commit and build id disagree (" + version + ")";
            return false;
        }

        if (!(node["artifacts"] is JArray artifacts) || artifacts.Count == 0)
        {
            error = "has a release with no artifacts (" + version + ")";
            return false;
        }

        var parsed = new NevrRelease { Version = version, Tag = tag, Commit = commit, BuildId = buildId };
        foreach (JToken token in artifacts)
        {
            if (!TryParseArtifact(version, token as JObject, out NevrArtifact artifact, out error))
                return false;

            if (artifact != null)
                parsed.Artifacts.Add(artifact);
        }

        release = parsed;
        return true;
    }

    // Returns true with a null artifact for a kind this build does not handle.
    private static bool TryParseArtifact(
        string version,
        JObject node,
        out NevrArtifact artifact,
        out string error)
    {
        artifact = null;
        error = null;
        if (node == null)
        {
            error = "has an artifact that is not an object (" + version + ")";
            return false;
        }

        if (!RequireString(node, "kind", out string kind, out error))
            return false;
        if (kind == "game-data")
            return TryParseGameData(version, node, out artifact, out error);
        if (kind != "quest-apk")
            return true;

        if (!RequireString(node, "name", out string name, out error) ||
            !RequireString(node, "platform", out string platform, out error) ||
            !RequireString(node, "path", out string path, out error) ||
            !RequireString(node, "sha256", out string sha256, out error) ||
            !RequireLong(node, "size", out long size, out error))
            return false;

        if (platform != "android-arm64" ||
            !ArtifactPathPattern.IsMatch(path) ||
            !path.StartsWith("v" + version + "/", StringComparison.Ordinal) ||
            path.Substring(path.IndexOf('/') + 1) != name ||
            !Sha256Pattern.IsMatch(sha256) ||
            size < 1 || size > MaxJsonInteger)
        {
            error = "has an APK entry that is not valid (" + version + ")";
            return false;
        }

        if (!(node["android"] is JObject android) ||
            !RequireString(android, "package", out string package, out error) ||
            !RequireLong(android, "version_code", out long versionCode, out error) ||
            package != "com.readyatdawn.r15" ||
            versionCode < 1)
        {
            error = error ?? "has an APK entry with no Android package details (" + version + ")";
            return false;
        }

        string cert = null;
        if (android.TryGetValue("signer_cert_sha256", out JToken certToken))
        {
            cert = certToken.Type == JTokenType.String ? (string)certToken : null;
            if (cert == null || !Sha256Pattern.IsMatch(cert))
            {
                error = "has an APK signer certificate hash that is not valid (" + version + ")";
                return false;
            }
        }

        artifact = new NevrArtifact
        {
            Name = name,
            Kind = kind,
            Platform = platform,
            Path = path,
            Sha256 = sha256,
            Size = size,
            AndroidPackage = package,
            AndroidVersionCode = versionCode,
            AndroidSignerCertSha256 = cert
        };
        return true;
    }

    // The Echo VR game data for Android: one zip, named and hashed by the signed manifest.
    // Its "contents" list is not read; the zip as a whole is what is checked.
    private static bool TryParseGameData(
        string version,
        JObject node,
        out NevrArtifact artifact,
        out string error)
    {
        artifact = null;
        error = null;
        if (!RequireString(node, "name", out string name, out error) ||
            !RequireString(node, "platform", out string platform, out error) ||
            !RequireString(node, "path", out string path, out error) ||
            !RequireString(node, "sha256", out string sha256, out error) ||
            !RequireLong(node, "size", out long size, out error))
            return false;

        if (platform != "android-arm64" ||
            !IsGameDataPath(path) ||
            path.Substring("data/".Length) != name ||
            !Sha256Pattern.IsMatch(sha256) ||
            size < 1 || size > MaxGameDataBytes)
        {
            error = "has a game-data entry that is not valid (" + version + ")";
            return false;
        }

        if (!(node["game_data"] is JObject gameData) ||
            !RequireString(gameData, "data_version", out string dataVersion, out error) ||
            !RequireString(gameData, "echo_build", out string echoBuild, out error) ||
            !DigitsPattern.IsMatch(dataVersion) ||
            !DigitsPattern.IsMatch(echoBuild))
        {
            error = error ?? "has a game-data entry with no valid data version or Echo build (" + version + ")";
            return false;
        }

        artifact = new NevrArtifact
        {
            Name = name,
            Kind = "game-data",
            Platform = platform,
            Path = path,
            Sha256 = sha256,
            Size = size,
            DataVersion = dataVersion,
            EchoBuild = echoBuild
        };
        return true;
    }

    private static bool RequireString(JObject node, string name, out string value, out string error)
    {
        value = null;
        error = null;
        if (node.TryGetValue(name, out JToken token) && token.Type == JTokenType.String)
        {
            value = (string)token;
            return true;
        }

        error = "is missing the text member '" + name + "'";
        return false;
    }

    private static bool RequireLong(JObject node, string name, out long value, out string error)
    {
        value = 0;
        error = null;
        if (node.TryGetValue(name, out JToken token) && token.Type == JTokenType.Integer)
        {
            try
            {
                value = (long)token;
                return true;
            }
            catch (OverflowException)
            {
            }
        }

        error = "is missing the whole-number member '" + name + "'";
        return false;
    }

    private static bool RequireTimestamp(JObject node, string name, out DateTime value, out string error)
    {
        value = default(DateTime);
        if (!RequireString(node, name, out string text, out error))
            return false;

        if (!DateTime.TryParseExact(
                text,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out value))
        {
            error = "has a time that is not valid ('" + name + "')";
            return false;
        }

        return true;
    }
}
