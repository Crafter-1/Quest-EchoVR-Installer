using UnityEngine.Networking;

// Feeds a UnityWebRequest's body into a NevrDownloadSink, so the download stops as soon as
// it would pass the size the manifest allows.
public sealed class NevrCappedDownloadHandler : DownloadHandlerScript
{
    private readonly NevrDownloadSink _sink;

    public NevrCappedDownloadHandler(NevrDownloadSink sink) : base(new byte[64 * 1024])
    {
        _sink = sink;
    }

    // The base method cannot abort, so a too-large announcement is recorded in the sink,
    // and the first ReceiveData call then stops the download.
    protected override void ReceiveContentLengthHeader(ulong contentLength)
    {
        _sink.AcceptAnnouncedLength(contentLength > long.MaxValue ? long.MaxValue : (long)contentLength);
    }

    protected override bool ReceiveData(byte[] data, int dataLength)
    {
        return _sink.Write(data, dataLength);
    }

    protected override byte[] GetData()
    {
        return null;
    }
}
