#include "EventLocator.hpp"

#include <Windows.h>
#include <winver.h>

#include <array>
#include <cstddef>
#include <cstdint>
#include <span>
#include <vector>

namespace
{
    using PatternByte = std::int16_t;

    constexpr std::array<PatternByte, 19> PedEventPattern{
        0x83, 0xBF, -1, -1, 0x00, 0x00, -1, 0x75, -1,
        0x48, 0x8B, 0xCF, 0xE8, -1, -1, -1, -1, 0x83, 0xBF
    };

    constexpr std::array<PatternByte, 16> GlobalEventPattern{
        0x81, 0xBF, -1, -1, 0x00, 0x00, -1, -1,
        0x00, 0x00, 0x75, -1, 0x48, 0x8B, 0xCF, 0xE8
    };

    constexpr std::array<PatternByte, 9> DamageEventPattern{
        0x21, 0x4D, 0xD8, 0x21, 0x4D, 0xDC, 0x41, 0x8B, 0xD8
    };

    constexpr std::array<PatternByte, 6> DamageProcessPattern2060{
        0x41, 0x8A, 0x40, 0x08, 0x84, 0xC1
    };

    constexpr std::array<PatternByte, 7> DamageProcessPatternPre2060{
        0x41, 0x80, 0x60, 0x09, 0xFC, 0x24, 0x40
    };


    struct ImageView final
    {
        const std::byte* Base{};
        std::size_t Size{};
    };

    [[nodiscard]] ImageView GetImageView() noexcept
    {
        const HMODULE module = GetModuleHandleW(nullptr);
        if (module == nullptr)
        {
            return {};
        }

        const auto* base = reinterpret_cast<const std::byte*>(module);
        const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE)
        {
            return {};
        }

        const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(
            base + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE)
        {
            return {};
        }

        return {base, nt->OptionalHeader.SizeOfImage};
    }


    [[nodiscard]] std::vector<const std::byte*> FindAll(
        ImageView image,
        std::span<const PatternByte> pattern,
        std::size_t maximum)
    {
        std::vector<const std::byte*> matches;
        if (image.Base == nullptr || pattern.empty() || image.Size < pattern.size())
        {
            return matches;
        }

        matches.reserve(maximum);
        const std::size_t last = image.Size - pattern.size();
        for (std::size_t offset = 0; offset <= last; ++offset)
        {
            bool matched = true;
            for (std::size_t index = 0; index < pattern.size(); ++index)
            {
                const PatternByte expected = pattern[index];
                if (expected >= 0 &&
                    std::to_integer<std::uint8_t>(image.Base[offset + index]) !=
                        static_cast<std::uint8_t>(expected))
                {
                    matched = false;
                    break;
                }
            }

            if (!matched)
            {
                continue;
            }

            matches.push_back(image.Base + offset);
            if (matches.size() >= maximum)
            {
                break;
            }
        }
        return matches;
    }


    [[nodiscard]] void* FindDamageProcess(
        ImageView image,
        std::uint32_t gameBuild)
    {
        if (gameBuild >= 2060)
        {
            const auto matches = FindAll(image, DamageProcessPattern2060, 1);
            if (!matches.empty())
            {
                constexpr std::ptrdiff_t EntryOffset = -0x56;
                return const_cast<std::byte*>(matches[0] + EntryOffset);
            }
            return nullptr;
        }

        const auto matches = FindAll(image, DamageProcessPatternPre2060, 1);
        if (!matches.empty())
        {
            constexpr std::ptrdiff_t EntryOffset = -0x5D;
            return const_cast<std::byte*>(matches[0] + EntryOffset);
        }
        return nullptr;
    }

    [[nodiscard]] std::uint32_t GetGameBuild() noexcept
    {
        std::array<wchar_t, MAX_PATH> path{};
        const DWORD length = GetModuleFileNameW(
            nullptr,
            path.data(),
            static_cast<DWORD>(path.size()));
        if (length == 0 || length >= path.size())
        {
            return 0;
        }

        DWORD ignored = 0;
        const DWORD versionSize = GetFileVersionInfoSizeW(path.data(), &ignored);
        if (versionSize == 0)
        {
            return 0;
        }

        std::vector<std::byte> version(versionSize);
        if (GetFileVersionInfoW(
                path.data(),
                0,
                versionSize,
                version.data()) == FALSE)
        {
            return 0;
        }

        VS_FIXEDFILEINFO* fixed = nullptr;
        UINT fixedSize = 0;
        if (VerQueryValueW(
                version.data(),
                L"\\",
                reinterpret_cast<void**>(&fixed),
                &fixedSize) == FALSE ||
            fixed == nullptr ||
            fixedSize < sizeof(VS_FIXEDFILEINFO))
        {
            return 0;
        }

        return HIWORD(fixed->dwFileVersionLS);
    }
}

namespace CEventGenerator
{
    EventLocatorResult LocateEventHooks() noexcept
    {
        EventLocatorResult result{};
        result.Targets.GameBuild = GetGameBuild();
        if (result.Targets.GameBuild == 0)
        {
            result.State = Status::UnsupportedGameBuild;
            return result;
        }

        const ImageView image = GetImageView();
        if (image.Base == nullptr)
        {
            result.State = Status::HookNotFound;
            return result;
        }

        const auto pedMatches = FindAll(image, PedEventPattern, 4);
        const auto globalMatches = FindAll(image, GlobalEventPattern, 2);
        const auto damageMatches = FindAll(image, DamageEventPattern, 2);

        if (pedMatches.size() != 2 ||
            globalMatches.size() != 1 ||
            damageMatches.size() != 1)
        {
            result.State = Status::HookNotFound;
            return result;
        }

        constexpr std::ptrdiff_t EntryOffset = -0x36;
        result.Targets.Functions[0] = const_cast<std::byte*>(
            pedMatches[0] + EntryOffset);
        result.Targets.Functions[1] = const_cast<std::byte*>(
            pedMatches[1] + EntryOffset);
        result.Targets.Functions[2] = const_cast<std::byte*>(
            globalMatches[0] + EntryOffset);

        constexpr std::ptrdiff_t DamageEntryOffset = -0x1F;
        result.Targets.DamageFunction = const_cast<std::byte*>(
            damageMatches[0] + DamageEntryOffset);
        result.Targets.DamageProcessFunction = FindDamageProcess(
            image,
            result.Targets.GameBuild);

        if (result.Targets.Functions[0] == result.Targets.Functions[1] ||
            result.Targets.Functions[0] == result.Targets.Functions[2] ||
            result.Targets.Functions[1] == result.Targets.Functions[2] ||
            result.Targets.DamageFunction == nullptr ||
            result.Targets.DamageFunction == result.Targets.Functions[0] ||
            result.Targets.DamageFunction == result.Targets.Functions[1] ||
            result.Targets.DamageFunction == result.Targets.Functions[2])
        {
            result.State = Status::HookNotFound;
            return result;
        }

        if (result.Targets.DamageProcessFunction == result.Targets.DamageFunction ||
            result.Targets.DamageProcessFunction == result.Targets.Functions[0] ||
            result.Targets.DamageProcessFunction == result.Targets.Functions[1] ||
            result.Targets.DamageProcessFunction == result.Targets.Functions[2])
        {
            result.Targets.DamageProcessFunction = nullptr;
        }

        result.State = Status::Ready;
        return result;
    }
}