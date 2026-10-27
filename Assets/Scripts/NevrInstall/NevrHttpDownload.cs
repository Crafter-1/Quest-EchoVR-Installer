using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

public sealed class NevrHttpOutcome
{
    // A sentence for the player; null when the download finished and was kept whole.
    public string Failure;

    // Set when the failure is the server's own refusal rather than the network, so callers
    // can log it differently.
    public long ResponseCode;
    public string Sha256;
    public string DateHeader;
    public long Received;

    // For a resumed download: how many bytes at the start of the target are known to be the
    // start of the file (the bytes kept before, plus those received under a good answer).
    // Anything past it, such as an error page, must be cut off before the next try.
    public long TrustedBytes;

    // The server answered a Range request with the whole file. The target is no use; start over
    // without a Range.
    public bool RangeIgnored;
}

// One GET into a stream with the rules every download in the installer shares: https only
// (the URL is built from pinned constants), no redirects followed, a byte cap, a stall
// watchdog and a total time limit. The stream is closed when the download ends.
//
// With `resumeFrom` above 0 the target already holds that many bytes of the file (`limit` is
// the whole file's size, with `requireExact`). The request asks for the rest with a Range
// header and only a 206 for exactly that range is kept; a 200 means the server ignored the
// range, which sets outcome.RangeIgnored. The hash in the outcome then covers only the new bytes.
public static class NevrHttpDownload
{
    public static IEnumerator Get(
        string url,
        Stream target,
        long limit,
        bool requireExact,
        int requestTimeoutSeconds,
        double stallSeconds,
        double totalSeconds,
        NevrHttpOutcome outcome,
        System.Action<long> onProgress = null,
        long resumeFrom = 0)
    {
        if (!url.StartsWith("https://", System.StringComparison.Ordinal))
        {
            target.Dispose();
            outcome.Failure = "The installer only downloads over https, so it refused " + url + ".";
            yield break;
        }

        var sink = new NevrDownloadSink(target, limit, requireExact, resumeFrom);
        var watchdog = new NevrStallWatchdog(stallSeconds, totalSeconds, Time.realtimeSinceStartupAsDouble);
        string watchdogFailure = null;

        using (var handler = new NevrCappedDownloadHandler(sink))
        using (var request = UnityWebRequest.Get(url))
        {
            request.timeout = requestTimeoutSeconds;
            request.redirectLimit = NevrHttpRules.RedirectLimit;
            request.downloadHandler = handler;
            if (resumeFrom > 0)
                request.SetRequestHeader("Range", NevrGameData.RangeHeader(resumeFrom));
            UnityWebRequestAsyncOperation operation = request.SendWebRequest();

            while (!operation.isDone)
            {
                onProgress?.Invoke(sink.Received);

                // A server that ignores the Range header starts the file over: stop before
                // it fills the disk with a second copy.
                if (resumeFrom > 0 && NevrGameData.ServerIgnoredRange(request.responseCode))
                {
                    outcome.RangeIgnored = true;
                    watchdogFailure = "The server does not resume downloads, so the game data download starts again from the beginning.";
                    request.Abort();
                    break;
                }

                watchdogFailure = watchdog.Check(sink.Received, Time.realtimeSinceStartupAsDouble);
                if (watchdogFailure != null)
                {
                    request.Abort();
                    break;
                }

                yield return null;
            }

            // An aborted request finishes on the next frame; let it settle before reading it.
            while (watchdogFailure != null && !request.isDone)
                yield return null;

            outcome.ResponseCode = request.responseCode;
            outcome.Received = sink.Received;

            // Bytes are only the file's if the answer was the one asked for.
            string rangeProblem = resumeFrom > 0
                ? NevrGameData.CheckRangeResponse(request.responseCode, request.GetResponseHeader("Content-Range"), resumeFrom, limit)
                : null;
            bool bodyIsFile = resumeFrom > 0 ? rangeProblem == null : request.responseCode == 200;
            outcome.TrustedBytes = outcome.RangeIgnored ? 0 : bodyIsFile ? sink.Received : resumeFrom;

            if (sink.Error != null)
                outcome.Failure = sink.Error;
            else if (watchdogFailure != null)
                outcome.Failure = watchdogFailure;
            else if (request.responseCode >= 300 && request.responseCode < 400)
                outcome.Failure = NevrHttpRules.RefusalFor(request.responseCode);
            else if (request.result == UnityWebRequest.Result.ProtocolError)
                outcome.Failure = "The update server answered with an error (HTTP " + request.responseCode + "). Try again later.";
            else if (request.result != UnityWebRequest.Result.Success)
                outcome.Failure = "Could not reach the update server (" + request.error + "). Check the headset's internet connection and try again.";
            else if (resumeFrom > 0 && rangeProblem != null)
                outcome.Failure = rangeProblem;
            else if (resumeFrom == 0 && NevrHttpRules.RefusalFor(request.responseCode) != null)
                outcome.Failure = NevrHttpRules.RefusalFor(request.responseCode);
            else if (!sink.Complete(out string sha256))
                outcome.Failure = sink.Error;
            else
            {
                outcome.Sha256 = sha256;
                outcome.DateHeader = request.GetResponseHeader("Date");
            }
        }

        sink.Dispose();
    }
}
