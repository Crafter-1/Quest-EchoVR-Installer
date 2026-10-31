using System;
using System.Collections.Generic;

public enum NevrInstallAction
{
    Install,
    Update,
    AlreadyCurrent,
    NewerInstalled,
    DifferentSigner
}

// Picks the Quest APK a channel installs, and checks an APK file's own details against
// what the signed manifest says it must be.
public static class NevrQuestRelease
{
    public const string DefaultChannel = "stable";
    public const string EchoPackageName = "com.readyatdawn.r15";

    public static bool TryPick(
        NevrManifest manifest,
        string channel,
        out NevrRelease release,
        out NevrArtifact apk,
        out string error)
    {
        release = null;
        apk = null;
        error = null;

        if (!manifest.Channels.TryGetValue(channel, out string version))
        {
            error = $"The update list has no Echo VR release on the '{channel}' channel yet.";
            return false;
        }

        if (!manifest.Releases.TryGetValue(version, out NevrRelease found))
        {
            error = $"The update list names release {version} but does not contain it.";
            return false;
        }

        NevrArtifact match = null;
        foreach (NevrArtifact artifact in found.Artifacts)
        {
            if (artifact.Kind != "quest-apk" || artifact.Platform != "android-arm64")
                continue;

            if (match != null)
            {
                error = $"Release {version} lists more than one Quest APK, so the installer cannot tell which to use.";
                return false;
            }

            match = artifact;
        }

        if (match == null)
        {
            error = $"Release {version} has no Quest APK.";
            return false;
        }

        release = found;
        apk = match;
        return true;
    }

    // Compares what Android reports about an APK file with the manifest. Returns null when
    // everything matches, otherwise a sentence for the player. The manifest must name the
    // signer certificate: without one there is nothing to check the signer against.
    public static string CheckApk(
        NevrArtifact artifact,
        string packageName,
        long versionCode,
        IReadOnlyList<string> signerCertSha256)
    {
        if (string.IsNullOrEmpty(artifact.AndroidSignerCertSha256))
            return "The update list does not say who signed this APK, so the installer will not install it.";

        if (!string.Equals(packageName, artifact.AndroidPackage, StringComparison.Ordinal) ||
            !string.Equals(packageName, EchoPackageName, StringComparison.Ordinal))
            return $"The downloaded file is the app '{packageName}', not Echo VR, so it was not installed.";

        if (versionCode != artifact.AndroidVersionCode)
            return $"The downloaded APK is version code {versionCode} but the update list says {artifact.AndroidVersionCode}, so it was not installed.";

        if (signerCertSha256 == null || signerCertSha256.Count != 1)
            return "The downloaded APK does not have exactly one signer, so it was not installed.";

        if (!string.Equals(signerCertSha256[0], artifact.AndroidSignerCertSha256, StringComparison.OrdinalIgnoreCase))
            return "The downloaded APK was signed by a different key than the update list names, so it was not installed.";

        return null;
    }

    // After Android's installer returns: the package on the headset must be the one that was
    // verified before the install, signed by the key the manifest named. A matching version
    // code alone is not enough. Returns null when it is, otherwise a sentence for the player.
    public static string CheckInstalled(
        string expectedSignerCertSha256,
        long expectedVersionCode,
        bool installed,
        long installedVersionCode,
        IReadOnlyList<string> installedSignerCertSha256)
    {
        if (!installed)
            return "Echo VR is not installed on this headset.";

        if (string.IsNullOrEmpty(expectedSignerCertSha256))
            return "The installer has no record of who should have signed the Echo VR it installed, so it cannot confirm the install.";

        if (installedVersionCode != expectedVersionCode)
            return $"The Echo VR on this headset is version code {installedVersionCode}, not the {expectedVersionCode} that was downloaded.";

        if (installedSignerCertSha256 == null || installedSignerCertSha256.Count != 1 ||
            !string.Equals(installedSignerCertSha256[0], expectedSignerCertSha256, StringComparison.OrdinalIgnoreCase))
            return "The Echo VR now on this headset is signed by a different key than the update list names, " +
                   "so it is not the release the installer verified. Do not use it: uninstall Echo VR and run the installer again.";

        return null;
    }

    // True when every certificate Android reports for an installed package is the one the
    // manifest names for our APK. Android only updates a package in place when the new APK
    // has the same signer.
    public static bool IsSignedByUs(NevrArtifact artifact, IReadOnlyList<string> installedSignerCertSha256)
    {
        if (string.IsNullOrEmpty(artifact.AndroidSignerCertSha256) ||
            installedSignerCertSha256 == null || installedSignerCertSha256.Count != 1)
            return false;

        return string.Equals(
            installedSignerCertSha256[0], artifact.AndroidSignerCertSha256, StringComparison.OrdinalIgnoreCase);
    }

    // What to do given what is on the headset now. Android only updates an installed
    // package in place when the new APK has the same signer, so an installed Echo VR
    // signed by anyone else (the community installer's patched APK) is its own case.
    public static NevrInstallAction Plan(
        NevrArtifact artifact,
        bool installed,
        long installedVersionCode,
        IReadOnlyList<string> installedSignerCertSha256)
    {
        if (!installed)
            return NevrInstallAction.Install;

        if (!IsSignedByUs(artifact, installedSignerCertSha256))
            return NevrInstallAction.DifferentSigner;

        if (installedVersionCode == artifact.AndroidVersionCode)
            return NevrInstallAction.AlreadyCurrent;

        return installedVersionCode < artifact.AndroidVersionCode
            ? NevrInstallAction.Update
            : NevrInstallAction.NewerInstalled;
    }
}
