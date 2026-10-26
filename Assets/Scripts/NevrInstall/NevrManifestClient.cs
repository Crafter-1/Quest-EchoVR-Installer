using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;

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
    private const double StallSeconds = 15;
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
        var outcome = new NevrHttpOutcome();
        yield return NevrHttpDownload.Get(
            url, buffer, limit, false, TimeoutSeconds, StallSeconds, TimeoutSeconds, outcome);

        if (outcome.Failure != null)
        {
            InstallerLog.Error("NEVR_GET_FAILED | " + url + " | http=" + outcome.ResponseCode + " | " + outcome.Failure);
            result.Error = outcome.Failure;
            yield break;
        }

        result.Bytes = buffer.ToArray();
        result.DateHeader = outcome.DateHeader;
    }
}
