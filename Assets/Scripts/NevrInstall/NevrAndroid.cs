using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEngine;

// What Android reports about an APK file or an installed package: its package name, its
// version code, and the SHA-256 of each signer certificate (DER), lowercase hex.
// Needs API 28 or later (signing certificates); the Quest runs newer.
public static class NevrAndroid
{
    public sealed class PackageDetails
    {
        public string PackageName;
        public long VersionCode;
        public List<string> SignerCertSha256 = new List<string>();
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private const int GetSigningCertificates = 0x08000000;

    public static bool TryReadApk(string apkPath, out PackageDetails details, out string error)
    {
        details = null;
        error = null;
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var packageManager = activity.Call<AndroidJavaObject>("getPackageManager"))
            using (var packageInfo = packageManager.Call<AndroidJavaObject>(
                       "getPackageArchiveInfo", apkPath, GetSigningCertificates))
            {
                if (packageInfo == null)
                {
                    error = "Android could not read the downloaded APK.";
                    return false;
                }

                return TryRead(packageInfo, out details, out error);
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    // `installed` is false, with no error, when the package is not on the headset.
    public static bool TryReadInstalled(
        string packageName,
        out bool installed,
        out PackageDetails details,
        out string error)
    {
        installed = false;
        details = null;
        error = null;
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var packageManager = activity.Call<AndroidJavaObject>("getPackageManager"))
            {
                AndroidJavaObject packageInfo;
                try
                {
                    packageInfo = packageManager.Call<AndroidJavaObject>(
                        "getPackageInfo", packageName, GetSigningCertificates);
                }
                catch (AndroidJavaException exception)
                {
                    if (exception.Message.Contains("NameNotFoundException"))
                        return true;

                    throw;
                }

                using (packageInfo)
                {
                    if (packageInfo == null)
                        return true;

                    installed = true;
                    return TryRead(packageInfo, out details, out error);
                }
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    // Opens Android's own uninstall confirmation for a package. Returns false, with the
    // reason, if the request could not be made.
    public static bool TryRequestUninstall(string packageName, out string error)
    {
        error = null;
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var uriClass = new AndroidJavaClass("android.net.Uri"))
            using (var uri = uriClass.CallStatic<AndroidJavaObject>("parse", "package:" + packageName))
            using (var intent = new AndroidJavaObject("android.content.Intent", "android.intent.action.DELETE"))
            {
                intent.Call<AndroidJavaObject>("setData", uri);
                activity.Call("startActivity", intent);
                return true;
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool TryRead(AndroidJavaObject packageInfo, out PackageDetails details, out string error)
    {
        details = null;
        error = null;
        var read = new PackageDetails
        {
            PackageName = packageInfo.Get<string>("packageName"),
            VersionCode = packageInfo.Call<long>("getLongVersionCode")
        };

        using (var signingInfo = packageInfo.Get<AndroidJavaObject>("signingInfo"))
        {
            if (signingInfo == null)
            {
                error = "Android did not report who signed the package.";
                return false;
            }

            using (var signers = signingInfo.Call<AndroidJavaObject>("getApkContentsSigners"))
            {
                IntPtr array = signers.GetRawObject();
                int count = AndroidJNI.GetArrayLength(array);
                for (int i = 0; i < count; i++)
                {
                    IntPtr signature = AndroidJNI.GetObjectArrayElement(array, i);
                    try
                    {
                        IntPtr signatureClass = AndroidJNI.GetObjectClass(signature);
                        try
                        {
                            IntPtr method = AndroidJNI.GetMethodID(signatureClass, "toByteArray", "()[B");
                            IntPtr bytes = AndroidJNI.CallObjectMethod(signature, method, new jvalue[0]);
                            try
                            {
                                read.SignerCertSha256.Add(Sha256Hex(AndroidJNI.FromByteArray(bytes)));
                            }
                            finally
                            {
                                AndroidJNI.DeleteLocalRef(bytes);
                            }
                        }
                        finally
                        {
                            AndroidJNI.DeleteLocalRef(signatureClass);
                        }
                    }
                    finally
                    {
                        AndroidJNI.DeleteLocalRef(signature);
                    }
                }
            }
        }

        details = read;
        return true;
    }
#else
    public static bool TryRequestUninstall(string packageName, out string error)
    {
        error = "Uninstalling can only be requested on the headset.";
        return false;
    }

    public static bool TryReadApk(string apkPath, out PackageDetails details, out string error)
    {
        details = null;
        error = "APK details can only be read on the headset.";
        return false;
    }

    public static bool TryReadInstalled(
        string packageName,
        out bool installed,
        out PackageDetails details,
        out string error)
    {
        installed = false;
        details = null;
        error = "Installed app details can only be read on the headset.";
        return false;
    }
#endif

    private static string Sha256Hex(byte[] bytes)
    {
        using (SHA256 sha = SHA256.Create())
            return NevrHex.Encode(sha.ComputeHash(bytes));
    }
}
