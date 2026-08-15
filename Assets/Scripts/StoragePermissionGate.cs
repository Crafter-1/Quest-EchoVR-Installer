using UnityEngine;
using TMPro;
using System.Collections;

/// Shown first, before the install-type menu. Blocks progress until the user
/// grants "All files access" (MANAGE_EXTERNAL_STORAGE), which the later
/// download/extraction step needs to write into Echo VR's OBB folder.
public class StoragePermissionGate : MonoBehaviour
{
    [Header("Panels")]
    public GameObject permissionPanel;
    public GameObject mainMenuPanel; // InstallSelectObject

    [Header("UI")]
    public TMP_Text statusText;
    public UnityEngine.UI.Button grantButton;

    private void Start()
    {
        if (grantButton != null)
            grantButton.onClick.AddListener(OnGrantClicked);

#if UNITY_ANDROID && !UNITY_EDITOR
        if (HasAllFilesAccess())
        {
            SkipToMenu();
        }
        else
        {
            ShowPermissionScreen();
        }
#else
        // Editor/Play Mode - no real permission system, just skip straight through
        SkipToMenu();
#endif
    }

    private void ShowPermissionScreen()
    {
        permissionPanel.SetActive(true);
        mainMenuPanel.SetActive(false);

        if (statusText != null)
            statusText.text = "This app needs storage access to install Echo VR.\nTap below to grant it.";
    }

    public void OnGrantClicked()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        OpenAllFilesAccessSettings();
        StartCoroutine(PollForPermission());
#else
        SkipToMenu();
#endif
    }

    private IEnumerator PollForPermission()
    {
        if (statusText != null)
            statusText.text = "Waiting for permission...\n(grant it in Settings, then return here)";

#if UNITY_ANDROID && !UNITY_EDITOR
        while (!HasAllFilesAccess())
            yield return new WaitForSeconds(1f);
#else
        yield return null;
#endif

        SkipToMenu();
    }

    private void SkipToMenu()
    {
        permissionPanel.SetActive(false);
        mainMenuPanel.SetActive(true);
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private bool HasAllFilesAccess()
    {
        using var environment = new AndroidJavaClass("android.os.Environment");
        return environment.CallStatic<bool>("isExternalStorageManager");
    }

    private void OpenAllFilesAccessSettings()
    {
        using var uriClass = new AndroidJavaClass("android.net.Uri");
        using var uri = uriClass.CallStatic<AndroidJavaObject>(
            "parse", "package:" + Application.identifier);

        using var intent = new AndroidJavaObject(
            "android.content.Intent",
            "android.settings.MANAGE_APP_ALL_FILES_ACCESS_PERMISSION", uri);

        using var playerClass = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
        using var activity = playerClass.GetStatic<AndroidJavaObject>("currentActivity");
        activity.Call("startActivity", intent);
    }
#endif
}