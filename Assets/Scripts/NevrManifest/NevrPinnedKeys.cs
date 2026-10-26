using System;
using System.Collections.Generic;
using System.Text;

// A minisign public key the installer trusts to sign the release manifest.
public sealed class NevrPinnedKey
{
    public string Label { get; }
    public byte[] KeyId { get; }
    public byte[] PublicKey { get; }

    // Lowercase hex of the 8 key-id bytes in the order they appear in the key and
    // signature files. This is the spelling `revoked_keys` uses in the manifest.
    public string KeyIdHex => NevrHex.Encode(KeyId);

    private NevrPinnedKey(string label, byte[] keyId, byte[] publicKey)
    {
        Label = label;
        KeyId = keyId;
        PublicKey = publicKey;
    }

    // Parses the second line of a minisign .pub file: base64 of "Ed" || key_id[8] || key[32].
    public static bool TryParse(string label, string base64, out NevrPinnedKey key, out string error)
    {
        key = null;
        error = null;
        try
        {
            byte[] blob = Convert.FromBase64String(base64);
            if (blob.Length != 42 || blob[0] != (byte)'E' || blob[1] != (byte)'d')
            {
                error = "not a minisign Ed25519 public key";
                return false;
            }

            var keyId = new byte[8];
            var publicKey = new byte[32];
            Buffer.BlockCopy(blob, 2, keyId, 0, 8);
            Buffer.BlockCopy(blob, 10, publicKey, 0, 32);
            key = new NevrPinnedKey(label, keyId, publicKey);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }
}

// The keys compiled into this build. K1 signs. K2 is the next key and KR the recovery
// key, both kept offline. Their public halves are pinned from the first release so a
// rotation never needs a new installer to trust a new key.
public static class NevrPinnedKeys
{
    public const string K1 = "RWTA7pyFRwI9NPbeZVbRJxtiA0YsG3jolqhV8mxuCEP0AUb24092wkuy";
    public const string K2 = "RWQ+cq9F86dyVc7yFNVU+YAVnSl5pJFun+A68lujJ1p3GsMb2v3vrK0F";
    public const string KR = "RWRBDKzhAOWXf+lhvvVo22yLwhwmABcky2IkqcvmLWtpG8Pm9/ux8dYx";

    // The manifest_version that was current when this installer was built. A fresh
    // install with no stored state cannot be rolled back below it. Raise it whenever a
    // new installer is released.
    public const long MinManifestVersion = 1;

    private static IReadOnlyList<NevrPinnedKey> _all;

    public static IReadOnlyList<NevrPinnedKey> All
    {
        get
        {
            if (_all == null)
            {
                var keys = new List<NevrPinnedKey>();
                Add(keys, "K1", K1);
                Add(keys, "K2", K2);
                Add(keys, "KR", KR);
                _all = keys;
            }

            return _all;
        }
    }

    private static void Add(List<NevrPinnedKey> keys, string label, string base64)
    {
        if (!NevrPinnedKey.TryParse(label, base64, out NevrPinnedKey key, out string error))
            throw new InvalidOperationException($"Pinned key {label} is invalid: {error}");

        keys.Add(key);
    }
}

public static class NevrHex
{
    public static string Encode(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        for (int i = 0; i < bytes.Length; i++)
            builder.Append(bytes[i].ToString("x2"));
        return builder.ToString();
    }
}
