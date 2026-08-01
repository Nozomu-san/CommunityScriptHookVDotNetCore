namespace Script4Reload.Source

open System.Reflection

[<assembly: AssemblyMetadata("SHVDN4.Role", "RuntimeExtension")>]
[<assembly: AssemblyMetadata("SHVDN4.Id", "Script4Reload")>]
[<assembly: AssemblyMetadata("SHVDN4.EntryType", "Script4Reload.Source.Script4ReloadExtension")>]
[<assembly: AssemblyMetadata("SHVDN4.ContractMajor", "1")>]
[<assembly: AssemblyMetadata("SHVDN4.ContractMinor", "0")>]
[<assembly: AssemblyMetadata("SHVDN4.Provides", "scripts4.lifecycle;scripts4.reload.policy")>]
[<assembly: AssemblyMetadata("SHVDN4.Requires", "host.frame;package.lifecycle.transition;input.actions")>]
do ()