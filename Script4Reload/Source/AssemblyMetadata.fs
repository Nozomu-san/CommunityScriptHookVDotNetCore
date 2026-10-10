namespace Script4Reload.Source

open System.Reflection

[<assembly: AssemblyMetadata("CSHVDNC.Role", "RuntimeExtension")>]
[<assembly: AssemblyMetadata("CSHVDNC.Id", "Script4Reload")>]
[<assembly: AssemblyMetadata("CSHVDNC.EntryType", "Script4Reload.Source.Script4ReloadExtension")>]
[<assembly: AssemblyMetadata("CSHVDNC.Provides", "scripts4.reload.policy")>]
[<assembly: AssemblyMetadata("CSHVDNC.Requires", "host.frame;package.lifecycle.transition;runtime.diagnostics")>]
[<assembly: AssemblyMetadata("CSHVDNC.ConditionalRequires", "input.actions")>]
do ()