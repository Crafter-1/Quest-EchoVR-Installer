using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Holds the verified manifest and the Quest APK the chosen channel installs.
public class NevrReleaseManager : MonoBehaviour
{
    [Tooltip("The manifest channel to install from. There is no stable release yet.")]
    public string channel = NevrQuestRelease.DefaultChannel;

    public NevrManifest CurrentManifest { get; private set; }
    public NevrRelease CurrentRelease { get; private set; }
    public NevrArtifact CurrentApk { get; private set; }

    private bool _running;
    private readonly List<Action<bool, string>> _callbacks = new List<Action<bool, string>>();

    // Always asks the server again: the answer must be current, not remembered.
    public void Refresh(Action<bool, string> onComplete)
    {
        if (onComplete != null)
            _callbacks.Add(onComplete);

        if (!_running)
            StartCoroutine(RefreshRoutine());
    }

    private IEnumerator RefreshRoutine()
    {
        _running = true;
        CurrentManifest = null;
        CurrentRelease = null;
        CurrentApk = null;

        NevrFetchResult fetched = null;
        yield return NevrManifestClient.Fetch(result => fetched = result);

        bool success = false;
        string message = fetched == null ? "The update check did not finish." : fetched.Message;
        if (fetched != null && fetched.Success)
        {
            if (NevrQuestRelease.TryPick(fetched.Manifest, channel, out NevrRelease release, out NevrArtifact apk, out message))
            {
                CurrentManifest = fetched.Manifest;
                CurrentRelease = release;
                CurrentApk = apk;
                success = true;
                InstallerLog.Mark("NEVR_RELEASE_SELECTED",
                    "channel=" + channel + " | version=" + release.Version +
                    " | versionCode=" + apk.AndroidVersionCode + " | sha256=" + apk.Sha256);
            }
            else
            {
                InstallerLog.Error("NEVR_RELEASE_NOT_SELECTED | " + message);
            }
        }

        _running = false;
        Action<bool, string>[] callbacks = _callbacks.ToArray();
        _callbacks.Clear();
        foreach (Action<bool, string> callback in callbacks)
            callback?.Invoke(success, success ? null : message);
    }
}
