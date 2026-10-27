// The install flow, including the game-data download that starts when the download screen
// opens, runs only after the signed manifest has been fetched and accepted. The menu opens
// the gate on acceptance and closes it again at the start of every attempt.
public static class NevrInstallGate
{
    public static bool IsOpen { get; private set; }

    public static void Close()
    {
        IsOpen = false;
    }

    public static void Open()
    {
        IsOpen = true;
    }
}
