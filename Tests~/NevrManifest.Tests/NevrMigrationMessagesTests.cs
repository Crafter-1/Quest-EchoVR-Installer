using Xunit;

public class NevrMigrationMessagesTests
{
    [Fact]
    public void NoticeSaysWhatWillHappenAndPromisesNothingAboutData()
    {
        string text = NevrMigrationMessages.BeforeUninstall(12);
        Assert.Contains("uninstalled first", text);
        Assert.Contains("Cancel", text);
        Assert.Contains("not verified", text);
        Assert.Contains("12", text);
        Assert.DoesNotContain("will be kept", text);
        Assert.DoesNotContain("will not be lost", text);
        Assert.DoesNotContain("safe", text);
    }
}

public class NevrMigrationDataTests
{
    [Fact]
    public void NoticeOnlyPromisesDataThatIsAlreadyDownloaded()
    {
        string text = NevrMigrationMessages.BeforeUninstall(5);
        Assert.Contains("already been downloaded", text);
        Assert.DoesNotContain("as part of the install", text);
        Assert.Contains("not verified", text);
    }

    [Fact]
    public void MigratingNeverSkipsTheDataDownload()
    {
        Assert.True(NevrMigrationRules.IsValid(replaceOtherSigner: true, apkOnlyUpdate: false));
        Assert.False(NevrMigrationRules.IsValid(replaceOtherSigner: true, apkOnlyUpdate: true));
        Assert.True(NevrMigrationRules.IsValid(replaceOtherSigner: false, apkOnlyUpdate: true));
        Assert.True(NevrMigrationRules.IsValid(replaceOtherSigner: false, apkOnlyUpdate: false));
    }

    [Fact]
    public void FailureAfterRemovalSaysWhatHappenedAndHowToRetry()
    {
        string text = NevrMigrationMessages.RemovedButNotInstalled("boom");
        Assert.Contains("removed", text);
        Assert.Contains("boom", text);
        Assert.Contains("open it again", text);
    }
}

public class NevrMigrationRetryMessageTests
{
    [Fact]
    public void RetryMessageSaysOnlyAMatchingApkIsCheckedAgain()
    {
        string text = NevrMigrationMessages.RemovedButNotInstalled("boom");
        Assert.Contains("APK", text);
        Assert.Contains("checked again", text);
        Assert.Contains("game data", text);
        Assert.Contains("as it is", text);
        Assert.DoesNotContain("What was downloaded is kept", text);
    }
}

public class NevrMigrationEntryPointTests
{
    // The scene classes cannot run in the test project, so this reads the source: a migration
    // (replaceOtherSigner: true) may only be started by AfterInstallController.BeginMigration,
    // which downloads the game data fresh and sets _migrating first. A second caller that
    // goes straight to the APK installer skips both.
    [Fact]
    public void OnlyBeginMigrationStartsAMigration()
    {
        var offenders = new System.Collections.Generic.List<string>();
        foreach (string file in System.IO.Directory.GetFiles(ScriptsDirectory(), "*.cs", System.IO.SearchOption.AllDirectories))
        {
            string name = System.IO.Path.GetFileName(file);
            if (name == "AfterInstallController.cs" || name == "ApkInstaller.cs")
                continue;

            string source = System.IO.File.ReadAllText(file);
            if (System.Text.RegularExpressions.Regex.IsMatch(source, @"replaceOtherSigner\s*:\s*true"))
                offenders.Add(name);
        }

        Assert.True(offenders.Count == 0, "starts a migration without BeginMigration: " + string.Join(", ", offenders));
    }

    private static string ScriptsDirectory()
    {
        string directory = System.AppContext.BaseDirectory;
        while (directory != null)
        {
            string candidate = System.IO.Path.Combine(directory, "Assets", "Scripts");
            if (System.IO.Directory.Exists(candidate))
                return candidate;

            directory = System.IO.Path.GetDirectoryName(directory);
        }

        throw new System.IO.DirectoryNotFoundException("Assets/Scripts was not found");
    }
}

// Source checks, not runtime tests: the scene classes cannot run in the test project. They
// pin the wiring that the scene needs, because the scene has two ApkInstaller components
// (the one the menu and Download use, and a second on the download screen with no status
// text), and FindFirstObjectByType can return either.
public class NevrMigrationWiringTests
{
    [Fact]
    public void MigrationFromTheMenuIsGivenTheMenusInstaller()
    {
        string after = Source("AfterInstallController.cs");
        Assert.Matches(@"BeginMigrationFromInstallMenu\(\s*NevrArtifact\s+apk,\s*ApkInstaller\s+installer\s*\)", after);

        string menu = Source("InstallMenuController.cs");
        Assert.Matches(@"BeginMigrationFromInstallMenu\(\s*apk,\s*installer\s*\)", menu);
    }

    [Fact]
    public void GetApkInstallerPrefersTheOneTheDownloaderUses()
    {
        string body = Method(Source("AfterInstallController.cs"), "private ApkInstaller GetApkInstaller()");
        int preferred = body.IndexOf("dataDownloader.apkInstaller", System.StringComparison.Ordinal);
        int search = body.IndexOf("FindFirstObjectByType", System.StringComparison.Ordinal);
        Assert.True(preferred >= 0, "GetApkInstaller does not look at dataDownloader.apkInstaller");
        Assert.True(search < 0 || preferred < search, "GetApkInstaller searches the scene before using dataDownloader.apkInstaller");
    }

    [Fact]
    public void AFailedMigrationStartClosesTheGateAndLeavesNothingMigrating()
    {
        string body = Method(Source("AfterInstallController.cs"), "private void BeginMigration(");
        Assert.Contains("catch", body);
        Assert.Contains("NevrInstallGate.Close()", body);
        Assert.Contains("_migrating = false", body);
    }

    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, System.StringComparison.Ordinal);
        Assert.True(start >= 0, "not found: " + signature);
        int end = source.IndexOf("\n    }\n", start, System.StringComparison.Ordinal);
        Assert.True(end > start, "end of method not found: " + signature);
        return source.Substring(start, end - start);
    }

    private static string Source(string fileName)
    {
        string directory = System.AppContext.BaseDirectory;
        while (directory != null)
        {
            string candidate = System.IO.Path.Combine(directory, "Assets", "Scripts", fileName);
            if (System.IO.File.Exists(candidate))
                return System.IO.File.ReadAllText(candidate);

            directory = System.IO.Path.GetDirectoryName(directory);
        }

        throw new System.IO.FileNotFoundException(fileName);
    }
}
