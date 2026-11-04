using UnityEngine;

public class ExtrasPanelController : MonoBehaviour
{
    [SerializeField] private GameObject mainPanel;    // the panel you're coming FROM
    [SerializeField] private GameObject extrasPanel;  // the new optional-stuff panel

    private void Start()
    {
        if (extrasPanel != null) extrasPanel.SetActive(false);
    }

    // wire the "Extras" button on the main panel to this
    public void OpenExtras()
    {
        if (mainPanel != null) mainPanel.SetActive(false);
        if (extrasPanel != null) extrasPanel.SetActive(true);
        Debug.Log("[ExtrasPanelController] OpenExtras invoked.");
    }

    // wire a "Back" button on the extras panel to this
    public void CloseExtras()
    {
        if (extrasPanel != null) extrasPanel.SetActive(false);
                if (mainPanel != null) mainPanel.SetActive(true);
    }

    // Echo's package name: use the SAME string AfterInstallController uses to launch Echo,
    // so the two can never disagree.
    [SerializeField] private string echoPackageName = "com.readyatdawn.r15";

    // wire the "Uninstall" button to this. Opens Android's own uninstall dialog;
    // the player confirms, we never delete anything ourselves.
    public void UninstallEcho()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var uriClass = new AndroidJavaClass("android.net.Uri"))
            using (var uri = uriClass.CallStatic<AndroidJavaObject>("parse", "package:" + echoPackageName))
            using (var intent = new AndroidJavaObject("android.content.Intent", "android.intent.action.DELETE", uri))
                        {
                activity.Call("startActivity", intent);
            }
            pendingUninstallCheck = true; // checked when we get focus back (OnApplicationFocus)
            Debug.Log("[ExtrasPanelController] Uninstall prompt opened for " + echoPackageName);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[ExtrasPanelController] Could not open uninstall prompt: " + e);
        }
#else
        Debug.Log("[ExtrasPanelController] UninstallEcho only works on the headset.");
#endif
    }

    // the panel to return to once Echo is gone (InstallSelectObject in the scene)
    [SerializeField] private GameObject installPanel;
    private bool pendingUninstallCheck;

    // Android's uninstall dialog runs on top of us; we get focus back when it closes,
    // whether the player confirmed or cancelled. That's the moment to check.
    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus || !pendingUninstallCheck) return;
        pendingUninstallCheck = false;

        if (IsEchoInstalled())
        {
            Debug.Log("[ExtrasPanelController] Echo is still installed (uninstall cancelled).");
            return;
        }

        Debug.Log("[ExtrasPanelController] Echo is gone, returning to the install panel.");
        if (extrasPanel != null) extrasPanel.SetActive(false);
        if (installPanel != null) installPanel.SetActive(true);
    }

    private bool IsEchoInstalled()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var pm = activity.Call<AndroidJavaObject>("getPackageManager"))
            using (pm.Call<AndroidJavaObject>("getPackageInfo", echoPackageName, 0))
            {
                return true; // getPackageInfo throws NameNotFoundException when it's gone
            }
        }
        catch (System.Exception)
        {
            return false;
        }
#else
        return true;
#endif
    }
}
