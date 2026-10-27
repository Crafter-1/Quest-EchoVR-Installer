using System;
using System.IO;
using System.Security.Cryptography;

// Receives a download in pieces, never takes more bytes than the manifest allows, and
// hashes as it goes. It writes to any stream: a file for the APK, memory for the manifest.
public sealed class NevrDownloadSink : IDisposable
{
    private readonly Stream _target;
    private readonly long _limit;
    private readonly bool _requireExact;
    private readonly SHA256 _hash = SHA256.Create();
    private bool _finished;

    public long Received { get; private set; }
    public string Error { get; private set; }

    // `limit` is the most bytes accepted. With `requireExact` the download must also end at
    // exactly `limit` bytes (an APK of the size the manifest gave).
    public NevrDownloadSink(Stream target, long limit, bool requireExact)
    {
        _target = target;
        _limit = limit;
        _requireExact = requireExact;
    }

    // A server that announces more bytes than the limit is refused before any are taken.
    // A shorter announcement is allowed (a compressed transfer announces its compressed
    // size); an exact size is enforced when the download completes.
    public bool AcceptAnnouncedLength(long announced)
    {
        if (announced > _limit)
        {
            Error = $"The server announced {announced} bytes where at most {_limit} were expected.";
            return false;
        }

        return true;
    }

    // Returns false, and stops accepting, when the bytes would pass the limit.
    public bool Write(byte[] buffer, int count)
    {
        if (Error != null || _finished)
            return false;

        if (count < 0 || Received + count > _limit)
        {
            Error = $"The download was larger than the {_limit} bytes expected, so it was stopped.";
            return false;
        }

        try
        {
            _target.Write(buffer, 0, count);
            _hash.TransformBlock(buffer, 0, count, null, 0);
            Received += count;
            return true;
        }
        catch (Exception exception)
        {
            Error = "Could not save the download: " + exception.Message;
            return false;
        }
    }

    // Ends the download. Fails when it is short of an exact size.
    public bool Complete(out string sha256Hex)
    {
        sha256Hex = null;
        if (Error != null)
            return false;

        if (_requireExact && Received != _limit)
        {
            Error = $"The download ended after {Received} of {_limit} bytes.";
            return false;
        }

        try
        {
            _hash.TransformFinalBlock(new byte[0], 0, 0);
            _target.Flush();
            _finished = true;
            sha256Hex = NevrHex.Encode(_hash.Hash);
            return true;
        }
        catch (Exception exception)
        {
            Error = "Could not save the download: " + exception.Message;
            return false;
        }
    }

    public void Dispose()
    {
        _hash.Dispose();
        _target.Dispose();
    }
}
