// Sentences the player sees when the Echo VR on the headset is not ours.
public static class NevrMigrationMessages
{
    // Placeholder until the move from the community installer's APK is added: the
    // installer says what is wrong and does nothing.
    public const string DifferentSigner =
        "The Echo VR on this headset was installed from a different source (it is signed with a different key), " +
        "so Android cannot update it in place. The installer has not changed anything.";
}
