using System;
using System.Collections.Generic;
using System.Globalization;

public enum NevrManifestFailure
{
    None,
    ClockUnavailable,
    SignatureMalformed,
    SignatureUntrusted,
    KeyRevoked,
    SignatureInvalid,
    ManifestInvalid,
    VersionWentDown,
    VersionConflict,
    Expired,
    NotYetValid
}

public sealed class NevrManifestDecision
{
    public bool Accepted { get; private set; }
    public NevrManifestFailure Failure { get; private set; }

    // A sentence for the player, in plain words.
    public string Message { get; private set; }

    public NevrManifest Manifest { get; private set; }

    // The state to store. Null unless Accepted; equal to the old state when the
    // manifest was the one already stored.
    public NevrTrustState NewState { get; private set; }

    // True when the new state differs from the old and must be stored.
    public bool StateChanged { get; private set; }

    public static NevrManifestDecision Accept(
        NevrManifest manifest,
        NevrTrustState newState,
        bool stateChanged)
    {
        return new NevrManifestDecision
        {
            Accepted = true,
            Failure = NevrManifestFailure.None,
            Manifest = manifest,
            NewState = newState,
            StateChanged = stateChanged
        };
    }

    public static NevrManifestDecision Refuse(NevrManifestFailure failure, string message)
    {
        return new NevrManifestDecision { Failure = failure, Message = message };
    }
}

public static class NevrTrustedClock
{
    // The oldest time a correct clock can show: the day this build was made.
    public static readonly DateTime FloorUtc = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
    public static readonly TimeSpan MaxSkew = TimeSpan.FromHours(24);

    // The time is the server's own Date header (read over HTTPS from the host that
    // served the manifest). The device clock only has to agree with it, so a wrong
    // device clock is caught instead of trusted. A missing or unreadable header, or a
    // device clock that is off by more than a day, gives no time at all.
    public static bool TryGet(
        DateTime deviceUtc,
        string serverDateHeader,
        out DateTime nowUtc,
        out string error)
    {
        nowUtc = default(DateTime);
        error = null;

        if (string.IsNullOrWhiteSpace(serverDateHeader) ||
            !DateTime.TryParseExact(
                serverDateHeader.Trim(),
                "r",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTime server))
        {
            error = "The update server did not give a usable time, so the installer cannot tell whether its update list is current.";
            return false;
        }

        if (server < FloorUtc)
        {
            error = "The update server's time looks wrong, so the installer cannot tell whether its update list is current.";
            return false;
        }

        if (deviceUtc.Kind != DateTimeKind.Utc || (deviceUtc - server).Duration() > MaxSkew)
        {
            error = "Your headset's clock is more than a day away from the real time. Set the date and time in the headset's settings, then try again.";
            return false;
        }

        nowUtc = server;
        return true;
    }
}

public static class NevrManifestPolicy
{
    private const int FutureSlackHours = 24;

    // Runs the rules in order and stops at the first that fails: clock, signature
    // (before any parsing), parse, schema and product, version, expiry, revoked keys.
    // It touches no files; the caller stores NewState when Accepted and StateChanged.
    // `nowUtc` null means the clock is missing, and that refuses the manifest.
    public static NevrManifestDecision Evaluate(
        byte[] manifestBytes,
        string signatureText,
        NevrTrustState state,
        IReadOnlyList<NevrPinnedKey> pinnedKeys,
        long minManifestVersion,
        DateTime? nowUtc)
    {
        state = state ?? new NevrTrustState();

        if (nowUtc == null || nowUtc.Value.Kind != DateTimeKind.Utc)
            return NevrManifestDecision.Refuse(
                NevrManifestFailure.ClockUnavailable,
                "The installer cannot tell what time it is, so it cannot tell whether its update list is current.");

        if (manifestBytes == null || manifestBytes.Length == 0)
            return NevrManifestDecision.Refuse(
                NevrManifestFailure.ManifestInvalid, "The update list was empty.");

        NevrSignatureStatus signature = MinisignVerifier.Verify(
            manifestBytes, signatureText, pinnedKeys, state.RevokedKeys, out string keyId);
        switch (signature)
        {
            case NevrSignatureStatus.Valid:
                break;
            case NevrSignatureStatus.RevokedKey:
                return NevrManifestDecision.Refuse(
                    NevrManifestFailure.KeyRevoked,
                    "The update list was signed with a key that has been withdrawn, so the installer will not use it.");
            case NevrSignatureStatus.UnknownKey:
            case NevrSignatureStatus.UnsupportedAlgorithm:
                return NevrManifestDecision.Refuse(
                    NevrManifestFailure.SignatureUntrusted,
                    "The update list was not signed with a key this installer trusts, so it will not use it.");
            case NevrSignatureStatus.Malformed:
                return NevrManifestDecision.Refuse(
                    NevrManifestFailure.SignatureMalformed,
                    "The update list's signature could not be read, so the installer will not use it.");
            default:
                return NevrManifestDecision.Refuse(
                    NevrManifestFailure.SignatureInvalid,
                    "The update list's signature does not match its contents, so the installer will not use it.");
        }

        if (!NevrManifest.TryParse(manifestBytes, out NevrManifest manifest, out string parseError))
            return NevrManifestDecision.Refuse(
                NevrManifestFailure.ManifestInvalid,
                "The update list is signed but is not usable: it " + parseError + ".");

        string sha256 = NevrTrustState.Sha256Hex(manifestBytes);
        long floor = Math.Max(state.HighestManifestVersion, minManifestVersion);
        if (manifest.ManifestVersion < floor)
            return NevrManifestDecision.Refuse(
                NevrManifestFailure.VersionWentDown,
                "The update list is older than one this installer has already seen (version " +
                manifest.ManifestVersion + ", have " + floor + "), so it will not use it.");

        bool sameAsStored = manifest.ManifestVersion == state.HighestManifestVersion;
        if (sameAsStored && sha256 != state.HighestManifestSha256)
            return NevrManifestDecision.Refuse(
                NevrManifestFailure.VersionConflict,
                "The update list has the same version number as the one already stored but different contents, so the installer will not use it.");

        DateTime now = nowUtc.Value;
        if (now >= manifest.ExpiresUtc)
            return NevrManifestDecision.Refuse(
                NevrManifestFailure.Expired,
                "The update list has expired (" + manifest.ExpiresUtc.ToString("yyyy-MM-dd HH:mm") +
                " UTC), so the installer will not use it. Try again later.");

        if (manifest.GeneratedUtc > now.AddHours(FutureSlackHours))
            return NevrManifestDecision.Refuse(
                NevrManifestFailure.NotYetValid,
                "The update list claims to be from the future, so the installer will not use it.");

        if (keyId != null && manifest.RevokedKeys.Contains(keyId))
            return NevrManifestDecision.Refuse(
                NevrManifestFailure.KeyRevoked,
                "The update list withdraws the key that signed it, so the installer will not use it.");

        var next = new NevrTrustState
        {
            HighestManifestVersion = manifest.ManifestVersion,
            HighestManifestSha256 = sha256,
            ManifestBytes = manifestBytes,
            SignatureText = signatureText
        };
        foreach (string id in state.RevokedKeys)
            next.RevokedKeys.Add(id);
        foreach (string id in manifest.RevokedKeys)
            next.RevokedKeys.Add(id);

        bool changed = !sameAsStored || !next.RevokedKeys.SetEquals(state.RevokedKeys);
        return NevrManifestDecision.Accept(manifest, changed ? next : state, changed);
    }
}
