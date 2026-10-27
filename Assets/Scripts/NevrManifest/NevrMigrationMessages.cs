// Sentences the player sees when the Echo VR on the headset is not ours.
public static class NevrMigrationMessages
{
    public const int SecondsToRead = 20;

    // Shown before the installer asks Android to remove the old Echo VR. It says what will
    // happen and only what has been checked: whether the game data survives the removal
    // has not been verified on a headset (EchoTools/nevr-runtime#965).
    public static string BeforeUninstall(int secondsLeft)
    {
        return
            "The Echo VR on this headset came from the community installer and is signed with a different key. " +
            "Android will not replace it with the official build, so it has to be uninstalled first, then the official build is installed.\n\n" +
            "The official build is already downloaded and checked. Android will now ask you to confirm the uninstall; choose Cancel to keep what you have.\n\n" +
            "Your game data: it is not verified that it survives the uninstall. The game data has already been downloaded again; " +
            "once the official build is installed, this installer extracts it.\n\n" +
            "Asking Android in " + secondsLeft + "...";
    }

    // The uninstall went through but the new APK could not be installed: the headset has no
    // Echo VR now, and the player needs to know that and how to retry.
    public static string RemovedButNotInstalled(string detail)
    {
        return
            "The old Echo VR was removed, but the official build could not be installed (" + detail + ").\n\n" +
            "To try again, close this installer, open it again and choose Install. " +
            "A downloaded APK that still matches is kept and checked again; the game data download is used as it is.";
    }

    public const string StillInstalled =
        "Echo VR is still installed, so nothing was changed. Start the install again to retry.";

    public const string Removed =
        "The old Echo VR was removed. Installing the official build...";

    public const string CouldNotAsk =
        "The installer could not ask Android to uninstall the old Echo VR, so nothing was changed. " +
        "You can uninstall it yourself in the headset's app library, then run this installer again.";
}

public static class NevrMigrationRules
{
    // Moving to the official build can remove the old game data along with the old app, and
    // the notice tells the player the data was downloaded again. So a migration always runs
    // the data download first; an APK-only update never replaces another signer's app.
    public static bool IsValid(bool replaceOtherSigner, bool apkOnlyUpdate)
    {
        return !(replaceOtherSigner && apkOnlyUpdate);
    }
}
