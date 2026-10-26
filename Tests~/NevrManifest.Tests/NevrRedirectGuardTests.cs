using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

public class NevrRedirectGuardTests
{
    [Fact]
    public void RedirectLimitIsZero()
    {
        Assert.Equal(0, NevrHttpRules.RedirectLimit);
    }

    // The Unity request classes cannot run here, so this reads the source: every place in
    // Assets/Scripts that creates a UnityWebRequest must also set its redirect limit from
    // NevrHttpRules, or Unity follows up to 32 redirects (to http, or to another host).
    [Fact]
    public void EveryUnityWebRequestRefusesRedirects()
    {
        var requestPattern = new Regex(@"UnityWebRequest\s*\.\s*(Get|Head|Post|Put|Delete)\s*\(|new\s+UnityWebRequest\s*\(");
        var limitPattern = new Regex(@"\.redirectLimit\s*=\s*NevrHttpRules\.RedirectLimit\s*;");

        var unguarded = new List<string>();
        foreach (string file in Directory.GetFiles(ScriptsDirectory(), "*.cs", SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(file);
            int requests = requestPattern.Matches(source).Count;
            int limits = limitPattern.Matches(source).Count;
            if (requests != limits)
                unguarded.Add($"{Path.GetFileName(file)}: {requests} request(s), {limits} redirect limit(s)");
        }

        Assert.True(unguarded.Count == 0, string.Join("; ", unguarded));
    }

    private static string ScriptsDirectory()
    {
        string directory = AppContext.BaseDirectory;
        while (directory != null)
        {
            string candidate = Path.Combine(directory, "Assets", "Scripts");
            if (Directory.Exists(candidate))
                return candidate;

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException("Assets/Scripts was not found above " + AppContext.BaseDirectory);
    }
}
