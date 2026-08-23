using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class StoragePermissionGate : MonoBehaviour
{
    [Header("Panels")]
    public GameObject permissionPanel;
    public GameObject mainMenuPanel;

    [Header("UI")]
    public TMP_Text statusText;
    public Button grantButton;

    private bool _gateCompleted;

    private void Start()
    {
        if (grantButton != null)
            grantButton.onClick.AddListener(RequestStoragePermission);

        RefreshPermissionState();
    }

    private void OnDestroy()
    {
        if (grantButton != null)
            grantButton.onClick.RemoveListener(RequestStoragePermission);
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        // Android returns focus to Unity after the user leaves the settings page.
        // Once the initial gate has completed, later focus changes (APK installer,
        // Echo settings, etc.) must not reopen the original selection menu.
        if (hasFocus && !_gateCompleted)
            RefreshPermissionState();
    }

    public void RequestStoragePermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var settings = new AndroidJavaClass("android.provider.Settings");
            using var uri = new AndroidJavaClass("android.net.Uri")
                .CallStatic<AndroidJavaObject>("parse", "package:" + Application.identifier);
            using var intent = new AndroidJavaObject(
                "android.content.Intent",
                settings.GetStatic<string>("ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION"),
                uri);

            activity.Call("startActivity", intent);
            SetStatus("Enable 'Allow access to manage all files', then return to the installer.");
        }
        catch (System.Exception exception)
        {
            Debug.LogError($"[StoragePermissionGate] Could not open storage settings: {exception}");
            SetStatus("Could not open storage settings. Please enable file access in Android settings.");
        }
#else
        // Treat the Editor as permitted so the rest of the UI can be tested.
        ShowMainMenu();
#endif
    }

    private void RefreshPermissionState()
    {
        if (HasStoragePermission())
            ShowMainMenu();
        else
            ShowPermissionPrompt();
    }

    private static bool HasStoragePermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var environment = new AndroidJavaClass("android.os.Environment");
            return environment.CallStatic<bool>("isExternalStorageManager");
        }
        catch (System.Exception exception)
        {
            Debug.LogError($"[StoragePermissionGate] Could not check storage permission: {exception}");
            return false;
        }
#else
        return true;
#endif
    }

    private void ShowPermissionPrompt()
    {
        if (permissionPanel != null)
            permissionPanel.SetActive(true);

        if (mainMenuPanel != null)
            mainMenuPanel.SetActive(false);

        SetStatus("Storage access is required to install Echo VR data.");
    }

    private void ShowMainMenu()
    {
        _gateCompleted = true;

        if (permissionPanel != null)
            permissionPanel.SetActive(false);

        if (mainMenuPanel != null)
            mainMenuPanel.SetActive(true);

        SetStatus("Storage access granted.");
    }

    private void SetStatus(string message)
    {
        if (statusText != null)
            statusText.text = message;

        Debug.Log("[StoragePermissionGate] " + message);
    }
}
