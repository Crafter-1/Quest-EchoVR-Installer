using UnityEngine;
using TMPro;

public class InstallMenuController : MonoBehaviour
{
    [Header("Panels")]
    public GameObject mainMenuPanel;      // Install method choice
    public GameObject patchedInputPanel;  // retired link entry screen; hidden when installing
    public GameObject downloadPanel;

    [Header("Retired link entry (kept so the scene's references stay valid)")]
    public TMP_InputField messageInput;
    public TMP_Text errorText;

    [Header("Install flow")]
    public ApkInstaller installer;
    public QuestUpdateManager updateManager;
    public NevrReleaseManager releaseManager;

    [Header("Discord link")]
    public string discordUrl = "https://discord.gg/Rs8CQvjDv";
    public void OnOpenDiscord()
    {
        Application.OpenURL(discordUrl);
    }

    // Both install buttons start the same install of the signed APK. The scene's labels
    // still describe the old methods and are changed in the editor.
    public void OnSelectLegacy()
    {
        InstallerLog.Mark("LEGACY_SELECTION");
        BeginSignedInstall();
    }

    public void OnSelectPatched()
    {
        InstallerLog.Mark("PATCHED_SELECTION");
        BeginSignedInstall();
    }

    private void BeginSignedInstall()
    {
        if (mainMenuPanel != null)
            mainMenuPanel.SetActive(false);
        if (patchedInputPanel != null)
            patchedInputPanel.SetActive(false);
        if (downloadPanel != null)
            downloadPanel.SetActive(true);

        if (installer == null)
            installer = FindFirstObjectByType<ApkInstaller>();

        if (installer == null)
        {
            ShowError("Could not find the APK installer.");
            return;
        }

        NevrReleaseManager release = GetReleaseManager();
        installer.SetStatusMessage("Checking the Echo update list...");
        release.Refresh((success, error) =>
        {
            if (!success)
            {
                installer.SetStatusMessage(error);
                return;
            }

            NevrArtifact apk = release.CurrentApk;
            if (!NevrAndroid.TryReadInstalled(
                    NevrQuestRelease.EchoPackageName,
                    out bool installed,
                    out NevrAndroid.PackageDetails details,
                    out string readError))
            {
                InstallerLog.Error("INSTALLED_CHECK_FAILED | " + readError);
                installer.SetStatusMessage("Could not check whether Echo VR is already installed: " + readError);
                return;
            }

            NevrInstallAction action = NevrQuestRelease.Plan(
                apk, installed, installed ? details.VersionCode : 0, installed ? details.SignerCertSha256 : null);
            InstallerLog.Mark("INSTALL_PLAN", "action=" + action);
            switch (action)
            {
                case NevrInstallAction.Install:
                case NevrInstallAction.Update:
                    installer.DownloadAndInstallSigned(apk, apkOnlyUpdate: false);
                    break;
                case NevrInstallAction.AlreadyCurrent:
                case NevrInstallAction.NewerInstalled:
                    installer.SkipApkInstallBecauseCurrent();
                    break;
                default:
                    installer.DownloadAndInstallSigned(apk, apkOnlyUpdate: false, replaceOtherSigner: true);
                    break;
            }
        });
    }

    private NevrReleaseManager GetReleaseManager()
    {
        if (releaseManager == null)
            releaseManager = FindFirstObjectByType<NevrReleaseManager>();

        if (releaseManager == null)
            releaseManager = gameObject.AddComponent<NevrReleaseManager>();

        return releaseManager;
    }

    private void ShowError(string message)
    {
        if (errorText != null)
        {
            errorText.text = message;
            errorText.gameObject.SetActive(true);
        }
    }
}
