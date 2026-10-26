using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public sealed class NevrFetchResult
{
    public bool Success;

    // A sentence for the player. Set on success too (empty) so callers can show it as is.
    public string Message;
    public NevrManifest Manifest;
}

// Fetches the signed manifest and its signature, decides whether it may be used, and
// stores what the installer must remember. Every failure ends in a message for the
// player; nothing falls back to an older or unsigned list.
public static class NevrManifestClient
{
    private const int TimeoutSeconds = 20;
    private const int SignatureRetries = 2;
    private const float RetryDelaySeconds = 3f;

    public static string StateDirectory => Path.Combine(Application.persistentDataPath, "nevr");

    public static IEnumerator Fetch(Action<NevrFetchResult> done)
    {
        InstallerLog.Mark("NEVR_MANIFEST_FETCH_START", "url=" + NevrManifest.ManifestUrl);

        NevrTrustLoadStatus loadStatus = NevrTrustStore.Load(
            StateDirectory, out NevrTrustState state, out string loadError);
        if (loadStatus == NevrTrustLoadStatus.Damaged)
        {
            InstallerLog.Error("NEVR_STATE_DAMAGED | " + loadError);
            done(Fail("The installer's saved update information is damaged, so it cannot check that the update list is not older than one it has seen. Clear this app's data in the headset's settings and try again."));
            yield break;
        }

        NevrManifestDecision decision = null;
        for (int attempt = 0; attempt <= SignatureRetries; attempt++)
        {
            var manifestGet = new GetResult();
            yield return Get(NevrManifest.ManifestUrl, NevrManifest.MaxManifestBytes, manifestGet);
            if (manifestGet.Error != null)
            {
                done(Fail(manifestGet.Error));
                yield break;
            }

            var signatureGet = new GetResult();
            yield return Get(NevrManifest.SignatureUrl, MinisignVerifier.MaxSignatureBytes, signatureGet);
            if (signatureGet.Error != null)
            {
                done(Fail(signatureGet.Error));
                yield break;
            }

            if (!NevrTrustedClock.TryGet(DateTime.UtcNow, manifestGet.DateHeader, out DateTime now, out string clockError))
            {
                InstallerLog.Error("NEVR_CLOCK_REFUSED | " + clockError);
                done(Fail(clockError));
                yield break;
            }

            string signatureText;
            try
            {
                signatureText = new UTF8Encoding(false, true).GetString(signatureGet.Bytes);
            }
            catch (Exception)
            {
                signatureText = null;
            }

            decision = NevrManifestPolicy.Evaluate(
                manifestGet.Bytes, signatureText, state, NevrPinnedKeys.All,
                NevrPinnedKeys.MinManifestVersion, now);

            bool signatureProblem = !decision.Accepted &&
                (decision.Failure == NevrManifestFailure.SignatureInvalid ||
                 decision.Failure == NevrManifestFailure.SignatureMalformed);
            if (!signatureProblem || attempt == SignatureRetries)
                break;

            // A signature that does not match can be a cache serving the manifest and its
            // signature from two different moments. Fetch the pair again.
            InstallerLog.Warning("NEVR_SIGNATURE_RETRY | attempt=" + (attempt + 1) + " | " + decision.Failure);
            yield return new WaitForSecondsRealtime(RetryDelaySeconds);
        }

        if (!decision.Accepted)
        {
            InstallerLog.Error("NEVR_MANIFEST_REFUSED | " + decision.Failure + " | " + decision.Message);
            done(Fail(decision.Message));
            yield break;
        }

        if (decision.StateChanged &&
            !NevrTrustStore.TrySave(StateDirectory, decision.NewState, out string saveError))
        {
            InstallerLog.Error("NEVR_STATE_SAVE_FAILED | " + saveError);
            done(Fail("The installer could not save the update list it just checked (" + saveError + "), so it will not use it."));
            yield break;
        }

        InstallerLog.Mark("NEVR_MANIFEST_ACCEPTED",
            "manifestVersion=" + decision.Manifest.ManifestVersion +
            " | stateChanged=" + decision.StateChanged);
        done(new NevrFetchResult { Success = true, Message = string.Empty, Manifest = decision.Manifest });
    }

    private static NevrFetchResult Fail(string message)
    {
        return new NevrFetchResult { Success = false, Message = message };
    }

    private sealed class GetResult
    {
        public byte[] Bytes;
        public string DateHeader;
        public string Error;
    }

    private static IEnumerator Get(string url, long limit, GetResult result)
    {
        var buffer = new MemoryStream();
        var sink = new NevrDownloadSink(buffer, limit, false);
        using (var handler = new NevrCappedDownloadHandler(sink))
        using (var request = UnityWebRequest.Get(url))
        {
            request.timeout = TimeoutSeconds;
            request.downloadHandler = handler;
            yield return request.SendWebRequest();

            if (sink.Error != null)
            {
                InstallerLog.Error("NEVR_GET_REFUSED | " + url + " | " + sink.Error);
                result.Error = "The update server's answer was not what the installer expects (" + sink.Error + ")";
            }
            else if (request.result == UnityWebRequest.Result.ProtocolError)
            {
                InstallerLog.Error("NEVR_GET_HTTP | " + url + " | " + request.responseCode);
                result.Error = "The update server answered with an error (HTTP " + request.responseCode + "). Try again later.";
            }
            else if (request.result != UnityWebRequest.Result.Success)
            {
                InstallerLog.Error("NEVR_GET_FAILED | " + url + " | " + request.result + " | " + request.error);
                result.Error = "Could not reach the update server (" + request.error + "). Check the headset's internet connection and try again.";
            }
            else if (!sink.Complete(out _))
            {
                result.Error = "The update server's answer could not be saved: " + sink.Error;
            }
            else
            {
                result.Bytes = buffer.ToArray();
                result.DateHeader = request.GetResponseHeader("Date");
            }
        }

        sink.Dispose();
    }
}
