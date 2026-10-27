// Ends a download that has stopped receiving bytes, or has run for too long overall, so a
// dead connection cannot leave the player on a progress screen forever.
public sealed class NevrStallWatchdog
{
    private readonly double _stallSeconds;
    private readonly double _totalSeconds;
    private readonly double _startSeconds;
    private long _lastReceived;
    private double _lastProgressSeconds;

    public NevrStallWatchdog(double stallSeconds, double totalSeconds, double startSeconds)
    {
        _stallSeconds = stallSeconds;
        _totalSeconds = totalSeconds;
        _startSeconds = startSeconds;
        _lastProgressSeconds = startSeconds;
    }

    // Returns null while the download is healthy, otherwise a sentence for the player.
    public string Check(long received, double nowSeconds)
    {
        if (received != _lastReceived)
        {
            _lastReceived = received;
            _lastProgressSeconds = nowSeconds;
        }

        if (nowSeconds - _startSeconds > _totalSeconds)
            return "The download was taking too long and was stopped. Check the headset's internet connection and try again.";

        if (nowSeconds - _lastProgressSeconds > _stallSeconds)
            return $"The download stopped receiving data for {_stallSeconds:0} seconds and was stopped. Check the headset's internet connection and try again.";

        return null;
    }
}
