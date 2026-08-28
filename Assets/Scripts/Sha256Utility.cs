using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;

public static class Sha256Utility
{
    private const int BufferSize = 128 * 1024;
    private const long BytesPerFrame = 4L * 1024L * 1024L;

    public static IEnumerator CalculateFile(
        string path,
        Action<string> onComplete,
        Action<string> onError = null)
    {
        FileStream stream = null;
        SHA256 sha = null;

        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
            sha = SHA256.Create();
        }
        catch (Exception exception)
        {
            stream?.Dispose();
            sha?.Dispose();
            onError?.Invoke(exception.Message);
            yield break;
        }

        byte[] buffer = new byte[BufferSize];
        long bytesThisFrame = 0;
        string failure = null;

        while (true)
        {
            int bytesRead = 0;
            try
            {
                bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead > 0)
                    sha.TransformBlock(buffer, 0, bytesRead, buffer, 0);
            }
            catch (Exception exception)
            {
                failure = exception.Message;
            }

            if (failure != null || bytesRead == 0)
                break;

            bytesThisFrame += bytesRead;
            if (bytesThisFrame >= BytesPerFrame)
            {
                bytesThisFrame = 0;
                yield return null;
            }
        }

        if (failure == null)
        {
            try
            {
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            }
            catch (Exception exception)
            {
                failure = exception.Message;
            }
        }

        string result = null;
        if (failure == null)
            result = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();

        stream.Dispose();
        sha.Dispose();

        if (failure != null)
            onError?.Invoke(failure);
        else
            onComplete?.Invoke(result);
    }
}
