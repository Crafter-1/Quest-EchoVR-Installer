using System;
using System.IO;
using System.Linq;
using Xunit;

public sealed class NevrTrustStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nevr-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private static NevrTrustState Sample(long version)
    {
        byte[] bytes = Fixtures.Edit(r => r["manifest_version"] = version);
        var state = new NevrTrustState
        {
            HighestManifestVersion = version,
            HighestManifestSha256 = NevrTrustState.Sha256Hex(bytes),
            ManifestBytes = bytes,
            SignatureText = "untrusted comment: t\nAAAA\ntrusted comment: t\nBBBB\n"
        };
        state.RevokedKeys.Add("00000000000000aa");
        state.RevokedKeys.Add("00000000000000bb");
        return state;
    }

    [Fact]
    public void MissingFileIsMissingNotDamaged()
    {
        Assert.Equal(NevrTrustLoadStatus.Missing, NevrTrustStore.Load(_dir, out _, out _));
    }

    [Fact]
    public void RoundTripKeepsEverythingAndLeavesNoTemporaryFile()
    {
        NevrTrustState state = Sample(7);
        Assert.True(NevrTrustStore.TrySave(_dir, state, out string error), error);
        Assert.Equal(NevrTrustLoadStatus.Loaded, NevrTrustStore.Load(_dir, out NevrTrustState back, out error));
        Assert.Equal(7, back.HighestManifestVersion);
        Assert.Equal(state.HighestManifestSha256, back.HighestManifestSha256);
        Assert.Equal(state.ManifestBytes, back.ManifestBytes);
        Assert.Equal(state.SignatureText, back.SignatureText);
        Assert.Equal(state.RevokedKeys, back.RevokedKeys);
        Assert.Equal(new[] { NevrTrustStore.FileName }, Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void SecondSaveReplacesTheFirst()
    {
        Assert.True(NevrTrustStore.TrySave(_dir, Sample(1), out string error), error);
        Assert.True(NevrTrustStore.TrySave(_dir, Sample(2), out error), error);
        NevrTrustStore.Load(_dir, out NevrTrustState back, out _);
        Assert.Equal(2, back.HighestManifestVersion);
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("")]
    public void DamagedFileIsDamagedNotMissing(string contents)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, NevrTrustStore.FileName), contents);
        Assert.Equal(NevrTrustLoadStatus.Damaged, NevrTrustStore.Load(_dir, out NevrTrustState state, out string error));
        Assert.Null(state);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void StoredBytesThatDoNotMatchTheirChecksumAreDamaged()
    {
        Assert.True(NevrTrustStore.TrySave(_dir, Sample(3), out string saveError), saveError);
        string path = Path.Combine(_dir, NevrTrustStore.FileName);
        string text = File.ReadAllText(path).Replace("\"highest_manifest_version\": 3", "\"highest_manifest_version\": 3")
            .Replace("\"manifest_sha256\": \"", "\"manifest_sha256\": \"0");
        File.WriteAllText(path, text);
        Assert.Equal(NevrTrustLoadStatus.Damaged, NevrTrustStore.Load(_dir, out _, out _));
    }

    [Fact]
    public void FailedSaveKeepsTheOldFileAndReportsFailure()
    {
        Assert.True(NevrTrustStore.TrySave(_dir, Sample(1), out string error), error);
        // A directory squatting on the temporary name makes the write fail.
        Directory.CreateDirectory(Path.Combine(_dir, NevrTrustStore.FileName + ".tmp"));
        Assert.False(NevrTrustStore.TrySave(_dir, Sample(2), out error));
        Assert.False(string.IsNullOrEmpty(error));
        NevrTrustStore.Load(_dir, out NevrTrustState back, out _);
        Assert.Equal(1, back.HighestManifestVersion);
    }
}

public class NevrTrustedClockTests
{
    private static readonly DateTime Device = new DateTime(2026, 10, 26, 12, 0, 5, DateTimeKind.Utc);
    private const string Header = "Mon, 26 Oct 2026 12:00:00 GMT";

    [Fact]
    public void ServerTimeIsUsedWhenDeviceAgrees()
    {
        Assert.True(NevrTrustedClock.TryGet(Device, Header, out DateTime now, out string error), error);
        Assert.Equal(new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc), now);
        Assert.Equal(DateTimeKind.Utc, now.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yesterday")]
    [InlineData("2026-10-26T12:00:00Z")]
    public void MissingOrUnreadableHeaderGivesNoTime(string header)
    {
        Assert.False(NevrTrustedClock.TryGet(Device, header, out _, out string error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void DeviceClockMoreThanADayOffGivesNoTime()
    {
        Assert.False(NevrTrustedClock.TryGet(Device.AddHours(25), Header, out _, out string error));
        Assert.Contains("clock", error);
        Assert.False(NevrTrustedClock.TryGet(Device.AddDays(-400), Header, out _, out _));
        Assert.True(NevrTrustedClock.TryGet(Device.AddHours(23), Header, out _, out _));
    }

    [Fact]
    public void ServerTimeBeforeTheBuildDateGivesNoTime()
    {
        Assert.False(NevrTrustedClock.TryGet(
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), "Wed, 01 Jan 2020 00:00:00 GMT", out _, out _));
    }

    [Fact]
    public void DeviceTimeOfUnspecifiedKindGivesNoTime()
    {
        Assert.False(NevrTrustedClock.TryGet(
            new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Unspecified), Header, out _, out _));
    }
}
