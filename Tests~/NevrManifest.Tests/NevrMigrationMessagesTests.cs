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
