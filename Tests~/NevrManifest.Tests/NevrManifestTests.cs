using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

public class NevrManifestTests
{
    private static bool Parse(byte[] bytes, out NevrManifest manifest, out string error) =>
        NevrManifest.TryParse(bytes, out manifest, out error);

    [Fact]
    public void ExampleManifestParses()
    {
        Assert.True(Parse(Fixtures.ExampleManifest(), out NevrManifest m, out string error), error);
        Assert.Equal(1, m.ManifestVersion);
        Assert.Equal("5.5.1", m.Channels["beta"]);
        NevrArtifact apk = m.Releases["5.5.1"].Artifacts.Single();
        Assert.Equal("quest-apk", apk.Kind);
        Assert.Equal(163417084, apk.Size);
        Assert.Equal("7266b1fbfc8e3c9efc465bba9bf01eeda7efe36751aa86e80a8e06e8447f71f2", apk.Sha256);
        Assert.Equal("com.readyatdawn.r15", apk.AndroidPackage);
        Assert.Equal(4987566, apk.AndroidVersionCode);
        Assert.Null(apk.AndroidSignerCertSha256);
        Assert.Equal(
            "https://evr.echo.taxi/nevr-runtime/v5.5.1/nevr-runtime-v5.5.1-quest.apk",
            NevrManifest.BuildArtifactUrl(apk));
        Assert.Equal(new DateTime(2026, 11, 7, 18, 0, 0, DateTimeKind.Utc), m.ExpiresUtc);
    }

    [Fact]
    public void SignerCertIsRead()
    {
        string cert = new string('a', 64);
        byte[] bytes = Fixtures.Edit(r =>
            r["releases"]["5.5.1"]["artifacts"][2]["android"]["signer_cert_sha256"] = cert);
        Assert.True(Parse(bytes, out NevrManifest m, out string error), error);
        Assert.Equal(cert, m.Releases["5.5.1"].Artifacts.Single().AndroidSignerCertSha256);
    }

    [Fact]
    public void UnknownMembersAndKindsAreIgnored()
    {
        byte[] bytes = Fixtures.Edit(r =>
        {
            r["future_member"] = new JObject { ["a"] = 1 };
            ((JArray)r["releases"]["5.5.1"]["artifacts"]).Add(new JObject { ["kind"] = "game-data", ["x"] = 1 });
        });
        Assert.True(Parse(bytes, out NevrManifest m, out string error), error);
        Assert.Single(m.Releases["5.5.1"].Artifacts);
    }

    public static TheoryData<string> Bad() => new TheoryData<string>
    {
        "schema2", "product", "version0", "versionString", "versionHuge", "genBad", "genOffset",
        "expiresBeforeGenerated", "noChannels", "channelToMissing", "channelName", "badRevoked",
        "tagMismatch", "buildIdMismatch", "pathDotDot", "pathOtherVersion", "pathOtherName",
        "shaUpper", "size0", "sizeString", "sizeFloat", "noAndroid", "otherPackage", "versionCode0",
        "certBad", "platformWindows", "noReleases", "releaseKeyBad"
    };

    [Theory]
    [MemberData(nameof(Bad))]
    public void InvalidManifestsAreRefused(string what)
    {
        byte[] bytes = Fixtures.Edit(r =>
        {
            JObject apk = (JObject)r["releases"]["5.5.1"]["artifacts"][2];
            switch (what)
            {
                case "schema2": r["schema_version"] = 2; break;
                case "product": r["product"] = "other"; break;
                case "version0": r["manifest_version"] = 0; break;
                case "versionString": r["manifest_version"] = "1"; break;
                case "versionHuge": r["manifest_version"] = 9007199254740992; break;
                case "genBad": r["generated"] = "2026-10-24 18:00:00"; break;
                case "genOffset": r["generated"] = "2026-10-24T18:00:00+00:00"; break;
                case "expiresBeforeGenerated": r["expires"] = "2026-10-01T00:00:00Z"; break;
                case "noChannels": r["channels"] = new JObject(); break;
                case "channelToMissing": r["channels"]["beta"] = "9.9.9"; break;
                case "channelName": r["channels"]["nightly"] = "5.5.1"; break;
                case "badRevoked": r["revoked_keys"] = new JArray("XYZ"); break;
                case "tagMismatch": r["releases"]["5.5.1"]["tag"] = "v5.5.2"; break;
                case "buildIdMismatch": r["releases"]["5.5.1"]["build_id"] = "NEVR-BUILD 5.5.1 " + new string('0', 40); break;
                case "pathDotDot": apk["path"] = "v5.5.1/../nevr-runtime-v5.5.1-quest.apk"; break;
                case "pathOtherVersion": apk["path"] = "v5.5.0/nevr-runtime-v5.5.1-quest.apk"; break;
                case "pathOtherName": apk["path"] = "v5.5.1/nevr-runtime-v5.5.1-other.apk"; break;
                case "shaUpper": apk["sha256"] = new string('A', 64); break;
                case "size0": apk["size"] = 0; break;
                case "sizeString": apk["size"] = "5"; break;
                case "sizeFloat": apk["size"] = 5.5; break;
                case "noAndroid": apk.Remove("android"); break;
                case "otherPackage": apk["android"]["package"] = "com.evil"; break;
                case "versionCode0": apk["android"]["version_code"] = 0; break;
                case "certBad": apk["android"]["signer_cert_sha256"] = "abc"; break;
                case "platformWindows": apk["platform"] = "windows-x86_64"; break;
                case "noReleases": r["releases"] = new JObject(); break;
                case "releaseKeyBad": ((JObject)r["releases"]).Add("5.05.1", r["releases"]["5.5.1"].DeepClone()); break;
            }
        });

        // otherPackage is checked by the install step against EchoPackageName, and the
        // manifest schema pins it with const; the parser refuses it as well.
        Assert.False(Parse(bytes, out _, out string error), what + " was accepted");
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void NonJsonAndStructuralProblemsNeverThrow()
    {
        byte[] ok = Fixtures.ExampleManifest();
        string text = Encoding.UTF8.GetString(ok);
        foreach (byte[] bytes in new[]
                 {
                     null, new byte[0], new byte[] { 0xFF, 0xFE, 0x00 }, Encoding.UTF8.GetBytes("{"),
                     Encoding.UTF8.GetBytes("[]"), Encoding.UTF8.GetBytes("null"),
                     Encoding.UTF8.GetBytes(text + " {}"), Encoding.UTF8.GetBytes(text + "x"),
                     Encoding.UTF8.GetBytes(new string('[', 5000)),
                     Encoding.UTF8.GetBytes(text.Replace("\"schema_version\": 1,", "\"schema_version\": 1, \"schema_version\": 1,")),
                     new byte[2 * 1024 * 1024]
                 })
        {
            Assert.False(Parse(bytes, out NevrManifest m, out _));
            Assert.Null(m);
        }
    }
}
