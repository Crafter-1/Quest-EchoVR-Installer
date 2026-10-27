using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

// A throwaway signer that writes minisign hashed-mode ("ED") signature files, so tests
// can sign any manifest. The seeds are fixed test values, not release keys.
public sealed class TestKey
{
    public byte[] KeyId { get; }
    public byte[] PublicKey { get; }
    private readonly Ed25519PrivateKeyParameters _private;

    public TestKey(byte seedByte, byte idByte)
    {
        var seed = new byte[32];
        for (int i = 0; i < seed.Length; i++)
            seed[i] = (byte)(seedByte + i);

        _private = new Ed25519PrivateKeyParameters(seed, 0);
        PublicKey = _private.GeneratePublicKey().GetEncoded();
        KeyId = new byte[8];
        for (int i = 0; i < 8; i++)
            KeyId[i] = (byte)(idByte + i);
    }

    public string KeyIdHex => NevrHex.Encode(KeyId);

    public NevrPinnedKey Pinned(string label = "T")
    {
        var blob = new byte[42];
        blob[0] = (byte)'E';
        blob[1] = (byte)'d';
        Buffer.BlockCopy(KeyId, 0, blob, 2, 8);
        Buffer.BlockCopy(PublicKey, 0, blob, 10, 32);
        Assert_True(NevrPinnedKey.TryParse(label, Convert.ToBase64String(blob), out NevrPinnedKey key, out _));
        return key;
    }

    public string Sign(byte[] data, string trustedComment = "timestamp:1 file:manifest.json", string algorithm = "ED")
    {
        var hash = new Blake2bDigest(512);
        hash.BlockUpdate(data, 0, data.Length);
        var digest = new byte[64];
        hash.DoFinal(digest, 0);
        byte[] message = algorithm == "ED" ? digest : data;
        byte[] signature = Raw(message);

        var blob = new byte[74];
        blob[0] = (byte)algorithm[0];
        blob[1] = (byte)algorithm[1];
        Buffer.BlockCopy(KeyId, 0, blob, 2, 8);
        Buffer.BlockCopy(signature, 0, blob, 10, 64);

        byte[] comment = Encoding.UTF8.GetBytes(trustedComment);
        var signed = new byte[64 + comment.Length];
        Buffer.BlockCopy(signature, 0, signed, 0, 64);
        Buffer.BlockCopy(comment, 0, signed, 64, comment.Length);

        return "untrusted comment: test signature\n" +
               Convert.ToBase64String(blob) + "\n" +
               "trusted comment: " + trustedComment + "\n" +
               Convert.ToBase64String(Raw(signed)) + "\n";
    }

    private byte[] Raw(byte[] message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, _private);
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    private static void Assert_True(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("test key did not parse");
    }
}

public static class Fixtures
{
    public static string Path(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static byte[] ExampleManifest() => File.ReadAllBytes(Path("example-manifest.json"));

    // The example manifest with `edit` applied, as compact JSON bytes.
    public static byte[] Edit(Action<JObject> edit)
    {
        JObject root = JObject.Parse(File.ReadAllText(Path("example-manifest.json")));
        edit(root);
        return Encoding.UTF8.GetBytes(root.ToString(Formatting.None));
    }

    public static readonly DateTime Now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
}
