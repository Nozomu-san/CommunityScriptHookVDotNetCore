# Community Script Hook V .NET Core

# English | [Tiếng Việt](README_VI.md)

## Introduction
- Developed based on [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) and [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced), Community Script Hook V .NET Core is a brand new design brings better support on modding than ever before on modern .NET Core.
- .NET components are built on C++ 26, C# 15, F# 11 & Embedded C# 15 on PowerShell 7.7 Preview, in order to last as long as possible on future .NET Core releases.
- No unsafe builds guaranteed.

## Role
- Community Script Hook V .Net Core is developed with 6-infrastructure one-way model: *Host - Central Brain - Extended Contents - Dynamic Library - Inherited Class - Human Readables*.
- Role-based, not language-based. It means if you write for Community Script Hook V .Net Core whatever the language is, you only need contracts & suitable runtimes to be recognised.
- Subsequent infrastructure are totally unawared of the precedings, yet the precedings creates conditions for the subsequents to exist. This means, modifying from any infrastructure will normally not affect the precedings.
- Host and Central Brain only have 1 member each, yet that's enough to operate an entire .NET Core ecosystem on GTA V.
- Being one-way model, subsequent infrastures are various on requirements with precedings. Otherwise ecosystems will not work.
- Domino collapse: In order to prevent zombie code or unknown executions, branch design is made to operate. When roots belonging to an infrastructure collapses, any from subsequents requires which collapsed will be affected. However, if subsequents does not require from collapsed, still operate normally.
- Contract based design: In order to recognise other infrastructures, contracts are mode to be recognised instead of naming, so naming no longer matter.

### Host
Importance: Mandatory
Role: Work as a powerhouse for .NET Core and some low level executions from original Script Hook V, serve as one-time written, many-times reused . The main role on this level is just call low level executions & host .NET Core.

### Central Brain
Importance: Mandatory
Role: Use tickrates to manage lifetime of inherited classes. If classes shouldever fail, they will be retired and no longer available on lifetime. Starting with scripts4 folder, any file .dll does not have any inherited class are recognied as library, 1 or more classes will be recognised as inherited classes. However collapse ecosystem is managed on class-designed, rather than entire content from file .dll fails. If classes fail, any other classes does not require from collapsed still work normally.

### Extended Contents
Importance: Code-based
Role: Extended contents formerly seen [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) and [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced), now been split on many contents & types to operate.

### Dynamic Library
Importance: Code-based
Role: Same as ever with [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) and [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced), this is where you want to commonize contents.

### Inherited Class
Importance: Code-based
Role: Main contents on modding, same as ever with [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) and [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced).

### Human Readables
Importance: Optional
Role: Such as readable files like json, log, ini, etc... which mod can either read to operate or read-only kind.

## Components

### CoreCLRHostLoader (Script Hook V CoreCLR Host Loader)
- Fully runtime based, means fully-supported Visual Basic, F# & C# (based on which you have on Computer). For F# modders, FSharp.Core is required to run. Also usable with preview editions.
- Future-brain replacements ready without rewriting (In case replacing central brain).

### CommunityScriptHookVDotNetCore (Script Hook V .NET Core)
- Responsible for mods lifetime, tickrates and so on.

### Alloc8orStandardNatives (Alloc8or's Standard Native Executables)
- Based as Alloc8or's native executable website. You can now develop your native executables based on the website on either [Legacy](https://alloc8or.re/gta5/nativedb) or [Enhanced](https://alloc8or.re/gta5/nativedb/enhanced), so no more manual declarations or direct usage.
- You don't need to list all codes just to update native catalog, Run built PowerShell file and it will be done. It's completely synchronous with the website.
- 64-bit Native Executables are compressed with Brotli algorithm for compressing build size.
- In order to update the catalog, you need to have [PowerShell](https://apps.microsoft.com/detail/9mz1snwt0n5d) or (PowerShell Preview)[https://apps.microsoft.com/detail/9p95zzktnrn4] to execute.

### ScriptHookInput (Script Hook V Input)
- Based on FiveM's website, there are concluded 2 types of inputs, game input and device input.
- Game input such as `INPUT_TALK`, `INPUT_CONTEXT`, etc. are part of the game, just go to settings and change.
- Device input such as controller, keyboard and mouse.

### Script4Reload (Script4's Reload tool)
- It's the same reload ability from [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) và [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced) which modders got used to. However, there are 2 modes. 1 is Manual as ever, 2 is Synchronized, which you can't use manual reload key. That is meant to be done automatically.
- No more game freeze, since reload is now moved to asynchronous type.
- No more brute-force and all-at-once reload. For modders, having less reload workloads will have the game last longer instead of crash randomly early.

### LocalNativeMemories
- This answers what's in the game, such as how many objects, entities, vehicles & peds.

### CEventGenerator
- Tickrate sync or latency is completely replaced with event-based, driven via machine codes, which helps a lot on removing the performance bottleneck roots.

### LowLevelEvents
- Extract and convert low level CEvent(s) from machine codes to intermediate standards, which .NET Core projects uses. This also answers where it belongs.

### StandardGameOperations
- The easiest way to write codes, by using this, you will be bypassed lots of writing, such as declaring native calls manually, locally declaring game database and so on, even multiple operations of native calls, it's all here.

## Requirements

### As End-user
- [FSharp.Core](https://www.nuget.org/packages/fsharp.core) (for F# project, here has ScriptHookInput & Script4Reload).
- [.NET Core Runtime](https://dotnet.microsoft.com/en-us/download/dotnet) (target-driven, based on requirements).
- [.NET Host](https://www.nuget.org/packages/Microsoft.NETCore.App.Host.win-x64).

### As Co-Developers (Recommended since I am busy all the time)
- [Visual Studio 2026](https://visualstudio.microsoft.com) or [Visual Studio Insiders](https://visualstudio.microsoft.com/insiders).
- [.NET Core SDK](https://dotnet.microsoft.com/en-us/download/dotnet) (only requires on Previews, for Release is already part of Visual Studio Installer).

## Question: Can I remain modding on original SHVDN from either side?
- Yes. You can, but as long as you don't mess up with IO Exception due to duplications.