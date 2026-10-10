# Community Script Hook V .NET Core

# English | [Tiếng Việt](README_VI.md)

## Introduction
- Developed based on [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) and [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced), Community Script Hook V .NET Core is a brand new design brings better support on modding than ever before on modern .NET Core.
- No unsafe builds guaranteed.

## Role
- Community Script Hook V .Net Core is developed with 6-infrastructure one-way model: *Host - Central Brain - Extended Contents - Dynamic Library - Inherited Class - Human Readables*.
- Role-based, not language-based. It means if you write for Community Script Hook V .Net Core whatever the language is, you only need contracts & suitable runtimes to be recognised.
- Subsequent infrastructure are totally unawared of the precedings, yet the precedings creates conditions for the subsequents to exist. This means, modifying from any infrastructure will normally not affect the precedings.
- You only need mandatory components to operate.
- Being one-way model, subsequent infrastures are various on requirements with precedings. Otherwise ecosystems will not work.
- Domino collapse: In order to prevent zombie code or unknown executions, branch design is made to operate. When roots belonging to an infrastructure collapses, any from subsequents requires which collapsed will be affected. However, if subsequents does not require from collapsed, still operate normally.
- Contract based design: In order to recognise other infrastructures, contracts are mode to be recognised instead of naming, so naming no longer matter.

### Host
- Importance: **Mandatory**
- Maximum Components: **1**
- Role: Work as a powerhouse for .NET Core and some low level executions from original Script Hook V, serve as one-time written, many-times reused . The main role on this level is just call low level executions & host .NET Core.

### Central Brain
- Importance: **Mandatory**
- Maximum Components: **1**
- Role: Use tickrates to manage lifetime of inherited classes. If classes shouldever fail, they will be retired and no longer available on lifetime. Starting with scripts4 folder, any file .dll does not have any inherited class are recognied as library, 1 or more classes will be recognised as inherited classes. However collapse ecosystem is managed on class-designed, rather than entire content from file .dll fails. If classes fail, any other classes does not require from collapsed still work normally. Same it does with extension at extensions folder.

### Extended Contents
- Importance: **Code-based**
- Maximum Components: **No limits**
- Role: Extended contents formerly seen [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) and [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced), now been split on many contents & types to operate.

### Dynamic Library
- Importance: **Code-based**
- Maximum Components: **Code-based**
- Role: Same as ever with [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) and [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced), this is where you want to commonize contents.

### Inherited Class
- Importance: **Code-based**
- Maximum Components: **No limits**
- Role: Main contents on modding, same as ever with [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) and [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced).

### Human Readables
- Importance: **Optional**
- Maximum Components: **Code-based**
- Role: Such as readable files like json, log, ini, etc... which mod can either read to operate or read-only kind.

## Components

### CoreCLRHostLoader (Script Hook V CoreCLR Host Loader)
- Language Target: `C++ 23`
- Descriptions:
1. Fully runtime based, means fully-supported Visual Basic, F# & C# (based on which you have on Computer). For F# modders, `FSharp.Core` is required to run. Also usable with preview editions.
2. Future-brain replacements ready without rewriting (In case replacing central brain).

### CommunityScriptHookVDotNetCore (Script Hook V .NET Core)
- Language Target: `C# 15 Preview`
- Descriptions: Responsible for mods lifetime, tickrates and so on.

