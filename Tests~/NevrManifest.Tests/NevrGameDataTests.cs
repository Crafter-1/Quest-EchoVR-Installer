using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Xunit;

public class NevrGameDataTests
{
    private const string LivePath = "data/echovr-data-fc2eedeacc50.zip";
    private const string LiveSha = "fc2eedeacc50d9ddf751e21914bb4188660cf5e79ce48aab24b84e757f4b543c";
    private const long LiveSize = 937395400;

    // The game-data artifact of the live manifest (version 3), added to the example manifest.
    private static JObject GameDataNode() => new JObject
    {
        ["name"] = "echovr-data-fc2eedeacc50.zip",
        ["kind"] = "game-data",
        ["platform"] = "android-arm64",
        ["path"] = LivePath,
        ["sha256"] = LiveSha,
        ["size"] = LiveSize,
        ["game_data"] = new JObject { ["data_version"] = "5932408047", ["echo_build"] = "4987566" }
    };

    private static byte[] WithGameData(Action<JObject> edit = null) => Fixtures.Edit(r =>
    {
        JObject node = GameDataNode();
        edit?.Invoke(node);
        ((JArray)r["releases"]["5.5.1"]["artifacts"]).Add(node);
    });

    private static NevrManifest ParseOk(byte[] bytes)
    {
        Assert.True(NevrManifest.TryParse(bytes, out NevrManifest m, out string error), error);
        return m;
    }

    private static NevrArtifact Pick(NevrManifest m)
    {
        Assert.True(NevrGameData.TryPick(m, "beta", out NevrArtifact artifact, out string error), error);
        return artifact;
    }

    // ---- parsing ----

    [Fact]
    public void GameDataArtifactParses()
    {
        NevrArtifact a = Pick(ParseOk(WithGameData()));
        Assert.Equal("game-data", a.Kind);
        Assert.Equal("android-arm64", a.Platform);
        Assert.Equal("echovr-data-fc2eedeacc50.zip", a.Name);
        Assert.Equal(LivePath, a.Path);
        Assert.Equal(LiveSha, a.Sha256);
        Assert.Equal(LiveSize, a.Size);
        Assert.Equal("5932408047", a.DataVersion);
        Assert.Equal("4987566", a.EchoBuild);
    }

    [Fact]
    public void TheApkIsStillPickedNextToGameData()
    {
        NevrManifest m = ParseOk(WithGameData());
        Assert.True(NevrQuestRelease.TryPick(m, "beta", out _, out NevrArtifact apk, out string error), error);
        Assert.Equal("quest-apk", apk.Kind);
        Assert.Equal(2, m.Releases["5.5.1"].Artifacts.Count);
    }

    [Fact]
    public void TheLiveManifestVersion3ParsesAndNamesTheGameData()
    {
        byte[] bytes = File.ReadAllBytes(Fixtures.Path("live-manifest-v3.json"));
        NevrManifest m = ParseOk(bytes);
        Assert.Equal(3, m.ManifestVersion);

        NevrArtifact a = Pick(m);
        Assert.Equal(LivePath, a.Path);
        Assert.Equal(LiveSha, a.Sha256);
        Assert.Equal(LiveSize, a.Size);
        Assert.Equal("5932408047", a.DataVersion);
        Assert.Equal("4987566", a.EchoBuild);

        Assert.True(NevrGameData.TryBuildUrl(a, out string url, out string error), error);
        Assert.Equal("https://evr.echo.taxi/nevr-runtime/data/echovr-data-fc2eedeacc50.zip", url);

        // The same release still has exactly one Quest APK for the install.
        Assert.True(NevrQuestRelease.TryPick(m, "beta", out _, out NevrArtifact apk, out error), error);
        Assert.Equal("v5.10.0/nevr-runtime-v5.10.0-quest.apk", apk.Path);
    }

    [Fact]
    public void TheLiveManifestSignatureVerifiesAgainstThePinnedKeys()
    {
        byte[] bytes = File.ReadAllBytes(Fixtures.Path("live-manifest-v3.json"));
        string sig = File.ReadAllText(Fixtures.Path("live-manifest-v3.json.sig"));
        Assert.Equal(NevrSignatureStatus.Valid,
            MinisignVerifier.Verify(bytes, sig, NevrPinnedKeys.All, null, out _));
    }

