using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

public class NevrManifestPolicyTests
{
    private static readonly TestKey Key = new TestKey(1, 0x10);
    private static readonly TestKey Other = new TestKey(77, 0x40);
    private static readonly NevrPinnedKey[] Pinned = { Key.Pinned("K1"), Other.Pinned("K2") };

    private static NevrManifestDecision Run(
        byte[] bytes, string sig, NevrTrustState state = null, long min = 1, DateTime? now = null,
        bool nullNow = false) =>
        NevrManifestPolicy.Evaluate(bytes, sig, state, Pinned, min, nullNow ? (DateTime?)null : (now ?? Fixtures.Now));

    private static NevrManifestDecision Signed(byte[] bytes, NevrTrustState state = null, long min = 1,
        DateTime? now = null) => Run(bytes, Key.Sign(bytes), state, min, now);

    [Fact]
    public void ExampleManifestIsAcceptedAndProducesState()
    {
        byte[] bytes = Fixtures.ExampleManifest();
        NevrManifestDecision d = Signed(bytes);
        Assert.True(d.Accepted, d.Message);
        Assert.True(d.StateChanged);
        Assert.Equal(1, d.NewState.HighestManifestVersion);
        Assert.Equal(NevrTrustState.Sha256Hex(bytes), d.NewState.HighestManifestSha256);
        Assert.Equal(bytes, d.NewState.ManifestBytes);
    }

    [Fact]
    public void MissingClockRefuses()
    {
        NevrManifestDecision d = Run(Fixtures.ExampleManifest(), Key.Sign(Fixtures.ExampleManifest()), nullNow: true);
        Assert.False(d.Accepted);
        Assert.Equal(NevrManifestFailure.ClockUnavailable, d.Failure);
    }

