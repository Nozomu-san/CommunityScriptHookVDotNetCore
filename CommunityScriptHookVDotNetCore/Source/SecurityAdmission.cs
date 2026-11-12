#pragma warning disable SYSLIB1054

using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace CommunityScriptHookVDotNetCore.Source;

internal sealed class SecurityBlockedException(string diagnostic) : Exception(diagnostic)
{
}

internal static class SecurityAdmission
{
    private const int ErrorVirusInfected = 225;
    private const int ErrorVirusDeleted = 226;
    private const int AmsiBlockedByAdminStart = 0x4000;
    private const int AmsiBlockedByAdminEnd = 0x4FFF;
    private const int AmsiDetected = 0x8000;

    internal static bool IsAntivirusFileBlock(Exception exception)
    {
        if (exception is not IOException &&
            exception is not UnauthorizedAccessException)
        {
            return false;
        }
        uint code = unchecked((uint)exception.HResult);
        uint error = code & 0xFFFFu;
        return (code & 0xFFFF0000u) == 0x80070000u &&
            (error == ErrorVirusInfected || error == ErrorVirusDeleted);
    }

    internal static void CheckCapturedImage(
        string sourcePath,
        byte[] image,
        RuntimeLog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(log);

        if (!OperatingSystem.IsWindows() || image.Length == 0)
        {
            return;
        }

        nint context = 0;
        int result;
        try
        {
            int init = AmsiInitialize("CommunityScriptHookVDotNetCore", out context);
            if (init < 0 || context == 0)
            {
                log.Warning(
                    $"Security scanner unavailable for '{Path.GetFileName(sourcePath)}'; " +
                    "the source remains subject to OS file protection and contract validation.");
                return;
            }
            int scan = AmsiScanBuffer(
                context,
                image,
                (uint)image.Length,
                sourcePath,
                0,
                out result);
            if (scan < 0)
            {
                log.Warning(
                    $"Security scanner did not return a verdict for " +
                    $"'{Path.GetFileName(sourcePath)}' (HRESULT 0x{scan:X8}).");
                return;
            }
        }
        catch (DllNotFoundException)
        {
            return;
        }
        catch (EntryPointNotFoundException)
        {
            return;
        }
        catch (ExternalException exception)
        {
            log.Warning(
                $"Security scanner failed for '{Path.GetFileName(sourcePath)}': " +
                exception.Message);
            return;
        }
        finally
        {
            if (context != 0)
            {
                AmsiUninitialize(context);
            }
        }

        if (result >= AmsiDetected ||
            result >= AmsiBlockedByAdminStart &&
            result <= AmsiBlockedByAdminEnd)
        {
            string digest = Convert.ToHexString(SHA256.HashData(image));
            throw new SecurityBlockedException(
                $"Security scan blocked '{Path.GetFileName(sourcePath)}' " +
                $"(SHA-256 {digest}, AMSI result {result}).");
        }
    }

    [DllImport("amsi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AmsiInitialize(
        string appName,
        out nint amsiContext);

    [DllImport("amsi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AmsiScanBuffer(
        nint amsiContext,
        [In] byte[] buffer,
        uint length,
        string contentName,
        nint session,
        out int result);

    [DllImport("amsi.dll", ExactSpelling = true)]
    private static extern void AmsiUninitialize(nint amsiContext);
}

#pragma warning restore SYSLIB1054