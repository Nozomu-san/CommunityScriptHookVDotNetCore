# Community Script Hook V .NET Core

# English | [Tiếng Việt](README_VI.md)

## Introduction
- Developed based on [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) and [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced), Community Script Hook V .NET Core is a brand new design brings better support on modding than ever before on modern .NET Core.
- .NET components are built on latest C# 14 & F# 10 in order to last as long as possible on future .NET Core releases.
- No unsafe builds guaranteed.

## Components
### CoreCLRHostLoader (Script Hook V CoreCLR Host Loader)
- Powerhouse for .NET Core.
- Completely .NET Core scalable (including .NET Core Preview)
- Fully-supported Visual Basic, F# & C# (based on Runtime you have on Computer). For F# modders, FSharp.Core is required to run.
- Future-brain replacements ready without rewriting (In case replacing Script Hook V .NET 4).
### ScriptHookVDotNet4 (Script Hook V .NET Core)
- Responsible for mods lifetime, tickrates and so on. Sounds familiar? But no longer contains everything like used to be.
### Alloc8orStandardNatives (Alloc8or's Standard Native Executables)
- Based as Alloc8or's native executable website.
- You don't need to list all codes just to update native catalog, Run built PowerShell file and it will be done. It's completely synchronous with the website.
- 64-bit Native Executables are compressed with Brotli algorithm for compressing build size.
### ScriptHookInput (Script Hook V Input)
- Based on FiveM's website, there are concluded 2 types of inputs, game input and device input.
- Game input such as `INPUT_TALK`, `INPUT_CONTEXT`, etc. are part of the game, just go to settings and change.
- Device input such as controller, keyboard and mouse.
### Script4Reload (Script4's Reload tool)
- It's the same reload ability based on .NET Framework modders got used to. However, there are 2 modes. 1 is Manual as ever, 2 is Synchronized, which you can't use manual reload key. That is meant to be done automatically.
- No more game freeze, since reload is now moved to asynchronous type.
- No more brute-force and all-at-once reload. For modders, having less reload workloads will have the game last longer instead of crash randomly early.
## Requirements
### As End-user
- [FSharp.Core](https://www.nuget.org/packages/fsharp.core).
- [.NET Core Runtime](https://dotnet.microsoft.com/en-us/download/dotnet) (mod-driven, targeting versions are vary).

### As Co-Developers (Recommended since I am busy all the time)
- [Visual Studio 2026](https://visualstudio.microsoft.com) or [Visual Studio Insiders](https://visualstudio.microsoft.com/insiders).
- [.NET Core SDK](https://dotnet.microsoft.com/en-us/download/dotnet) (for Preview only, for Release is already part of Visual Studio Installer).

## Question: Can I remain modding on original SHVDN from either side?
- Yes. You can, but as long as you don't mess up with IO Exception due to duplications.