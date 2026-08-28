using UnityEngine;
using TMPro;
using System.Text.RegularExpressions;

public class InstallMenuController : MonoBehaviour
{
    [Header("Panels")]
    public GameObject mainMenuPanel;      // Legacy / Patched choice
    public GameObject patchedInputPanel;  // link entry screen
    public GameObject downloadPanel;

    [Header("Patched flow")]
    public TMP_InputField messageInput;
    public TMP_Text errorText;

    [Header("Install flow")]
    public ApkInstaller installer;
    public QuestUpdateManager updateManager;

    [Header("Discord link")]
    public string discordUrl = "https://discord.gg/Rs8CQvjDv";
    public void OnOpenDiscord()
{
    Application.OpenURL(discordUrl);
}

    private static readonly Regex UrlPattern = new Regex(@"https?://[^\s]+", RegexOptions.IgnoreCase);

    // Called by Button_Legacy's OnClick
    public void OnSelectLegacy()
    {
        QuestUpdateManager manager = GetUpdateManager();
        mainMenuPanel.SetActive(false);
        downloadPanel.SetActive(true);

        if (installer == null)
            installer = FindFirstObjectByType<ApkInstaller>();

        if (installer == null)
        {
            ShowError("Could not find the APK installer.");
            return;
        }

        installer.SetStatusMessage("Checking the Echo update manifest...");
        manager.EnsureManifest((success, error) =>
        {
            if (!success)
            {
                installer.SetStatusMessage(error);
                return;
            }

            if (manager.IsManifestBaseApkInstalled() && ApkInstaller.IsEchoVrInstalled())
            {
                installer.SkipApkInstallBecauseCurrent();
                return;
            }

            installer.DownloadAndInstallFromManifest(
                manager.CurrentManifest,
                manager.GetBaseApkMirrors());
        });
    }

    // Called by Button_Patched's OnClick
    public void OnSelectPatched()
    {
        mainMenuPanel.SetActive(false);
        patchedInputPanel.SetActive(true);
    }

    // Called by SubmitButton's OnClick (inside PatchedInputPanel)
    public void OnSubmitPatchedLink()
    {
        string rawMessage = messageInput.text;
        string extractedUrl = ExtractApkUrl(rawMessage);

        if (string.IsNullOrEmpty(extractedUrl))
        {
            ShowError("APK download failed. Did you enter the correct link?");
            return;
        }

        ClearError();
        QuestUpdateManager manager = GetUpdateManager();
        patchedInputPanel.SetActive(false);
        downloadPanel.SetActive(true);

        if (installer == null)
            installer = FindFirstObjectByType<ApkInstaller>();

        if (installer == null)
        {
            ShowError("Could not find the APK installer.");
            return;
        }

        installer.SetStatusMessage("Checking the Echo update manifest...");
        manager.EnsureManifest((success, error) =>
        {
            if (!success)
            {
                installer.SetStatusMessage(error);
                return;
            }

            if (manager.IsManifestBaseApkInstalled() && ApkInstaller.IsEchoVrInstalled())
            {
                installer.SkipApkInstallBecauseCurrent();
                return;
            }

            installer.DownloadAndInstallPatchedFromUrl(
                extractedUrl,
                manager.CurrentManifest);
        });
    }

    // Optional: back button from the patched input screen to main menu
    public void OnBackToMenu()
    {
        patchedInputPanel.SetActive(false);
        mainMenuPanel.SetActive(true);
        ClearError();
    }

    private string ExtractApkUrl(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        foreach (Match match in UrlPattern.Matches(message))
        {
            string url = match.Value.TrimEnd('.', ',', ')', ']', '"', '\'');
            if (url.IndexOf(".apk", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return url;
        }

        return null;
    }

    private QuestUpdateManager GetUpdateManager()
    {
        if (updateManager == null)
            updateManager = FindFirstObjectByType<QuestUpdateManager>();

        if (updateManager == null)
            updateManager = gameObject.AddComponent<QuestUpdateManager>();

        return updateManager;
    }

    private void ShowError(string message)
    {
        if (errorText != null)
        {
            errorText.text = message;
            errorText.gameObject.SetActive(true);
        }
    }

    private void ClearError()
    {
        if (errorText != null)
            errorText.gameObject.SetActive(false);
    }
}
