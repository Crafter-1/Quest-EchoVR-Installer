using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

// Download.cs cannot run here (it is a MonoBehaviour), so these read its source for the
// ordering the game-data download relies on.
public class NevrGameDataWiringTests
{
    private static string DownloadSource()
    {
        string directory = AppContext.BaseDirectory;
        while (directory != null)
        {
            string candidate = Path.Combine(directory, "Assets", "Scripts", "Download.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = Path.GetDirectoryName(directory);
        }

        throw new FileNotFoundException("Assets/Scripts/Download.cs was not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void TheDownloadNoLongerBuildsAnAddressFromAServerListAndAFileName()
    {
        string source = DownloadSource();
        Assert.DoesNotContain("ServerLocations", source);
        Assert.DoesNotContain("string BuildUrl(", source);
        Assert.DoesNotContain("DownloadFileName}", source);
        Assert.DoesNotContain("UnityWebRequest", source);
        Assert.Contains("NevrGameData.TryBuildUrl(", source);
    }

    [Fact]
    public void TheDownloadIsCappedAtTheManifestSizeAndResumesFromThePartialFile()
    {
        string source = DownloadSource();
        Assert.Matches(new Regex(@"NevrHttpDownload\.Get\(\s*url,\s*target,\s*artifact\.Size,\s*true,"), source);
        Assert.Contains("NevrGameData.ResumeOffset(", source);
        Assert.Contains("resumeFrom);", source);
    }

    [Fact]
    public void TheZipGetsItsFinalNameOnlyAfterTheSizeAndHashAreChecked()
    {
        string source = DownloadSource();
        int check = source.IndexOf("NevrGameData.CheckDownloaded(", StringComparison.Ordinal);
        int refuse = source.IndexOf("\"DATA_VERIFY_FAILED\", problem", StringComparison.Ordinal);
        int store = source.IndexOf("TryReplaceDownloadedFile(temporaryPath", StringComparison.Ordinal);
        Assert.True(check > 0, "Download.cs never checks the download against the manifest");
        Assert.True(refuse > check, "a failed check is not refused right after it");
        Assert.True(store > refuse, "the zip is renamed before the check is made");

        // A refused download is deleted and the method ends before the rename.
        string between = source.Substring(check, store - check);
        Assert.Contains("TryDelete(temporaryPath);", between);
        Assert.Contains("yield break;", between);
    }

    [Fact]
    public void OnlyTheZipTheManifestNamesIsExtracted()
    {
        string source = DownloadSource();
        Assert.Contains("Path.GetFileName(_downloadedZipPath) != _gameData.Name", source);
        Assert.Contains("TryGetLength(_downloadedZipPath) != _gameData.Size", source);
    }
}
