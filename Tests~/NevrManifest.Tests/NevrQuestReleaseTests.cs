using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

public class NevrQuestReleaseTests
{
    private const string Cert = "c0ffee0000000000000000000000000000000000000000000000000000000000";

    private static NevrManifest Manifest(Action<JObject> edit = null)
    {
        byte[] bytes = Fixtures.Edit(r =>
        {
            r["releases"]["5.5.1"]["artifacts"][2]["android"]["signer_cert_sha256"] = Cert;
            edit?.Invoke(r);
        });
        Assert.True(NevrManifest.TryParse(bytes, out NevrManifest m, out string error), error);
        return m;
    }

    [Fact]
    public void PicksTheBetaApk()
    {
        Assert.True(NevrQuestRelease.TryPick(Manifest(), "beta", out NevrRelease release, out NevrArtifact apk, out string error), error);
        Assert.Equal("5.5.1", release.Version);
        Assert.Equal("quest-apk", apk.Kind);
    }

    [Fact]
    public void AbsentChannelHasNoRelease()
    {
        Assert.False(NevrQuestRelease.TryPick(Manifest(), "stable", out _, out _, out string error));
        Assert.Contains("'stable'", error);
    }

    [Fact]
    public void ReleaseWithoutQuestApkIsRefused()
    {
        NevrManifest m = Manifest(r => ((JArray)r["releases"]["5.5.1"]["artifacts"]).RemoveAt(2));
        Assert.False(NevrQuestRelease.TryPick(m, "beta", out _, out _, out string error));
        Assert.Contains("no Quest APK", error);
    }

    [Fact]
    public void TwoQuestApksAreAmbiguous()
    {
        NevrManifest m = Manifest(r =>
        {
            JToken second = r["releases"]["5.5.1"]["artifacts"][2].DeepClone();
            second["name"] = "nevr-runtime-v5.5.1-quest2.apk";
            second["path"] = "v5.5.1/nevr-runtime-v5.5.1-quest2.apk";
            ((JArray)r["releases"]["5.5.1"]["artifacts"]).Add(second);
        });
        Assert.False(NevrQuestRelease.TryPick(m, "beta", out _, out _, out string error));
        Assert.Contains("more than one", error);
    }

    private static NevrArtifact Apk(bool withCert = true)
    {
        NevrManifest m = Manifest();
        NevrQuestRelease.TryPick(m, "beta", out _, out NevrArtifact apk, out _);
        if (!withCert)
            apk.AndroidSignerCertSha256 = null;
        return apk;
    }

    [Fact]
    public void MatchingApkPasses()
    {
        Assert.Null(NevrQuestRelease.CheckApk(Apk(), "com.readyatdawn.r15", 4987566, new[] { Cert }));
        Assert.Null(NevrQuestRelease.CheckApk(Apk(), "com.readyatdawn.r15", 4987566, new[] { Cert.ToUpperInvariant() }));
    }

    [Fact]
    public void EachMismatchIsRefused()
    {
        Assert.NotNull(NevrQuestRelease.CheckApk(Apk(false), "com.readyatdawn.r15", 4987566, new[] { Cert }));
        Assert.NotNull(NevrQuestRelease.CheckApk(Apk(), "com.evil", 4987566, new[] { Cert }));
        Assert.NotNull(NevrQuestRelease.CheckApk(Apk(), "com.readyatdawn.r15", 4987567, new[] { Cert }));
        Assert.NotNull(NevrQuestRelease.CheckApk(Apk(), "com.readyatdawn.r15", 4987566, new[] { new string('1', 64) }));
        Assert.NotNull(NevrQuestRelease.CheckApk(Apk(), "com.readyatdawn.r15", 4987566, new string[0]));
        Assert.NotNull(NevrQuestRelease.CheckApk(Apk(), "com.readyatdawn.r15", 4987566, null));
        Assert.NotNull(NevrQuestRelease.CheckApk(Apk(), "com.readyatdawn.r15", 4987566, new[] { Cert, Cert }));
    }

