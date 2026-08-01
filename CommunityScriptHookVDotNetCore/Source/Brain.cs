using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: AssemblyMetadata("CCHL.Role", "ManagedBrain")]
[assembly: AssemblyMetadata(
    "CCHL.ContractId",
    "7C8E18B7-2D11-4D1E-9C53-5E3A0A4A63A1")]
[assembly: AssemblyMetadata("CCHL.AbiMajor", "1")]
[assembly: AssemblyMetadata("CCHL.AbiMinor", "0")]
[assembly: AssemblyMetadata(
    "CCHL.EntryType",
    "CommunityScriptHookVDotNetCore.Source.Brain, " +
    "CommunityScriptHookVDotNetCore")]
[assembly: AssemblyMetadata("CCHL.EntryMethod", "Run")]
[assembly: AssemblyMetadata("CCHL.RuntimeTfm", "net10.0")]
[assembly: AssemblyMetadata(
    "CCHL.RuntimeFramework",
    "Microsoft.NETCore.App")]
[assembly: AssemblyMetadata("CCHL.RuntimeVersion", "10.0.0")]

namespace CommunityScriptHookVDotNetCore.Source;

public static class Brain
{
    [UnmanagedCallersOnly(
        CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int Run(nint request, int requestSize)
    {
        try
        {
            return (int)Runtime.Run(request, requestSize);
        }
        catch
        {
            return (int)BrainRunResult.InternalFailure;
        }
    }
}