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
        NevrInstallGate.Close();
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
            // The manifest is accepted: only now may the game-data download start.
            NevrInstallGate.Open();

            // Not our Echo: the signer differs, or the version code is the stock build's.
            // The player uninstalls it first; nothing is installed over it.
            if (action == NevrInstallAction.DifferentSigner ||
                (installed && NevrQuestRelease.IsStockVersionCode(details.VersionCode)))
            {
                InstallerLog.Mark("NON_NEVR_ECHO_DETECTED", "versionCode=" + details.VersionCode);
                ShowUninstallFirstPanel();
                return;
            }

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
                    // DifferentSigner is handled above; anything else unexpected is treated the same.
                    ShowUninstallFirstPanel();
                    break;
            }
        });
    }

    [Header("Uninstall first (non-nevr Echo)")]
    [Tooltip("Shown when an Echo VR that isn't ours is installed.")]
    public GameObject uninstallFirstPanel;
    public TMP_Text uninstallFirstText;

    private bool _pendingUninstallCheck;

    private void ShowUninstallFirstPanel()
    {
        if (uninstallFirstPanel == null)
        {
            InstallerLog.Error("UNINSTALL_FIRST_PANEL_MISSING");
            installer.SetStatusMessage("Another build of Echo VR is installed. Uninstall it from the Quest library, then run the installer again.");
            return;
        }

        if (downloadPanel != null)
            downloadPanel.SetActive(false);
        uninstallFirstPanel.SetActive(true);
        if (uninstallFirstText != null)
            uninstallFirstText.text = "A different build of Echo VR is installed. Uninstall it first, then the installer will continue.";
    }

    // Wire to the panel's Uninstall button. Opens Android's own uninstall dialog.
    public void OnUninstallFirstPressed()
    {
        if (!NevrAndroid.TryRequestUninstall(NevrQuestRelease.EchoPackageName, out string error))
        {
            InstallerLog.Error("UNINSTALL_FIRST_REQUEST_FAILED | " + error);
            if (uninstallFirstText != null)
                uninstallFirstText.text = "Could not open the uninstall prompt. Uninstall Echo VR from the Quest library instead.";
            return;
        }

        _pendingUninstallCheck = true;
    }

    // Wire to a Back button on the panel.
    public void OnUninstallFirstBack()
    {
        _pendingUninstallCheck = false;
        if (uninstallFirstPanel != null)
            uninstallFirstPanel.SetActive(false);
        if (mainMenuPanel != null)
            mainMenuPanel.SetActive(true);
    }

    // Android's uninstall dialog covers us; focus comes back whether the player confirmed or
    // cancelled. If Echo is gone, carry on with a normal install.
    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus || !_pendingUninstallCheck)
            return;

        _pendingUninstallCheck = false;
        if (!NevrAndroid.TryReadInstalled(
                NevrQuestRelease.EchoPackageName, out bool installed, out _, out _) || installed)
            return;

        InstallerLog.Mark("NON_NEVR_ECHO_REMOVED");
        if (uninstallFirstPanel != null)
            uninstallFirstPanel.SetActive(false);
        BeginSignedInstall();
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