### Alloc8orStandardNatives (Alloc8or's Standard Native Executables)
- Language Target: `C# 15 Preview` & `Embedded C# 15 Preview on PowerShell`
- Descriptions:
1. Based as Alloc8or's native executable website. You can now develop your native executables based on the website on either [Legacy](https://alloc8or.re/gta5/nativedb) or [Enhanced](https://alloc8or.re/gta5/nativedb/enhanced), so no more manual declarations or direct usage.
2. You don't need to list all codes just to update native catalog, Run built PowerShell file and it will be done. It's completely synchronous with the website.
3. 64-bit Native Executables are compressed with Brotli algorithm for compressing build size.
4. In order to update the catalog, you need to have [PowerShell](https://apps.microsoft.com/detail/9mz1snwt0n5d) or [PowerShell Preview](https://apps.microsoft.com/detail/9p95zzktnrn4) to execute.

### ScriptHookInput (Script Hook V Input)
- Language Target: `F# 7`
- Descriptions:
1. Based on FiveM's website, there are concluded 2 types of inputs, game input and device input.
2. Game input such as `INPUT_TALK`, `INPUT_CONTEXT`, etc. are part of the game, just go to settings and change.
3. Device input such as controller, keyboard and mouse.

### Script4Reload (Script4's Reload tool)
- Language Target: `F# 7`
- Descriptions:
1. It's the same reload ability from [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) và [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced) which modders got used to. However, there are 2 modes. 1 is Manual as ever, 2 is Synchronized, which you can't use manual reload key. That is meant to be done automatically.
2. No more game freeze, since reload is now moved to asynchronous type.
3. No more brute-force and all-at-once reload. For modders, having less reload workloads will have the game last longer instead of crash randomly early.

### LocalNativeMemories (Pool Memory & Low-level Native Resolver)
- Language Target: `C# 15 Preview`
- Descriptions: This answers what's in the game, such as how many objects, entities, vehicles & peds. Also the backup way if native call does not exist.

### CEventGenerator (Machine Code Event Generator)
- Language Target: `C++ 23`
- Descriptions: Tickrate sync or latency as polling type is completely replaced with event-based, driven via machine codes, removing the performance bottleneck roots.

### LowLevelEvents (Low Level Event Resolver)
- Language Target: `C# 15 Preview`
- Descriptions: Extract and convert low level CEvent(s) from machine codes to intermediate standards, which .NET Core projects uses. This also answers where it belongs.

### StandardGameOperations (Standard Code Table)
- Language Target: `C# 15 Preview`
- Descriptions: The easiest way to write codes, by using this, you will be bypassed lots of writing, such as declaring native calls manually, locally declaring game database and so on, even multiple operations of native calls, it's all here.

### LocalUserDebug (In-game Logging tool)
- Language Target: `C# 15 Preview`
- Descriptions: It's the same logging details, but now in-game, rather than reading log files consecutively.

## Requirements

### As End-user
- You only need 1 of 2 choice to make required components work.
1. Small component solution:
* [FSharp.Core](https://www.nuget.org/packages/fsharp.core) (for F# project, here has ScriptHookInput & Script4Reload)
* [.NET Host](https://www.nuget.org/packages/Microsoft.NETCore.App.Host.win-x64).
* [.NET Core Runtime](https://dotnet.microsoft.com/en-us/download/dotnet) (target-driven, based on requirements).

2. All-in-one solution: [.NET SDK](https://dotnet.microsoft.com/en-us/download/dotnet).

### As Co-Developers
- [Visual Studio 2026](https://visualstudio.microsoft.com) or [Visual Studio Insiders](https://visualstudio.microsoft.com/insiders).
- [.NET SDK](https://dotnet.microsoft.com/en-us/download/dotnet) (only requires on Previews, for Release is already part of Visual Studio Installer).

## Installation

### As End-user
- Small component solution:
1. Install [.NET Core Runtime](https://dotnet.microsoft.com/en-us/download/dotnet).
2. From [FSharp.Core](https://www.nuget.org/packages/fsharp.core), get `FSharp.Core.dll`.
3. From [.NET Host](https://www.nuget.org/packages/Microsoft.NETCore.App.Host.win-x64), get `nethost.dll`.
4. Move `FSharp.Core.dll` & `nethost.dll` to GTA V root directory.
- All-in-one solution: Simply install the SDK. SDK already have `FSharp.Core.dll`, `nethost.dll` and runtime to load.

### As Co-Developers
1. Install [Visual Studio 2026](https://visualstudio.microsoft.com) or [Visual Studio Insiders](https://visualstudio.microsoft.com/insiders).
2. Tick *.NET Desktop development* and select at least 2 required following components: *Development tools for .NET* & *F# desktop language support*.
3. Tick *Desktop development with C++* and select at least 2 required following components: *MSVC Build Tools for x64/x86* & *Windows 11 SDK*, depend on what you choose.
4. Open *Individual components* -> *Code tools* -> *Git for Windows*.
5. Then install the IDE.
6. Open the IDE.
7. Select `Clone a repository`, paste following to *Repository location*.
```text
https://github.com/Nozomu-san/CommunityScriptHookVDotNetCore
```
8. Select `Clone`.
9. Open `CommunityScriptHookVDotNetCore.slnx`.
10. Finished. You are good to go.

## Question: Can I remain modding on [Community Script Hook V .NET](https://github.com/scripthookvdotnet/scripthookvdotnet) or [Script Hook V .NET Enhanced](https://github.com/Chiheb-Bacha/ScriptHookVDotNetEnhanced)?
- Yes. You can stay modding on those APIs.