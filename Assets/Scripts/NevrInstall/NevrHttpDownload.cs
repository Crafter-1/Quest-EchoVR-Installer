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
}

// One GET into a stream with the rules every download in the installer shares: https only
// (the URL is built from pinned constants), no redirects followed, a byte cap, a stall
// watchdog and a total time limit. The stream is closed when the download ends.
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
        System.Action<long> onProgress = null)
    {
        if (!url.StartsWith("https://", System.StringComparison.Ordinal))
        {
            target.Dispose();
            outcome.Failure = "The installer only downloads over https, so it refused " + url + ".";
            yield break;
        }

        var sink = new NevrDownloadSink(target, limit, requireExact);
        var watchdog = new NevrStallWatchdog(stallSeconds, totalSeconds, Time.realtimeSinceStartupAsDouble);
        string watchdogFailure = null;

        using (var handler = new NevrCappedDownloadHandler(sink))
        using (var request = UnityWebRequest.Get(url))
        {
            request.timeout = requestTimeoutSeconds;
            request.redirectLimit = 0;
            request.downloadHandler = handler;
            UnityWebRequestAsyncOperation operation = request.SendWebRequest();

            while (!operation.isDone)
            {
                onProgress?.Invoke(sink.Received);
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
            else if (NevrHttpRules.RefusalFor(request.responseCode) != null)
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
