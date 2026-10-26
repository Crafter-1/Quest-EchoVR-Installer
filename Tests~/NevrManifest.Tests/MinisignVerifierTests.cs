using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

public class MinisignVerifierTests
{
    private static readonly TestKey Key = new TestKey(1, 0x10);
    private static readonly TestKey Other = new TestKey(77, 0x40);

    private static NevrPinnedKey[] Pinned(params TestKey[] keys) => keys.Select(k => k.Pinned()).ToArray();

    [Fact]
    public void RealMinisignOutputVerifies()
    {
        string pub = File.ReadAllLines(Fixtures.Path("minisign-test.pub"))[1];
        Assert.True(NevrPinnedKey.TryParse("minisign", pub, out NevrPinnedKey key, out _));
        byte[] data = File.ReadAllBytes(Fixtures.Path("manifest.json"));
        string sig = File.ReadAllText(Fixtures.Path("manifest.json.sig"));

        Assert.Equal(NevrSignatureStatus.Valid,
            MinisignVerifier.Verify(data, sig, new[] { key }, null, out string id));
        Assert.Equal(key.KeyIdHex, id);

        data[10] ^= 1;
        Assert.Equal(NevrSignatureStatus.BadSignature,
            MinisignVerifier.Verify(data, sig, new[] { key }, null, out _));
    }

    [Fact]
    public void TestSignerMatchesMinisignFormat()
    {
        byte[] data = Fixtures.ExampleManifest();
        Assert.Equal(NevrSignatureStatus.Valid,
            MinisignVerifier.Verify(data, Key.Sign(data), Pinned(Key), null, out _));
    }

    [Fact]
    public void TamperedDataIsBadSignature()
    {
        byte[] data = Fixtures.ExampleManifest();
        string sig = Key.Sign(data);
        data[data.Length - 3] ^= 0x20;
        Assert.Equal(NevrSignatureStatus.BadSignature,
            MinisignVerifier.Verify(data, sig, Pinned(Key), null, out _));
    }

    [Fact]
    public void TamperedTrustedCommentIsBadSignature()
    {
        byte[] data = Fixtures.ExampleManifest();
        string sig = Key.Sign(data).Replace("file:manifest.json", "file:manifest.jsoN");
        Assert.Equal(NevrSignatureStatus.BadSignature,
            MinisignVerifier.Verify(data, sig, Pinned(Key), null, out _));
    }

    [Fact]
    public void UnpinnedKeyIsUnknown()
    {
        byte[] data = Fixtures.ExampleManifest();
        Assert.Equal(NevrSignatureStatus.UnknownKey,
            MinisignVerifier.Verify(data, Other.Sign(data), Pinned(Key), null, out _));
    }

    [Fact]
    public void RevokedKeyIsRefusedEvenWhenPinnedAndValid()
    {
        byte[] data = Fixtures.ExampleManifest();
        Assert.Equal(NevrSignatureStatus.RevokedKey,
            MinisignVerifier.Verify(data, Key.Sign(data), Pinned(Key),
                new[] { Key.KeyIdHex }, out _));
    }

    [Fact]
    public void LegacyNonHashedModeIsRefused()
    {
        byte[] data = Fixtures.ExampleManifest();
        Assert.Equal(NevrSignatureStatus.UnsupportedAlgorithm,
            MinisignVerifier.Verify(data, Key.Sign(data, algorithm: "Ed"), Pinned(Key), null, out _));
    }

    [Fact]
    public void SignatureFromAnotherPinnedKeyIdWithWrongKeyMaterialFails()
    {
        // Same key id as the pinned key, signed by different key material.
        byte[] data = Fixtures.ExampleManifest();
        var impostor = new TestKey(99, 0x10);
        Assert.Equal(Key.KeyIdHex, impostor.KeyIdHex);
        Assert.Equal(NevrSignatureStatus.BadSignature,
            MinisignVerifier.Verify(data, impostor.Sign(data), Pinned(Key), null, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("untrusted comment: x\n!!!notbase64!!!\ntrusted comment: y\nAAAA\n")]
    [InlineData("untrusted comment: x\nAAAA\ntrusted comment: y\nAAAA\n")]
    [InlineData("untrusted comment: x\nAAAA\n")]
    [InlineData("\n\n\n\n\n\n")]
    public void MalformedSignatureFilesNeverThrow(string sig)
    {
        Assert.Equal(NevrSignatureStatus.Malformed,
            MinisignVerifier.Verify(Fixtures.ExampleManifest(), sig, Pinned(Key), null, out _));
    }

    [Fact]
    public void NullAndOversizeInputsAreMalformed()
    {
        byte[] data = Fixtures.ExampleManifest();
        Assert.Equal(NevrSignatureStatus.Malformed, MinisignVerifier.Verify(null, "x", Pinned(Key), null, out _));
        Assert.Equal(NevrSignatureStatus.Malformed, MinisignVerifier.Verify(data, null, Pinned(Key), null, out _));
        Assert.Equal(NevrSignatureStatus.Malformed,
            MinisignVerifier.Verify(data, new string('a', 5000), Pinned(Key), null, out _));
    }

    [Fact]
    public void ExtraLinesAreMalformed()
    {
        byte[] data = Fixtures.ExampleManifest();
        Assert.Equal(NevrSignatureStatus.Malformed,
            MinisignVerifier.Verify(data, Key.Sign(data) + "extra\n", Pinned(Key), null, out _));
    }

    [Fact]
    public void CrLfLineEndingsVerify()
    {
        byte[] data = Fixtures.ExampleManifest();
        Assert.Equal(NevrSignatureStatus.Valid,
            MinisignVerifier.Verify(data, Key.Sign(data).Replace("\n", "\r\n"), Pinned(Key), null, out _));
    }

    [Fact]
    public void PinnedKeysParseAndKeyIdsMatchTheirLabels()
    {
        // The comments in manifest-keys.pub show the id byte-reversed (minisign prints it
        // little-endian); the manifest's revoked_keys use file order.
        string[] labels = { "343D0247859CEEC0", "5572A7F345AF723E", "7F97E500E1AC0C41" };
        Assert.Equal(3, NevrPinnedKeys.All.Count);
        for (int i = 0; i < 3; i++)
        {
            byte[] id = NevrPinnedKeys.All[i].KeyId.Reverse().ToArray();
            Assert.Equal(labels[i].ToLowerInvariant(), NevrHex.Encode(id));
            Assert.Equal(32, NevrPinnedKeys.All[i].PublicKey.Length);
        }
    }

    [Fact]
    public void BadPinnedKeysAreRejected()
    {
        Assert.False(NevrPinnedKey.TryParse("x", "not base64!", out _, out _));
        Assert.False(NevrPinnedKey.TryParse("x", "AAAA", out _, out _));
    }
}