    public static TheoryData<string> BadEntries() => new TheoryData<string>
    {
        "pathDotDot", "pathDotDotMiddle", "pathAbsoluteUrl", "pathLeadingSlash", "pathSchemeRelative",
        "pathOtherFolder", "pathBackslash", "pathTrailingNewline", "pathQuery", "pathUpper",
        "pathNested", "nameMismatch", "nameWithSlash", "platformWindows", "shaUpper", "shaShort",
        "shaTrailingNewline", "size0", "sizeNegative", "sizeTooBig", "sizeString", "sizeFloat",
        "noGameDataMember", "dataVersionMissing", "dataVersionLetters", "dataVersionNumber",
        "echoBuildMissing", "echoBuildEmpty", "echoBuildTrailingNewline", "noPath", "noSha", "noSize"
    };

    [Theory]
    [MemberData(nameof(BadEntries))]
    public void InvalidGameDataEntriesRefuseTheManifest(string what)
    {
        byte[] bytes = WithGameData(n =>
        {
            switch (what)
            {
                case "pathDotDot": n["path"] = "data/../echovr-data-fc2eedeacc50.zip"; break;
                case "pathDotDotMiddle": n["path"] = "data/../../evil/echovr-data-fc2eedeacc50.zip"; break;
                case "pathAbsoluteUrl": n["path"] = "https://evil.example/data/echovr-data-fc2eedeacc50.zip"; break;
                case "pathLeadingSlash": n["path"] = "/data/echovr-data-fc2eedeacc50.zip"; break;
                case "pathSchemeRelative": n["path"] = "//evil.example/data/echovr-data-fc2eedeacc50.zip"; break;
                case "pathOtherFolder": n["path"] = "v5.5.1/echovr-data-fc2eedeacc50.zip"; break;
                case "pathBackslash": n["path"] = "data\\echovr-data-fc2eedeacc50.zip"; break;
                case "pathTrailingNewline": n["path"] = LivePath + "\n"; break;
                case "pathQuery": n["path"] = LivePath + "?x=1"; break;
                case "pathUpper": n["path"] = "data/echovr-data-FC2EEDEACC50.zip"; break;
                case "pathNested": n["path"] = "data/more/echovr-data-fc2eedeacc50.zip"; break;
                case "nameMismatch": n["name"] = "echovr-data-000000000000.zip"; break;
                case "nameWithSlash": n["name"] = "data/echovr-data-fc2eedeacc50.zip"; break;
                case "platformWindows": n["platform"] = "windows-x86_64"; break;
                case "shaUpper": n["sha256"] = LiveSha.ToUpperInvariant(); break;
                case "shaShort": n["sha256"] = LiveSha.Substring(1); break;
                case "shaTrailingNewline": n["sha256"] = LiveSha + "\n"; break;
                case "size0": n["size"] = 0; break;
                case "sizeNegative": n["size"] = -5; break;
                case "sizeTooBig": n["size"] = NevrManifest.MaxGameDataBytes + 1; break;
                case "sizeString": n["size"] = "937395400"; break;
                case "sizeFloat": n["size"] = 5.5; break;
                case "noGameDataMember": n.Remove("game_data"); break;
                case "dataVersionMissing": ((JObject)n["game_data"]).Remove("data_version"); break;
                case "dataVersionLetters": n["game_data"]["data_version"] = "59x2408047"; break;
                case "dataVersionNumber": n["game_data"]["data_version"] = 5932408047; break;
                case "echoBuildMissing": ((JObject)n["game_data"]).Remove("echo_build"); break;
                case "echoBuildEmpty": n["game_data"]["echo_build"] = ""; break;
                case "echoBuildTrailingNewline": n["game_data"]["echo_build"] = "4987566\n"; break;
                case "noPath": n.Remove("path"); break;
                case "noSha": n.Remove("sha256"); break;
                case "noSize": n.Remove("size"); break;
            }
        });

        Assert.False(NevrManifest.TryParse(bytes, out NevrManifest m, out string error), what + " was accepted");
        Assert.Null(m);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void ContentsAndOtherMembersOfTheEntryAreIgnored()
    {
        byte[] bytes = WithGameData(n =>
        {
            n["contents"] = new JArray(new JObject { ["path"] = "../../x", ["sha256"] = "zz", ["size"] = -1 });
            n["future"] = 1;
        });
        Assert.Equal(LivePath, Pick(ParseOk(bytes)).Path);
    }

    // ---- picking ----

    [Fact]
    public void AReleaseWithoutGameDataSaysSo()
    {
        NevrManifest m = ParseOk(Fixtures.ExampleManifest());
        Assert.False(NevrGameData.TryPick(m, "beta", out NevrArtifact a, out string error));
        Assert.Null(a);
        Assert.Contains("no game data", error);
    }

    [Fact]
    public void TwoGameDataFilesAreRefusedAsAmbiguous()
    {
        byte[] bytes = Fixtures.Edit(r =>
        {
            var artifacts = (JArray)r["releases"]["5.5.1"]["artifacts"];
            artifacts.Add(GameDataNode());
            JObject second = GameDataNode();
            second["name"] = "echovr-data-aaaaaaaaaaaa.zip";
            second["path"] = "data/echovr-data-aaaaaaaaaaaa.zip";
            artifacts.Add(second);
        });
        NevrManifest m = ParseOk(bytes);
        Assert.False(NevrGameData.TryPick(m, "beta", out NevrArtifact a, out string error));
        Assert.Null(a);
        Assert.Contains("more than one", error);
    }

    [Fact]
    public void AMissingChannelIsRefused()
    {
        NevrManifest m = ParseOk(WithGameData());
        Assert.False(NevrGameData.TryPick(m, "stable", out NevrArtifact a, out _));
        Assert.Null(a);
    }

    // ---- the address ----

    [Fact]
    public void ThePathIsJoinedToTheBaseUrl()
    {
        NevrArtifact a = Pick(ParseOk(WithGameData()));
        Assert.True(NevrGameData.TryBuildUrl(a, out string url, out string error), error);
        Assert.Equal(NevrManifest.BaseUrl + LivePath, url);
        Assert.Equal("https://evr.echo.taxi/nevr-runtime/data/echovr-data-fc2eedeacc50.zip", url);
        Assert.DoesNotContain("..", url);
        Assert.StartsWith("https://evr.echo.taxi/nevr-runtime/", url);
    }

    [Theory]
    [InlineData("../echovr-data-fc2eedeacc50.zip")]
    [InlineData("data/../echovr-data-fc2eedeacc50.zip")]
    [InlineData("data/%2e%2e/echovr-data-fc2eedeacc50.zip")]
    [InlineData("https://evil.example/data/echovr-data-fc2eedeacc50.zip")]
    [InlineData("http://evr.echo.taxi/nevr-runtime/data/echovr-data-fc2eedeacc50.zip")]
    [InlineData("//evil.example/data/echovr-data-fc2eedeacc50.zip")]
    [InlineData("/data/echovr-data-fc2eedeacc50.zip")]
    [InlineData("data/echovr-data-fc2eedeacc50.zip?a=b")]
    [InlineData("data/echovr-data-fc2eedeacc50.zip#frag")]
    [InlineData("data/echovr-data-fc2eedeacc50.zip\n")]
    [InlineData("")]
    [InlineData(null)]
    public void AnArtifactWithAnyOtherPathHasNoAddress(string path)
    {
        // Built by hand, as if something other than the parser had made it: the address is
        // checked again here and not only when the manifest is parsed.
        var artifact = new NevrArtifact { Kind = "game-data", Path = path, Name = "x", Sha256 = LiveSha, Size = 1 };
        Assert.False(NevrGameData.TryBuildUrl(artifact, out string url, out string error));
        Assert.Null(url);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void OnlyAGameDataArtifactHasAGameDataAddress()
    {
        var apk = new NevrArtifact { Kind = "quest-apk", Path = LivePath };
        Assert.False(NevrGameData.TryBuildUrl(apk, out _, out _));
        Assert.False(NevrGameData.TryBuildUrl(null, out _, out _));
    }

    // ---- size and hash ----

    private static byte[] FakeData(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)((i * 31 + 7) % 251);
        return data;
    }

    private static string Sha(byte[] data) => NevrHex.Encode(SHA256.Create().ComputeHash(data));

    private static NevrArtifact ArtifactFor(byte[] data) => new NevrArtifact
    {
        Kind = "game-data", Name = "echovr-data-000000000000.zip", Path = "data/echovr-data-000000000000.zip",
        Platform = "android-arm64", Size = data.Length, Sha256 = Sha(data), DataVersion = "1", EchoBuild = "2"
    };

    [Fact]
    public void ARightDownloadIsAccepted()
    {
        byte[] data = FakeData(100_000);
        Assert.Null(NevrGameData.CheckDownloaded(ArtifactFor(data), data.Length, Sha(data)));
    }

    [Fact]
    public void ACapitalisedHashIsTheSameHash()
    {
        byte[] data = FakeData(1000);
        Assert.Null(NevrGameData.CheckDownloaded(ArtifactFor(data), data.Length, Sha(data).ToUpperInvariant()));
    }

    [Fact]
    public void AHashMismatchIsRefused()
    {
        byte[] data = FakeData(100_000);
        NevrArtifact artifact = ArtifactFor(data);
        byte[] flipped = (byte[])data.Clone();
        flipped[54321] ^= 1;

        string problem = NevrGameData.CheckDownloaded(artifact, flipped.Length, Sha(flipped));
        Assert.NotNull(problem);
        Assert.Contains("checksum", problem);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void ASizeMismatchIsRefusedEvenWithTheRightHash(int delta)
    {
        byte[] data = FakeData(100_000);
        string problem = NevrGameData.CheckDownloaded(ArtifactFor(data), data.Length + delta, Sha(data));
        Assert.NotNull(problem);
        Assert.Contains("bytes", problem);
    }

    [Fact]
    public void TheSinkStopsAtTheManifestSizeAndNeedsAllOfIt()
    {
        byte[] data = FakeData(100_000);

        // Short: the download ends before the size the manifest gave.
        using (var sink = new NevrDownloadSink(new MemoryStream(), data.Length, true))
        {
            Assert.True(sink.Write(data, data.Length - 1));
            Assert.False(sink.Complete(out string sha));
            Assert.Null(sha);
            Assert.Contains("of " + data.Length, sink.Error);
        }

        // Long: one byte past the size is refused and not stored.
        var target = new MemoryStream();
        using (var sink = new NevrDownloadSink(target, data.Length, true))
        {
            Assert.True(sink.Write(data, data.Length));
            Assert.False(sink.Write(new byte[1], 1));
            Assert.Equal(data.Length, target.Length);
            Assert.False(sink.Complete(out _));
        }

        // Exactly right.
        using (var sink = new NevrDownloadSink(new MemoryStream(), data.Length, true))
        {
            Assert.True(sink.Write(data, data.Length));
            Assert.True(sink.Complete(out string sha));
            Assert.Equal(Sha(data), sha);
        }
    }

    [Fact]
    public void ASmallFakeZipDownloadedInPiecesVerifiesAndAFlippedByteDoesNot()
    {
        byte[] data = FakeData(300_000);
        NevrArtifact artifact = ArtifactFor(data);

        string Run(byte[] wire)
        {
            using (var sink = new NevrDownloadSink(new MemoryStream(), artifact.Size, true))
            {
                for (int i = 0; i < wire.Length; i += 64 * 1024)
                {
                    var piece = new byte[Math.Min(64 * 1024, wire.Length - i)];
                    Buffer.BlockCopy(wire, i, piece, 0, piece.Length);
                    Assert.True(sink.Write(piece, piece.Length));
                }

                Assert.True(sink.Complete(out string sha));
                return NevrGameData.CheckDownloaded(artifact, sink.Received, sha);
            }
        }

        Assert.Null(Run(data));

        byte[] flipped = (byte[])data.Clone();
        flipped[123456] ^= 0x80;
        Assert.Contains("checksum", Run(flipped));
    }

    // ---- resume ----

    [Theory]
    [InlineData(-1L, 1000L, 0L)]
    [InlineData(0L, 1000L, 0L)]
    [InlineData(1L, 1000L, 1L)]
    [InlineData(500L, 1000L, 500L)]
    [InlineData(999L, 1000L, 999L)]
    [InlineData(1000L, 1000L, 1000L)]
    [InlineData(1001L, 1000L, 0L)]
    public void ResumeOffsetKeepsOnlyAPossiblePrefix(long partial, long size, long expected)
    {
        Assert.Equal(expected, NevrGameData.ResumeOffset(partial, size));
    }

    [Fact]
    public void TheRangeHeaderAsksForTheRest()
    {
        Assert.Equal("bytes=500-", NevrGameData.RangeHeader(500));
        Assert.Equal("bytes=937395399-", NevrGameData.RangeHeader(937395399));
    }

    [Fact]
    public void OnlyAnExactPartialAnswerIsAccepted()
    {
        Assert.Null(NevrGameData.CheckRangeResponse(206, "bytes 500-999/1000", 500, 1000));
        Assert.Null(NevrGameData.CheckRangeResponse(206, "bytes 937395399-937395399/937395400", 937395399, 937395400));

        Assert.NotNull(NevrGameData.CheckRangeResponse(200, null, 500, 1000));
        Assert.NotNull(NevrGameData.CheckRangeResponse(416, null, 500, 1000));
        Assert.NotNull(NevrGameData.CheckRangeResponse(206, null, 500, 1000));
        Assert.NotNull(NevrGameData.CheckRangeResponse(206, "bytes 0-999/1000", 500, 1000));
        Assert.NotNull(NevrGameData.CheckRangeResponse(206, "bytes 500-998/1000", 500, 1000));
        Assert.NotNull(NevrGameData.CheckRangeResponse(206, "bytes 500-999/*", 500, 1000));
        Assert.NotNull(NevrGameData.CheckRangeResponse(206, "bytes 500-999/2000", 500, 1000));
    }

    [Fact]
    public void AServerThatAnswers200ToARangeIsRecognisedAsIgnoringIt()
    {
        Assert.True(NevrGameData.ServerIgnoredRange(200));
        Assert.False(NevrGameData.ServerIgnoredRange(206));
        Assert.False(NevrGameData.ServerIgnoredRange(0));
    }

    [Fact]
    public void AResumedDownloadIsCountedAndCheckedAsTheWholeFile()
    {
        byte[] data = FakeData(200_000);
        NevrArtifact artifact = ArtifactFor(data);
        int half = 70_000;

        string path = Path.Combine(Path.GetTempPath(), "nevr-gamedata-resume-test-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            // The first run stopped after `half` bytes.
            File.WriteAllBytes(path, data.Take(half).ToArray());
            Assert.Equal(half, NevrGameData.ResumeOffset(new FileInfo(path).Length, artifact.Size));

            // The second run carries on from there.
            var tail = data.Skip(half).ToArray();
            string tailSha;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.Seek(half, SeekOrigin.Begin);
                using (var sink = new NevrDownloadSink(stream, artifact.Size, true, half))
                {
                    Assert.Equal(half, sink.Received);
                    Assert.True(sink.Write(tail, tail.Length));
                    Assert.True(sink.Complete(out tailSha));
                    Assert.Equal(artifact.Size, sink.Received);
                }
            }

            // The sink hashed only what it received, so the whole file is hashed from disk.
            Assert.Equal(Sha(tail), tailSha);
            Assert.NotEqual(artifact.Sha256, tailSha);
            byte[] onDisk = File.ReadAllBytes(path);
            Assert.Null(NevrGameData.CheckDownloaded(artifact, onDisk.Length, Sha(onDisk)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AResumedDownloadCannotPassTheManifestSize()
    {
        byte[] data = FakeData(1000);
        using (var sink = new NevrDownloadSink(new MemoryStream(), data.Length, true, 600))
        {
            Assert.True(sink.Write(data, 400));
            Assert.False(sink.Write(data, 1));
        }
    }

    [Fact]
    public void AResumedDownloadWithABadStartIsRefusedByTheHash()
    {
        byte[] data = FakeData(200_000);
        NevrArtifact artifact = ArtifactFor(data);

        // The kept bytes were an error page, not the start of the file.
        byte[] kept = data.Take(70_000).ToArray();
        kept[10] ^= 1;
        byte[] whole = kept.Concat(data.Skip(70_000)).ToArray();

        string problem = NevrGameData.CheckDownloaded(artifact, whole.Length, Sha(whole));
        Assert.NotNull(problem);
        Assert.Contains("checksum", problem);
    }
}