    [Fact]
    public void InstalledSignerComparison()
    {
        Assert.True(NevrQuestRelease.IsSignedByUs(Apk(), new[] { Cert }));
        Assert.False(NevrQuestRelease.IsSignedByUs(Apk(), new[] { new string('1', 64) }));
        Assert.False(NevrQuestRelease.IsSignedByUs(Apk(), new string[0]));
        Assert.False(NevrQuestRelease.IsSignedByUs(Apk(false), new[] { Cert }));
    }
}

public class NevrDownloadSinkTests
{
    private static string Hex(byte[] b) => NevrHex.Encode(System.Security.Cryptography.SHA256.Create().ComputeHash(b));

    [Fact]
    public void ExactDownloadPassesAndHashes()
    {
        byte[] data = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        var ms = new MemoryStream();
        using (var sink = new NevrDownloadSink(ms, 1000, true))
        {
            Assert.True(sink.Write(data, 400));
            Assert.True(sink.Write(data.Skip(400).ToArray(), 600));
            Assert.True(sink.Complete(out string sha));
            Assert.Equal(Hex(data), sha);
            Assert.Equal(1000, sink.Received);
        }
    }

    [Fact]
    public void BytesPastTheLimitStopTheDownload()
    {
        using (var sink = new NevrDownloadSink(new MemoryStream(), 10, true))
        {
            Assert.True(sink.Write(new byte[6], 6));
            Assert.False(sink.Write(new byte[6], 6));
            Assert.Equal(6, sink.Received);
            Assert.NotNull(sink.Error);
            Assert.False(sink.Write(new byte[1], 1));
            Assert.False(sink.Complete(out _));
        }
    }

    [Fact]
    public void ShortExactDownloadFails()
    {
        using (var sink = new NevrDownloadSink(new MemoryStream(), 10, true))
        {
            sink.Write(new byte[9], 9);
            Assert.False(sink.Complete(out _));
            Assert.Contains("9 of 10", sink.Error);
        }
    }

    [Fact]
    public void CapOnlyModeAcceptsShorter()
    {
        using (var sink = new NevrDownloadSink(new MemoryStream(), 10, false))
        {
            sink.Write(new byte[3], 3);
            Assert.True(sink.Complete(out _));
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(9, true)]
    public void AnnouncedLengthMustMatch(long announced, bool ok)
    {
        using (var sink = new NevrDownloadSink(new MemoryStream(), 10, true))
            Assert.Equal(ok, sink.AcceptAnnouncedLength(announced));
    }

    [Fact]
    public void WriteFailureIsReported()
    {
        var ro = new MemoryStream(new byte[4], false);
        using (var sink = new NevrDownloadSink(ro, 10, true))
        {
            Assert.False(sink.Write(new byte[2], 2));
            Assert.Contains("Could not save", sink.Error);
        }
    }
}

public class NevrInstallPlanTests
{
    private const string Cert = "c0ffee0000000000000000000000000000000000000000000000000000000000";
    private static readonly string[] Ours = { Cert };
    private static readonly string[] Theirs = { "1111111111111111111111111111111111111111111111111111111111111111" };

    private static NevrArtifact Apk()
    {
        byte[] bytes = Fixtures.Edit(r =>
            r["releases"]["5.5.1"]["artifacts"][2]["android"]["signer_cert_sha256"] = Cert);
        NevrManifest.TryParse(bytes, out NevrManifest m, out _);
        NevrQuestRelease.TryPick(m, "beta", out _, out NevrArtifact apk, out _);
        return apk;
    }

    [Fact]
    public void PlanFollowsWhatIsInstalled()
    {
        NevrArtifact apk = Apk();
        Assert.Equal(NevrInstallAction.Install, NevrQuestRelease.Plan(apk, false, 0, null));
        Assert.Equal(NevrInstallAction.AlreadyCurrent, NevrQuestRelease.Plan(apk, true, 4987566, Ours));
        Assert.Equal(NevrInstallAction.Update, NevrQuestRelease.Plan(apk, true, 4987565, Ours));
        Assert.Equal(NevrInstallAction.NewerInstalled, NevrQuestRelease.Plan(apk, true, 4987567, Ours));
        Assert.Equal(NevrInstallAction.DifferentSigner, NevrQuestRelease.Plan(apk, true, 4987566, Theirs));
        Assert.Equal(NevrInstallAction.DifferentSigner, NevrQuestRelease.Plan(apk, true, 4987566, new string[0]));
    }
}
