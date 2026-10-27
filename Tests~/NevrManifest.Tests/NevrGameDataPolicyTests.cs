using System.Linq;
using Xunit;

public class NevrGameDataPolicyTests
{
    private const string Sha = "0a7fa5f9cfc173013e152a75fac2ded7ca4f66b8d8530f598c0c2530b5cf0973";

    private static string Manifest(string baseUrl, params string[] lines) =>
        "# Base URL: " + baseUrl + "\n" +
        "# BASE_APK: echo.apk " + Sha + "\n" +
        "# Target: /sdcard/Android/media/com.readyatdawn.r15\n" +
        string.Join("\n", lines) + "\n";

    private static QuestUpdateManifest Parse(string text)
    {
        Assert.True(QuestUpdateManifest.TryParse(text, out QuestUpdateManifest manifest, out string error), error);
        return manifest;
    }

    [Fact]
    public void RealLookingManifestIsAccepted()
    {
        QuestUpdateManifest m = Parse(Manifest("https://files.echovr.de/updates/quest",
            "add asset_patches/489bb35d53ca50e9/2dfe2e7610506f03 " + Sha,
            "del asset_patches/e2efe7289d5985b8/3fa6d25bc24a7b01 " + Sha));
        Assert.Null(NevrGameDataPolicy.Check(m));
    }

    [Theory]
    [InlineData("https://evil.example/updates/quest")]
    [InlineData("https://files.echovr.de.evil.example/updates/quest")]
    [InlineData("https://user@files.echovr.de/updates/quest")]
    [InlineData("https://files.echovr.de:8443/updates/quest")]
    [InlineData("https://files.echovr.de/other")]
    public void BaseUrlOtherThanThePinnedOneIsRefused(string baseUrl)
    {
        QuestUpdateManifest m = Parse(Manifest(baseUrl, "add asset_patches/a/b " + Sha));
        Assert.NotNull(NevrGameDataPolicy.Check(m));
    }

    [Theory]
    [InlineData("lib/arm64-v8a/libmain.so")]
    [InlineData("asset_patches/a/libx.so")]
    [InlineData("asset_patches/a/payload.DEX")]
    [InlineData("asset_patches/a/x.apk")]
    [InlineData("asset_patches/a/x.jar")]
    [InlineData("asset_patches/a/x.dll")]
    [InlineData("files/x.sh")]
    [InlineData("asset_patches/a/.hidden")]
    [InlineData("files/.quest_evr_data_ready")]
    [InlineData("other_dir/file")]
    [InlineData("asset_patches")]
    [InlineData("asset_patches/a b/c")]
    [InlineData("asset_patches/a/cé")]
    public void PathsOutsideGameDataOrExecutableLookingAreRefused(string path)
    {
        Assert.False(NevrGameDataPolicy.IsAllowedPath(path, out string reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("del")]
    public void RefusalAppliesToDeletesToo(string operation)
    {
        QuestUpdateManifest m = Parse(Manifest("https://files.echovr.de/updates/quest",
            operation + " ../x " + Sha).Replace("../x", "obb/other"));
        Assert.NotNull(NevrGameDataPolicy.Check(m));
    }

    [Theory]
    [InlineData("asset_patches/489bb35d53ca50e9/2dfe2e7610506f03")]
    [InlineData("files/_data/some/asset.pnf")]
    [InlineData("files/_data/v1.2/a-b_c")]
    public void GameDataPathsAreAllowed(string path)
    {
        Assert.True(NevrGameDataPolicy.IsAllowedPath(path, out _));
    }

    [Fact]
    public void TooManyEntriesAreRefused()
    {
        string[] lines = Enumerable.Range(0, NevrGameDataPolicy.MaxEntries + 1)
            .Select(i => "add asset_patches/a/f" + i + " " + Sha).ToArray();
        QuestUpdateManifest m = Parse(Manifest("https://files.echovr.de/updates/quest", lines));
        Assert.NotNull(NevrGameDataPolicy.Check(m));
    }

    [Fact]
    public void AssetUrlIsBuiltFromThePinnedBaseNotTheFile()
    {
        Assert.Equal(
            "https://files.echovr.de/updates/quest/asset_patches/a/b",
            NevrGameDataPolicy.BuildAssetUrl("asset_patches/a/b"));
        Assert.StartsWith("https://", NevrGameDataPolicy.ManifestUrl);
        Assert.Equal("https://files.echovr.de/updates/quest/update.manifest", NevrGameDataPolicy.ManifestUrl);
    }

    [Fact]
    public void DownloadLimitsAreSet()
    {
        Assert.True(NevrGameDataPolicy.MaxManifestBytes > 0 && NevrGameDataPolicy.MaxManifestBytes <= 1024 * 1024);
        Assert.True(NevrGameDataPolicy.MaxAssetBytes > 26_000_000);
        Assert.True(NevrGameDataPolicy.MaxTotalBytes >= NevrGameDataPolicy.MaxAssetBytes);
    }
}

public class NevrHttpRulesTests
{
    [Fact]
    public void Only200IsAnAcceptedAnswer()
    {
        Assert.Null(NevrHttpRules.RefusalFor(200));
        foreach (long code in new long[] { 301, 302, 307, 308, 204, 206, 404, 500, 0 })
            Assert.False(string.IsNullOrEmpty(NevrHttpRules.RefusalFor(code)), code.ToString());
    }

    [Fact]
    public void RedirectsAreNamedAsRedirects()
    {
        Assert.Contains("another address", NevrHttpRules.RefusalFor(302));
    }
}

public class NevrStallWatchdogTests
{
    [Fact]
    public void ProgressKeepsItAlive()
    {
        var watchdog = new NevrStallWatchdog(stallSeconds: 30, totalSeconds: 3600, startSeconds: 0);
        long bytes = 0;
        for (double t = 1; t < 200; t += 10)
        {
            bytes += 1000;
            Assert.Null(watchdog.Check(bytes, t));
        }
    }

    [Fact]
    public void NoBytesForTheStallTimeIsAStall()
    {
        var watchdog = new NevrStallWatchdog(30, 3600, 0);
        Assert.Null(watchdog.Check(100, 5));
        Assert.Null(watchdog.Check(100, 34));
        Assert.Contains("stopped", watchdog.Check(100, 36));
    }

    [Fact]
    public void NeverStartingCountsFromTheStart()
    {
        var watchdog = new NevrStallWatchdog(30, 3600, 100);
        Assert.Null(watchdog.Check(0, 129));
        Assert.NotNull(watchdog.Check(0, 131));
    }

    [Fact]
    public void TotalTimeIsCappedEvenWithProgress()
    {
        var watchdog = new NevrStallWatchdog(30, 3600, 0);
        long bytes = 0;
        for (double t = 10; t <= 3600; t += 10)
            Assert.Null(watchdog.Check(bytes += 1, t));
        Assert.Contains("too long", watchdog.Check(bytes += 1, 3601));
    }
}
