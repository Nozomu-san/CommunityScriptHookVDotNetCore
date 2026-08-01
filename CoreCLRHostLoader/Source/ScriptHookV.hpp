#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>

#include <cstdint>

using ScriptHookVMain = void (*)();

__declspec(dllimport) void scriptRegister(
    HMODULE module,
    ScriptHookVMain scriptMain);

__declspec(dllimport) void scriptUnregister(
    HMODULE module);

__declspec(dllimport) void scriptWait(
    DWORD milliseconds);

__declspec(dllimport) void nativeInit(
    std::uint64_t hash);

__declspec(dllimport) void nativePush64(
    std::uint64_t value);

__declspec(dllimport) std::uint64_t* nativeCall();

#pragma comment(lib, "ScriptHookV.lib")
