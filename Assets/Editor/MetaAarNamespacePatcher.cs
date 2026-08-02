using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// Scans every Meta ("com.meta.xr.*") package in Library/PackageCache for .aar
/// files, finds any that share the same namespace ("com.oculus.Integration",
/// the default Meta ships), and rewrites each conflicting one to a unique
/// namespace derived from its own filename. This avoids needing to hardcode
/// each individual AAR as new Meta modules (Movement SDK, Telemetry, etc.)
/// run into the same conflict.
/// Temporary fix while Meta updates their SDK for Gradle 9+ compatibility.

public class MetaAarNamespacePatcher : IPreprocessBuildWithReport
{
    public int callbackOrder => -100;

    private const string Tag = "[MetaAarNamespacePatcher]";
    private const string ConflictingNamespace = "com.oculus.Integration";
    private const string MetaPackagePrefix = "com.meta.xr";

    public void OnPreprocessBuild(BuildReport report)
    {
        if (report.summary.platform != BuildTarget.Android)
            return;

        ApplyAll();
    }

    [MenuItem("Build/Patch Meta AAR Namespaces")]
    public static void PatchManually()
    {
        ApplyAll();
        UnityEngine.Debug.Log($"{Tag} Done.");
    }

    private static void ApplyAll()
    {
        string sevenZa = GetSevenZipPath();
        if (!File.Exists(sevenZa))
        {
            UnityEngine.Debug.LogError($"{Tag} 7za not found at: {sevenZa}");
            return;
        }

        string packageCache = Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", "Library", "PackageCache"));

        if (!Directory.Exists(packageCache))
        {
            UnityEngine.Debug.LogWarning($"{Tag} PackageCache not found at: {packageCache}");
            return;
        }

        // Find every Meta package folder (e.g. com.meta.xr.sdk.core@85.0.0)
        var metaDirs = Directory.GetDirectories(packageCache, MetaPackagePrefix + "*", SearchOption.TopDirectoryOnly);

        var patchedCount = 0;

        foreach (var metaDir in metaDirs)
        {
            // Find every .aar inside this package, at any depth
            var aarFiles = Directory.GetFiles(metaDir, "*.aar", SearchOption.AllDirectories);

            foreach (var aarPath in aarFiles)
            {
                if (TryPatchAar(sevenZa, aarPath))
                    patchedCount++;
            }
        }

        UnityEngine.Debug.Log($"{Tag} Scan complete. Patched {patchedCount} AAR(s).");
    }

    // sevenZipPath was added in Unity 6.3; fall back to the bundled path on older versions.
    private static string GetSevenZipPath()
    {
        var prop = typeof(EditorApplication).GetProperty("sevenZipPath", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        if (prop != null)
            return (string)prop.GetValue(null);

        string exe = Application.platform == RuntimePlatform.WindowsEditor ? "7z.exe" : "7za";
        return Path.Combine(EditorApplication.applicationContentsPath, "Tools", exe);
    }

    private static bool TryPatchAar(string sevenZa, string aarPath)
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), "MetaAarPatch_" + Path.GetFileNameWithoutExtension(aarPath) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));

        try
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
            Directory.CreateDirectory(tmpDir);

            Run7za(sevenZa, $"x \"{aarPath}\" -o\"{tmpDir}\" -y");

            string manifestPath = Path.Combine(tmpDir, "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
                return false; // not every .aar has a manifest, that's fine

            string xml = File.ReadAllText(manifestPath);
            var match = Regex.Match(xml, @"(?<=\bpackage="")[^""]*");

            if (!match.Success)
                return false;

            string currentNamespace = match.Value;

            // Only patch AARs using the known-conflicting default namespace.
            if (currentNamespace != ConflictingNamespace)
                return false;

            // Build a unique namespace from the AAR's own filename, e.g.
            // "InteractionSdk.aar" -> "com.oculus.Integration.interactionsdk"
            string suffix = Path.GetFileNameWithoutExtension(aarPath).ToLowerInvariant();
            suffix = Regex.Replace(suffix, "[^a-z0-9]", "");
            string newNamespace = $"{ConflictingNamespace}.{suffix}";

            string patched = xml.Substring(0, match.Index) + newNamespace + xml.Substring(match.Index + match.Length);
            File.WriteAllText(manifestPath, patched);

            File.Delete(aarPath);
            Run7za(sevenZa, $"a \"{aarPath}\" \"{tmpDir}{Path.DirectorySeparatorChar}*\" -tzip -mx=5");

            UnityEngine.Debug.Log($"{Tag} Patched {Path.GetFileName(aarPath)} -> {newNamespace}");
            return true;
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning($"{Tag} Failed to patch {Path.GetFileName(aarPath)}: {e.Message}");
            return false;
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    private static void Run7za(string sevenZa, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = sevenZa,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi);
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
            throw new Exception($"{Tag} 7za failed (exit {proc.ExitCode}):\n{stdout}\n{stderr}");
    }
}
