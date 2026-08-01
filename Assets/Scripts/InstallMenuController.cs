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

    [Header("Legacy flow")]
    public string legacyApkUrl = "http://files.echovr.de/echo_quest_16-07-2026.001.apk"; 

    public ApkInstaller installer;

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
        mainMenuPanel.SetActive(false);
        downloadPanel.SetActive(true);
        installer.DownloadAndInstallFromUrl(legacyApkUrl);
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
        patchedInputPanel.SetActive(false);
        downloadPanel.SetActive(true);
        installer.DownloadAndInstallFromUrl(extractedUrl);
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

        Match match = UrlPattern.Match(message);
        if (!match.Success)
            return null;

        string url = match.Value.TrimEnd('.', ',', ')', ']', '"', '\'');

        if (!url.ToLower().Contains(".apk"))
            return null;

        return url;
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
