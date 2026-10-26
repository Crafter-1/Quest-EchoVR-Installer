using System;
using System.Collections.Generic;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

public enum NevrSignatureStatus
{
    Valid,
    Malformed,
    UnsupportedAlgorithm,
    UnknownKey,
    RevokedKey,
    BadSignature
}

// Verifies a detached minisign signature in hashed mode ("ED": Ed25519 over the
// BLAKE2b-512 of the file) and its trusted comment. It returns a status for every
// outcome and never throws, so a bad signature file cannot surface as an unhandled
// exception.
public static class MinisignVerifier
{
    public const int MaxSignatureBytes = 1024;
    private const string UntrustedPrefix = "untrusted comment:";
    private const string TrustedPrefix = "trusted comment: ";

    public static NevrSignatureStatus Verify(
        byte[] data,
        string signatureText,
        IReadOnlyList<NevrPinnedKey> pinnedKeys,
        ICollection<string> revokedKeyIds,
        out string signingKeyId)
    {
        signingKeyId = null;
        try
        {
            return VerifyCore(data, signatureText, pinnedKeys, revokedKeyIds, out signingKeyId);
        }
        catch (Exception)
        {
            return NevrSignatureStatus.Malformed;
        }
    }

    private static NevrSignatureStatus VerifyCore(
        byte[] data,
        string signatureText,
        IReadOnlyList<NevrPinnedKey> pinnedKeys,
        ICollection<string> revokedKeyIds,
        out string signingKeyId)
    {
        signingKeyId = null;
        if (data == null || signatureText == null || pinnedKeys == null ||
            signatureText.Length > MaxSignatureBytes)
            return NevrSignatureStatus.Malformed;

        string[] lines = signatureText.Replace("\r\n", "\n").Split('\n');
        int count = lines.Length;
        if (count > 0 && lines[count - 1].Length == 0)
            count--;

        if (count != 4 ||
            !lines[0].StartsWith(UntrustedPrefix, StringComparison.Ordinal) ||
            !lines[2].StartsWith(TrustedPrefix, StringComparison.Ordinal))
            return NevrSignatureStatus.Malformed;

        byte[] blob = Convert.FromBase64String(lines[1]);
        byte[] globalSignature = Convert.FromBase64String(lines[3]);
        if (blob.Length != 74 || globalSignature.Length != 64)
            return NevrSignatureStatus.Malformed;

        if (blob[0] != (byte)'E' || blob[1] != (byte)'D')
            return NevrSignatureStatus.UnsupportedAlgorithm;

        var keyId = new byte[8];
        var signature = new byte[64];
        Buffer.BlockCopy(blob, 2, keyId, 0, 8);
        Buffer.BlockCopy(blob, 10, signature, 0, 64);
        signingKeyId = NevrHex.Encode(keyId);

        if (revokedKeyIds != null && revokedKeyIds.Contains(signingKeyId))
            return NevrSignatureStatus.RevokedKey;

        NevrPinnedKey key = null;
        for (int i = 0; i < pinnedKeys.Count; i++)
        {
            if (pinnedKeys[i].KeyIdHex == signingKeyId)
            {
                key = pinnedKeys[i];
                break;
            }
        }

        if (key == null)
            return NevrSignatureStatus.UnknownKey;

        var hash = new Blake2bDigest(512);
        hash.BlockUpdate(data, 0, data.Length);
        var digest = new byte[64];
        hash.DoFinal(digest, 0);
        if (!Ed25519Verify(key.PublicKey, digest, signature))
            return NevrSignatureStatus.BadSignature;

        byte[] trustedComment = new UTF8Encoding(false, true)
            .GetBytes(lines[2].Substring(TrustedPrefix.Length));
        var signed = new byte[64 + trustedComment.Length];
        Buffer.BlockCopy(signature, 0, signed, 0, 64);
        Buffer.BlockCopy(trustedComment, 0, signed, 64, trustedComment.Length);
        return Ed25519Verify(key.PublicKey, signed, globalSignature)
            ? NevrSignatureStatus.Valid
            : NevrSignatureStatus.BadSignature;
    }

    private static bool Ed25519Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
        verifier.BlockUpdate(message, 0, message.Length);
        return verifier.VerifySignature(signature);
    }
}
