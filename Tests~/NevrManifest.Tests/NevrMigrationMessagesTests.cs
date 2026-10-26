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