    [Fact]
    public void ClockOfUnspecifiedKindRefuses()
    {
        NevrManifestDecision d = Signed(Fixtures.ExampleManifest(), now: new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Unspecified));
        Assert.Equal(NevrManifestFailure.ClockUnavailable, d.Failure);
    }

    [Fact]
    public void ExpiredManifestRefuses()
    {
        NevrManifestDecision d = Signed(Fixtures.ExampleManifest(), now: new DateTime(2026, 11, 7, 18, 0, 0, DateTimeKind.Utc));
        Assert.Equal(NevrManifestFailure.Expired, d.Failure);
        d = Signed(Fixtures.ExampleManifest(), now: new DateTime(2026, 11, 7, 17, 59, 59, DateTimeKind.Utc));
        Assert.True(d.Accepted);
    }

    [Fact]
    public void ManifestFromTheFutureRefuses()
    {
        NevrManifestDecision d = Signed(Fixtures.ExampleManifest(), now: new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(NevrManifestFailure.NotYetValid, d.Failure);
    }

    [Fact]
    public void BadSignatureIsRefusedBeforeAnythingIsParsed()
    {
        // Not JSON at all: with a bad signature the failure must be the signature's.
        byte[] junk = Encoding.UTF8.GetBytes("this is not json");
        NevrManifestDecision d = Run(junk, Key.Sign(Fixtures.ExampleManifest()));
        Assert.Equal(NevrManifestFailure.SignatureInvalid, d.Failure);
        Assert.Null(d.Manifest);
    }

    [Fact]
    public void SignedButUnparseableIsManifestInvalid()
    {
        byte[] junk = Encoding.UTF8.GetBytes("this is not json");
        Assert.Equal(NevrManifestFailure.ManifestInvalid, Signed(junk).Failure);
    }

    [Fact]
    public void UnknownKeyAndMalformedSignatureAreDistinct()
    {
        byte[] bytes = Fixtures.ExampleManifest();
        var stranger = new TestKey(5, 0x70);
        Assert.Equal(NevrManifestFailure.SignatureUntrusted, Run(bytes, stranger.Sign(bytes)).Failure);
        Assert.Equal(NevrManifestFailure.SignatureMalformed, Run(bytes, "nonsense").Failure);
        Assert.Equal(NevrManifestFailure.SignatureMalformed, Run(bytes, null).Failure);
    }

    [Fact]
    public void KeyRevokedInStoredStateRefuses()
    {
        var state = new NevrTrustState();
        state.RevokedKeys.Add(Key.KeyIdHex);
        Assert.Equal(NevrManifestFailure.KeyRevoked, Signed(Fixtures.ExampleManifest(), state).Failure);
    }

    [Fact]
    public void ManifestRevokingItsOwnSigningKeyRefuses()
    {
        byte[] bytes = Fixtures.Edit(r => r["revoked_keys"] = new JArray(Key.KeyIdHex));
        Assert.Equal(NevrManifestFailure.KeyRevoked, Signed(bytes).Failure);
    }

    [Fact]
    public void RevokedKeysFromAcceptedManifestAreStoredAndAccumulate()
    {
        var state = new NevrTrustState();
        state.RevokedKeys.Add("0000000000000001");
        byte[] bytes = Fixtures.Edit(r => r["revoked_keys"] = new JArray(Other.KeyIdHex));
        NevrManifestDecision d = Signed(bytes, state);
        Assert.True(d.Accepted, d.Message);
        Assert.Equal(new[] { "0000000000000001", Other.KeyIdHex }.OrderBy(x => x, StringComparer.Ordinal),
            d.NewState.RevokedKeys);

        // A later manifest that says nothing about revocations does not un-revoke.
        byte[] later = Fixtures.Edit(r => r["manifest_version"] = 2);
        NevrManifestDecision d2 = Signed(later, d.NewState);
        Assert.True(d2.Accepted, d2.Message);
        Assert.Contains(Other.KeyIdHex, d2.NewState.RevokedKeys);

        // And the revoked key can no longer sign.
        byte[] third = Fixtures.Edit(r => r["manifest_version"] = 3);
        Assert.Equal(NevrManifestFailure.KeyRevoked, Run(third, Other.Sign(third), d2.NewState).Failure);
    }

    [Fact]
    public void LowerVersionThanStoredRefuses()
    {
        byte[] v5 = Fixtures.Edit(r => r["manifest_version"] = 5);
        NevrManifestDecision accepted = Signed(v5);
        Assert.True(accepted.Accepted, accepted.Message);

        byte[] v4 = Fixtures.Edit(r => r["manifest_version"] = 4);
        NevrManifestDecision d = Signed(v4, accepted.NewState);
        Assert.Equal(NevrManifestFailure.VersionWentDown, d.Failure);
    }

    [Fact]
    public void LowerVersionThanCompiledMinimumRefusesOnAFreshInstall()
    {
        byte[] v1 = Fixtures.ExampleManifest();
        Assert.Equal(NevrManifestFailure.VersionWentDown, Signed(v1, null, min: 2).Failure);
        Assert.True(Signed(v1, null, min: 1).Accepted);
    }

    [Fact]
    public void SameVersionSameBytesIsUnchanged()
    {
        byte[] bytes = Fixtures.ExampleManifest();
        NevrManifestDecision first = Signed(bytes);
        NevrManifestDecision again = Signed(bytes, first.NewState);
        Assert.True(again.Accepted, again.Message);
        Assert.False(again.StateChanged);
        Assert.NotNull(again.Manifest);
    }

    [Fact]
    public void SameVersionDifferentBytesRefuses()
    {
        NevrManifestDecision first = Signed(Fixtures.ExampleManifest());
        byte[] changed = Fixtures.Edit(r => r["channels"]["beta"] = "5.5.1");
        // Same content, different bytes (the example is pretty-printed, this is compact).
        Assert.NotEqual(Fixtures.ExampleManifest(), changed);
        Assert.Equal(NevrManifestFailure.VersionConflict, Signed(changed, first.NewState).Failure);
    }

    [Fact]
    public void HigherVersionIsAcceptedAndRaisesTheMark()
    {
        NevrManifestDecision first = Signed(Fixtures.ExampleManifest());
        byte[] v2 = Fixtures.Edit(r => r["manifest_version"] = 2);
        NevrManifestDecision d = Signed(v2, first.NewState);
        Assert.True(d.Accepted, d.Message);
        Assert.Equal(2, d.NewState.HighestManifestVersion);
    }

    [Fact]
    public void PolicyNeverThrowsOnHostileInput()
    {
        foreach (string sig in new[] { null, "", "x", new string('\n', 100), Key.Sign(new byte[] { 1 }) })
            foreach (byte[] bytes in new[] { null, new byte[0], new byte[] { 0 }, Fixtures.ExampleManifest() })
            {
                NevrManifestDecision d = Run(bytes, sig);
                Assert.False(d.Accepted);
                Assert.False(string.IsNullOrEmpty(d.Message));
            }
    }
}
