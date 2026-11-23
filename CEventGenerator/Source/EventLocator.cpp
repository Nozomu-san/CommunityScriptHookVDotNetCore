#include "EventLocator.hpp"
#include "MinHook/src/hde/hde64.h"

#include <Windows.h>
#include <winver.h>

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <span>
#include <unordered_map>
#include <unordered_set>
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

    constexpr std::array<PatternByte, 6> DamageProcessPatternCurrent{
        0x41, 0x8A, 0x40, 0x08, 0x84, 0xC1
    };

    constexpr std::array<PatternByte, 7> DamageProcessPatternLegacy{
        0x41, 0x80, 0x60, 0x09, 0xFC, 0x24, 0x40
    };

    constexpr std::array<PatternByte, 8> EnhancedCEventCountPattern{
        0x8B, 0xAD, -1, -1, -1, -1, 0x80, 0x3D
    };

    constexpr std::array<PatternByte, 4> EnhancedCEventStackPattern{
        0x48, 0x8B, 0x9C, 0xC8
    };

    constexpr std::array<PatternByte, 20> EnhancedEventTypePattern{
        0x80, 0xBF, -1, -1, -1, -1, -1, 0x75, -1, 0x48,
        0x8B, 0x07, 0x48, 0x89, 0xF9, 0x48, 0x89, 0xDA, 0xFF, 0x50
    };

    struct ImageView final
    {
        const std::byte* Base{};
        std::size_t Size{};
    };

    struct RuntimeFunctionView final
    {
        std::uint32_t BeginRva{};
        std::uint32_t EndRva{};

        [[nodiscard]] explicit operator bool() const noexcept
        {
            return BeginRva < EndRva;
        }

        [[nodiscard]] std::uint32_t Size() const noexcept
        {
            return EndRva - BeginRva;
        }
    };

    [[nodiscard]] std::array<wchar_t, MAX_PATH> GetExecutablePath() noexcept
    {
        std::array<wchar_t, MAX_PATH> path{};
        const DWORD length = GetModuleFileNameW(
            nullptr,
            path.data(),
            static_cast<DWORD>(path.size()));
        if (length == 0 || length >= path.size())
        {
            path.fill(L'\0');
        }
        return path;
    }

    [[nodiscard]] CEventGenerator::GameEdition DetectEdition(
        const std::array<wchar_t, MAX_PATH>& path) noexcept
    {
        const wchar_t* fileName = std::wcsrchr(path.data(), L'\\');
        fileName = fileName == nullptr ? path.data() : fileName + 1;
        if (_wcsicmp(fileName, L"GTA5_Enhanced.exe") == 0)
        {
            return CEventGenerator::GameEdition::Enhanced;
        }
        if (_wcsicmp(fileName, L"GTA5.exe") == 0)
        {
            return CEventGenerator::GameEdition::Legacy;
        }
        return CEventGenerator::GameEdition::Unknown;
    }

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

    [[nodiscard]] std::vector<const std::byte*> FindAllExecutable(
        ImageView image,
        std::span<const PatternByte> pattern,
        std::size_t maximum)
    {
        std::vector<const std::byte*> matches;
        if (image.Base == nullptr || pattern.empty() || maximum == 0 ||
            image.Size < pattern.size())
        {
            return matches;
        }
        matches.reserve(maximum);
        const std::uintptr_t base =
            reinterpret_cast<std::uintptr_t>(image.Base);
        const std::uintptr_t limit = base + image.Size;
        if (limit < base)
        {
            return matches;
        }
        for (std::uintptr_t address = base; address < limit;)
        {
            MEMORY_BASIC_INFORMATION info{};
            if (VirtualQuery(reinterpret_cast<const void*>(address),
                    &info, sizeof(info)) != sizeof(info))
            {
                break;
            }
            const std::uintptr_t regionBase =
                reinterpret_cast<std::uintptr_t>(info.BaseAddress);
            const std::uintptr_t regionLimit = regionBase + info.RegionSize;
            if (regionLimit <= address || regionLimit < regionBase)
            {
                break;
            }
            const std::uintptr_t end = std::min(regionLimit, limit);
            const DWORD protection = info.Protect & 0xFFu;
            const bool executable = info.State == MEM_COMMIT &&
                (info.Protect & PAGE_GUARD) == 0u &&
                (protection == PAGE_EXECUTE ||
                 protection == PAGE_EXECUTE_READ ||
                 protection == PAGE_EXECUTE_READWRITE ||
                 protection == PAGE_EXECUTE_WRITECOPY);
            if (executable && end - address >= pattern.size())
            {
                for (std::uintptr_t current = address;
                     current <= end - pattern.size(); ++current)
                {
                    bool matched = true;
                    for (std::size_t index = 0; index < pattern.size(); ++index)
                    {
                        const PatternByte expected = pattern[index];
                        if (expected >= 0 &&
                            std::to_integer<std::uint8_t>(
                                *reinterpret_cast<const std::byte*>(current + index)) !=
                                static_cast<std::uint8_t>(expected))
                        {
                            matched = false;
                            break;
                        }
                    }
                    if (matched)
                    {
                        matches.push_back(
                            reinterpret_cast<const std::byte*>(current));
                        if (matches.size() == maximum)
                        {
                            return matches;
                        }
                    }
                }
            }
            address = end;
        }
        return matches;
    }

    [[nodiscard]] bool Contains(
        ImageView image,
        const std::byte* address,
        std::size_t size) noexcept
    {
        if (image.Base == nullptr || address < image.Base)
        {
            return false;
        }
        const auto offset = static_cast<std::size_t>(address - image.Base);
        return offset <= image.Size && size <= image.Size - offset;
    }

    [[nodiscard]] std::int32_t ReadInt32(const std::byte* address) noexcept
    {
        std::int32_t value{};
        std::memcpy(&value, address, sizeof(value));
        return value;
    }

    [[nodiscard]] RuntimeFunctionView ResolveRuntimeFunction(
        ImageView image,
        const std::byte* address) noexcept
    {
        if (!Contains(image, address, 1))
        {
            return {};
        }

        DWORD64 imageBase = 0;
        const PRUNTIME_FUNCTION function = RtlLookupFunctionEntry(
            reinterpret_cast<DWORD64>(address),
            &imageBase,
            nullptr);
        if (function == nullptr ||
            imageBase != reinterpret_cast<DWORD64>(image.Base) ||
            function->BeginAddress >= function->EndAddress ||
            function->EndAddress > image.Size)
        {
            return {};
        }

        return {function->BeginAddress, function->EndAddress};
    }


    [[nodiscard]] std::uint8_t ByteAt(const std::byte* address) noexcept
    {
        return std::to_integer<std::uint8_t>(*address);
    }

    struct EnhancedReceiverShape final
    {
        std::size_t Length{};
        std::uint8_t GroupRegister{};
        bool WideImmediate{};
    };

    [[nodiscard]] EnhancedReceiverShape DecodeEnhancedComparison(
        const std::byte* address,
        std::size_t remaining) noexcept
    {
        if (remaining < 8)
        {
            return {};
        }

        std::size_t cursor = 0;
        std::uint8_t rex = 0;
        if ((ByteAt(address) & 0xF0u) == 0x40u)
        {
            rex = ByteAt(address);
            ++cursor;
        }

        if (remaining < cursor + 7)
        {
            return {};
        }

        const std::uint8_t opcode = ByteAt(address + cursor++);
        if (opcode != 0x83u && opcode != 0x81u)
        {
            return {};
        }
        const std::uint8_t modrm = ByteAt(address + cursor++);
        const std::uint8_t mode = modrm >> 6;
        std::uint8_t base = modrm & 7u;
        if (((modrm >> 3) & 7u) != 7u || mode != 2u)
        {
            return {};
        }
        if (base == 4u)
        {
            if (remaining <= cursor)
            {
                return {};
            }
            const std::uint8_t sib = ByteAt(address + cursor++);
            if (((sib >> 3) & 7u) != 4u)
            {
                return {};
            }
            base = sib & 7u;
        }

        const std::size_t immediate = opcode == 0x81u ? 4u : 1u;
        if (remaining < cursor + 4u + immediate)
        {
            return {};
        }
        cursor += 4u + immediate;
        return {cursor, static_cast<std::uint8_t>(base + ((rex & 1u) ? 8u : 0u)),
            opcode == 0x81u};
    }

    [[nodiscard]] bool IsEnhancedGroupTransfer(
        const std::byte* address,
        std::size_t available,
        std::uint8_t source) noexcept
    {
        if (available < 3 || (ByteAt(address) & 0xF8u) != 0x48u)
        {
            return false;
        }
        const std::uint8_t rex = ByteAt(address);
        const std::uint8_t opcode = ByteAt(address + 1);
        const std::uint8_t modrm = ByteAt(address + 2);
        if ((modrm & 0xC0u) != 0xC0u || (rex & 0x08u) == 0)
        {
            return false;
        }
        const std::uint8_t lowSource = source & 7u;
        const bool highSource = source >= 8u;
        if (opcode == 0x8Bu)
        {
            return ((modrm >> 3) & 7u) == 1u &&
                ((rex & 4u) == 0) &&
                (modrm & 7u) == lowSource &&
                (((rex & 1u) != 0) == highSource);
        }
        if (opcode == 0x89u)
        {
            return (modrm & 7u) == 1u &&
                ((rex & 1u) == 0) &&
                ((modrm >> 3) & 7u) == lowSource &&
                (((rex & 4u) != 0) == highSource);
        }
        return false;
    }

    [[nodiscard]] const std::byte* ResolveEnhancedCodeReference(
        ImageView image,
        const std::byte* instruction) noexcept
    {
        if (!Contains(image, instruction, 5))
        {
            return nullptr;
        }

        std::int64_t destination = 0;
        if (ByteAt(instruction) == 0xE8u || ByteAt(instruction) == 0xE9u)
        {
            destination = static_cast<std::int64_t>(instruction - image.Base) +
                5 + ReadInt32(instruction + 1);
        }
        else if (Contains(image, instruction, 6) &&
            ByteAt(instruction) == 0xFFu &&
            (ByteAt(instruction + 1) == 0x15u ||
             ByteAt(instruction + 1) == 0x25u))
        {
            const std::int64_t slotOffset =
                static_cast<std::int64_t>(instruction - image.Base) +
                6 + ReadInt32(instruction + 2);
            if (slotOffset < 0 ||
                static_cast<std::uint64_t>(slotOffset) > image.Size ||
                sizeof(std::uintptr_t) > image.Size -
                    static_cast<std::size_t>(slotOffset))
            {
                return nullptr;
            }
            std::uintptr_t pointerValue = 0;
            std::memcpy(&pointerValue,
                image.Base + slotOffset, sizeof(pointerValue));
            const auto base = reinterpret_cast<std::uintptr_t>(image.Base);
            if (pointerValue < base || pointerValue - base >= image.Size)
            {
                return nullptr;
            }
            destination = static_cast<std::int64_t>(pointerValue - base);
        }
        else
        {
            return nullptr;
        }

        if (destination < 0 ||
            static_cast<std::uint64_t>(destination) >= image.Size)
        {
            return nullptr;
        }
        return image.Base + destination;
    }

    struct EnhancedReceiverCandidate final
    {
        const std::byte* Entry{};
        const std::byte* Callee{};
        const std::byte* Comparison{};
        const std::byte* Transfer{};
        const std::byte* Callsite{};
        std::uint8_t GroupRegister{};
        bool WideImmediate{};
        bool Indirect{};
    };

    struct EnhancedCallResolution final
    {
        const std::byte* Callee{};
        const std::byte* Transfer{};
        const std::byte* Callsite{};
        bool Indirect{};
    };

    [[nodiscard]] const std::byte* ResolveEnhancedCallableTarget(
        ImageView image,
        const std::byte* instruction) noexcept
    {
        const std::byte* target = ResolveEnhancedCodeReference(
            image, instruction);
        for (unsigned int depth = 0; depth < 3 && target != nullptr; ++depth)
        {
            if (!Contains(image, target, 1))
            {
                return nullptr;
            }
            const std::uint8_t opcode = ByteAt(target);
            const bool forwarder = opcode == 0xE9u ||
                (Contains(image, target, 6) && opcode == 0xFFu &&
                 ByteAt(target + 1) == 0x25u);
            if (!forwarder)
            {
                const RuntimeFunctionView function =
                    ResolveRuntimeFunction(image, target);
                if (!function ||
                    target != image.Base + function.BeginRva)
                {
                    return nullptr;
                }
                return target;
            }
            target = ResolveEnhancedCodeReference(image, target);
        }
        return nullptr;
    }

    [[nodiscard]] EnhancedCallResolution FindEnhancedReceiverCallee(
        ImageView image,
        const std::byte* address,
        std::size_t position,
        std::uint8_t group,
        bool allowPointerFallback) noexcept
    {
        const RuntimeFunctionView outer = ResolveRuntimeFunction(image, address);
        if (!outer)
        {
            return {};
        }
        const std::byte* limit = image.Base + outer.EndRva;
        const std::byte* branch = address + position;
        const std::byte* lastBranch = branch + 12;
        for (; branch < limit && branch < lastBranch; ++branch)
        {
            std::size_t branchLength = 0;
            if (ByteAt(branch) == 0x75u)
            {
                branchLength = 2;
            }
            else if (branch + 1 < limit && ByteAt(branch) == 0x0Fu &&
                     ByteAt(branch + 1) == 0x85u)
            {
                branchLength = 6;
            }
            if (branchLength == 0 || branch + branchLength > limit)
            {
                continue;
            }

            const std::byte* afterBranch = branch + branchLength;
            const std::byte* transferLimit = afterBranch + 24;
            for (const std::byte* transfer = afterBranch;
                 transfer + 3 <= limit && transfer < transferLimit; ++transfer)
            {
                if (!IsEnhancedGroupTransfer(transfer,
                    static_cast<std::size_t>(limit - transfer), group))
                {
                    continue;
                }
                const std::byte* callLimit = transfer + 24;
                for (const std::byte* call = transfer + 3;
                     call + 5 <= limit && call < callLimit; ++call)
                {
                    const bool direct = ByteAt(call) == 0xE8u;
                    const bool pointer = allowPointerFallback &&
                        call + 6 <= limit && ByteAt(call) == 0xFFu &&
                        ByteAt(call + 1) == 0x15u;
                    if (direct || pointer)
                    {
                        if (const std::byte* target =
                            ResolveEnhancedCallableTarget(image, call))
                        {
                            return {target, transfer, call, pointer};
                        }
                    }
                }
            }
        }
        return {};
    }

    [[nodiscard]] std::vector<EnhancedReceiverCandidate> FindEnhancedReceivers(
        ImageView image,
        bool wideImmediate,
        std::size_t maximum,
        bool allowPointerFallback)
    {
        std::vector<EnhancedReceiverCandidate> result;
        result.reserve(maximum + 1);
        if (image.Base == nullptr || image.Size < 40)
        {
            return result;
        }
        for (std::size_t offset = 0; offset + 40 < image.Size; ++offset)
        {
            const std::uint8_t first = ByteAt(image.Base + offset);
            if (first != 0x81u && first != 0x83u &&
                (first & 0xF0u) != 0x40u)
            {
                continue;
            }
            const auto decoded = DecodeEnhancedComparison(
                image.Base + offset, image.Size - offset);
            if (decoded.Length == 0 || decoded.WideImmediate != wideImmediate)
            {
                continue;
            }
            const EnhancedCallResolution resolution = FindEnhancedReceiverCallee(
                image, image.Base + offset, decoded.Length,
                decoded.GroupRegister, allowPointerFallback);
            if (resolution.Callee == nullptr)
            {
                continue;
            }
            const RuntimeFunctionView function =
                ResolveRuntimeFunction(image, image.Base + offset);
            if (!function)
            {
                continue;
            }
            const std::byte* entry = image.Base + function.BeginRva;
            const bool duplicate = std::any_of(result.begin(), result.end(),
                [&](const EnhancedReceiverCandidate& candidate)
                {
                    return candidate.Entry == entry &&
                        candidate.Callee == resolution.Callee;
                });
            if (!duplicate)
            {
                result.push_back({entry, resolution.Callee,
                    image.Base + offset, resolution.Transfer, resolution.Callsite,
                    decoded.GroupRegister, decoded.WideImmediate,
                    resolution.Indirect});
                if (result.size() > maximum)
                {
                    break;
                }
            }
        }
        return result;
    }

    struct EnhancedSecondArgumentObservation final
    {
        std::uint32_t State{};
        std::uint32_t WriteRva{};
    };

    [[nodiscard]] EnhancedSecondArgumentObservation ObserveEnhancedSecondArgument(
        ImageView image, const EnhancedReceiverCandidate& candidate) noexcept
    {
        EnhancedSecondArgumentObservation result{};
        if (candidate.Transfer == nullptr || candidate.Callsite == nullptr ||
            candidate.Transfer >= candidate.Callsite ||
            !Contains(image, candidate.Transfer, 1) ||
            !Contains(image, candidate.Callsite, 1))
        {
            return result;
        }
        const auto extent = static_cast<std::size_t>(
            candidate.Callsite - candidate.Transfer);
        if (extent > 64u)
        {
            return result;
        }
        const std::byte* cursor = candidate.Transfer;
        for (std::uint32_t instructions = 0;
             cursor < candidate.Callsite && instructions < 20u; ++instructions)
        {
            const auto remaining = static_cast<std::size_t>(
                candidate.Callsite - cursor);
            std::array<std::uint8_t, 15> bytes{};
            const auto available = std::min(bytes.size(), remaining);
            std::memcpy(bytes.data(), cursor, available);
            hde64s instruction{};
            const unsigned int length = hde64_disasm(bytes.data(), &instruction);
            if (length == 0u || length > available ||
                (instruction.flags & F_ERROR) != 0u)
            {
                return {};
            }
            const auto opcode = instruction.opcode;
            if (opcode == 0xE8u || opcode == 0xE9u || opcode == 0xEBu ||
                (opcode >= 0x70u && opcode <= 0x7Fu) ||
                (opcode >= 0xE0u && opcode <= 0xE3u) ||
                (opcode == 0x0Fu && instruction.opcode2 >= 0x80u &&
                 instruction.opcode2 <= 0x8Fu) || opcode == 0xFFu)
            {
                return {};
            }
            const bool modrm = (instruction.flags & F_MODRM) != 0u;
            const bool rdxReg = modrm && instruction.rex_r == 0u &&
                instruction.modrm_reg == 2u;
            const bool rdxRm = modrm && instruction.rex_b == 0u &&
                instruction.modrm_rm == 2u && instruction.modrm_mod == 3u;
            std::uint32_t newState = 0u;
            bool written = false;
            if (rdxRm && ((opcode == 0x31u && rdxReg) ||
                          (opcode == 0x29u && rdxReg)))
            {
                newState = 1u;
                written = true;
            }
            else if (rdxReg && instruction.modrm_mod == 3u &&
                ((opcode == 0x33u && rdxRm) ||
                 (opcode == 0x2Bu && rdxRm)))
            {
                newState = 1u;
                written = true;
            }
            else if (opcode == 0xBAu && instruction.rex_b == 0u)
            {
                newState = instruction.rex_w == 0u &&
                    (instruction.flags & F_IMM32) != 0u &&
                    instruction.imm.imm32 == 0u ? 1u : 0u;
                written = true;
            }
            else if ((opcode == 0x8Bu || opcode == 0x8Du) && rdxReg)
            {
                newState = instruction.rex_w == 0u ? 0u :
                    opcode == 0x8Du || instruction.modrm_mod != 3u ? 3u : 2u;
                written = true;
            }
            else if (opcode == 0x89u && rdxRm)
            {
                newState = instruction.rex_w != 0u ? 2u : 0u;
                written = true;
            }
            else if (rdxRm && (opcode == 0x01u || opcode == 0x09u ||
                opcode == 0x21u || opcode == 0x29u || opcode == 0x31u ||
                opcode == 0x81u || opcode == 0x83u || opcode == 0xC7u))
            {
                written = true;
            }
            else if (rdxReg && (opcode == 0x03u || opcode == 0x0Bu ||
                opcode == 0x23u || opcode == 0x2Bu || opcode == 0x33u))
            {
                written = true;
            }
            if (written)
            {
                result.State = newState;
                result.WriteRva = static_cast<std::uint32_t>(cursor - image.Base);
            }
            cursor += length;
        }
        if (cursor != candidate.Callsite)
        {
            return {};
        }
        return result;
    }

    void RecordEnhancedOutgoingAbi(
        ImageView image,
        const std::vector<EnhancedReceiverCandidate>& input,
        std::uint32_t kind,
        CEventGenerator::EventLocatorDiagnostics& diagnostics)
    {
        for (const auto& candidate : input)
        {
            const auto abi = ObserveEnhancedSecondArgument(image, candidate);
            if (abi.State == 1u)
            {
                if (kind == 1u) ++diagnostics.EnhancedHybridPedOutgoingZero;
                else ++diagnostics.EnhancedHybridGlobalOutgoingZero;
            }
            if (abi.State == 2u)
            {
                if (kind == 1u) ++diagnostics.EnhancedHybridPedForwarded;
                else ++diagnostics.EnhancedHybridGlobalForwarded;
            }
            if (abi.State == 3u)
            {
                if (kind == 1u) ++diagnostics.EnhancedHybridPedAddressDerived;
                else ++diagnostics.EnhancedHybridGlobalAddressDerived;
            }
            if (abi.State != 0u)
            {
                const std::uint32_t stateQuota = kind == 1u
                    ? 4u : (abi.State == 3u ? 2u : 3u);
                std::uint32_t sameStateCount = 0;
                for (std::uint32_t j = 0;
                     j < diagnostics.EnhancedHybridAbiSampleCount; ++j)
                {
                    const auto& sample = diagnostics.EnhancedHybridAbiSamples[j];
                    if (sample.Kind == kind && sample.State == abi.State)
                    {
                        ++sameStateCount;
                    }
                }
                const std::uint32_t index = sameStateCount < stateQuota &&
                    diagnostics.EnhancedHybridAbiSampleCount <
                        diagnostics.EnhancedHybridAbiSamples.size()
                    ? diagnostics.EnhancedHybridAbiSampleCount++ : UINT32_MAX;
                if (index != UINT32_MAX)
                {
                    auto& entry = diagnostics.EnhancedHybridAbiSamples[index];
                    entry.EntryRva = static_cast<std::uint32_t>(
                        candidate.Entry - image.Base);
                    entry.CallsiteRva = static_cast<std::uint32_t>(
                        candidate.Callsite - image.Base);
                    entry.CalleeRva = static_cast<std::uint32_t>(
                        candidate.Callee - image.Base);
                    entry.LastRdxWriteRva = abi.WriteRva;
                    entry.ContextRva = static_cast<std::uint32_t>(
                        candidate.Transfer - image.Base);
                    const auto available = image.Size - entry.ContextRva;
                    const auto windowLength = static_cast<std::size_t>(
                        candidate.Callsite - candidate.Transfer) +
                        (candidate.Indirect ? 6u : 5u);
                    entry.ContextLength = static_cast<std::uint32_t>(
                        std::min({entry.Context.size(), available, windowLength}));
                    std::memcpy(entry.Context.data(),
                        image.Base + entry.ContextRva, entry.ContextLength);
                    entry.Kind = kind;
                    entry.State = abi.State;
                }
            }
        }
    }

    struct EnhancedReceiverTriplet final
    {
        std::array<const std::byte*, 3> Entries{};
        const std::byte* Callee{};
        std::uint32_t UniqueGroups{};
    };

    [[nodiscard]] EnhancedReceiverTriplet CorrelateEnhancedReceivers(
        const std::vector<EnhancedReceiverCandidate>& ped,
        const std::vector<EnhancedReceiverCandidate>& global,
        bool complete) noexcept
    {
        EnhancedReceiverTriplet result{};
        if (!complete)
        {
            return result;
        }
        for (const auto& source : global)
        {
            const std::byte* callee = source.Callee;
            std::array<const std::byte*, 3> unique{};
            std::size_t pedCount = 0;
            for (const auto& candidate : ped)
            {
                if (candidate.Callee != callee)
                {
                    continue;
                }
                if (pedCount < 3 &&
                    std::find(unique.begin(), unique.begin() + pedCount,
                        candidate.Entry) == unique.begin() + pedCount)
                {
                    unique[pedCount++] = candidate.Entry;
                }
                if (pedCount > 2)
                {
                    break;
                }
            }
            if (pedCount != 2)
            {
                continue;
            }
            const std::byte* third = source.Entry;
            if (third == unique[0] || third == unique[1])
            {
                continue;
            }
            std::size_t globalCount = 0;
            for (const auto& candidate : global)
            {
                if (candidate.Callee == callee &&
                    candidate.Entry != third)
                {
                    ++globalCount;
                    break;
                }
            }
            if (globalCount != 0)
            {
                continue;
            }
            ++result.UniqueGroups;
            if (result.UniqueGroups == 1)
            {
                result.Entries = {unique[0], unique[1], third};
                result.Callee = callee;
            }
        }
        if (result.UniqueGroups != 1)
        {
            result.Entries = {};
            result.Callee = nullptr;
        }
        return result;
    }

    struct IncomingCallTrace final
    {
        std::uint32_t Boundary{};
        std::uint32_t Branches{};
        std::uint32_t RcxState{};
        std::uint32_t RdxState{};
        std::uint32_t RcxWriteOffset{UINT32_MAX};
        std::uint32_t RdxWriteOffset{UINT32_MAX};
    };

    [[nodiscard]] IncomingCallTrace TraceIncomingCallerCode(
        const std::byte* code, std::size_t callOffset) noexcept
    {
        IncomingCallTrace trace{};
        if (code == nullptr || callOffset > 32768u)
        {
            return trace;
        }
        std::size_t cursor = 0u;
        while (cursor < callOffset)
        {
            const auto available = std::min<std::size_t>(
                15u, callOffset - cursor);
            std::array<std::uint8_t, 15> bytes{};
            std::memcpy(bytes.data(), code + cursor, available);
            hde64s instruction{};
            const auto length = hde64_disasm(bytes.data(), &instruction);
            if (length == 0u || length > available ||
                (instruction.flags & F_ERROR) != 0u ||
                instruction.opcode == 0xCCu)
            {
                return trace;
            }
            const auto op = instruction.opcode;
            const bool modrm = (instruction.flags & F_MODRM) != 0u;
            const auto reg = static_cast<std::uint32_t>(
                instruction.modrm_reg + (instruction.rex_r != 0u ? 8u : 0u));
            const auto rm = static_cast<std::uint32_t>(
                instruction.modrm_rm + (instruction.rex_b != 0u ? 8u : 0u));
            const bool registerRm = modrm && instruction.modrm_mod == 3u;
            const auto write = [&](std::uint32_t destination,
                                   std::uint32_t state)
            {
                if (destination == 1u)
                {
                    trace.RcxState = state;
                    trace.RcxWriteOffset = static_cast<std::uint32_t>(cursor);
                }
                else if (destination == 2u)
                {
                    trace.RdxState = state;
                    trace.RdxWriteOffset = static_cast<std::uint32_t>(cursor);
                }
            };
            const auto invalidate = [&]()
            {
                trace.RcxState = trace.RdxState = 0u;
                trace.RcxWriteOffset = trace.RdxWriteOffset = UINT32_MAX;
            };
            const bool conditional =
                (op >= 0x70u && op <= 0x7Fu) ||
                (op >= 0xE0u && op <= 0xE3u) ||
                (op == 0x0Fu && instruction.opcode2 >= 0x80u &&
                 instruction.opcode2 <= 0x8Fu);
            const bool transfer = op == 0xE8u || op == 0xE9u ||
                op == 0xEBu || op == 0xFFu || conditional;
            if (transfer)
            {
                ++trace.Branches;
                invalidate();
            }
            else if (op == 0x8Bu && modrm)
            {
                write(reg, instruction.rex_w != 0u
                    ? (registerRm ? 2u : 3u) : 0u);
            }
            else if (op == 0x8Du && modrm)
            {
                write(reg, instruction.rex_w != 0u ? 3u : 0u);
            }
            else if (op == 0x89u && registerRm)
            {
                write(rm, instruction.rex_w != 0u ? 2u : 0u);
            }
            else if ((op == 0x31u || op == 0x29u) && registerRm)
            {
                write(rm, reg == rm ? 1u : 0u);
            }
            else if ((op == 0x33u || op == 0x2Bu) && registerRm)
            {
                write(reg, reg == rm ? 1u : 0u);
            }
            else if ((op >= 0xB8u && op <= 0xBFu))
            {
                const auto dest = static_cast<std::uint32_t>(
                    op - 0xB8u + (instruction.rex_b != 0u ? 8u : 0u));
                const bool zero = (instruction.flags & F_IMM64) != 0u
                    ? instruction.imm.imm64 == 0u
                    : (instruction.flags & F_IMM32) != 0u &&
                      instruction.imm.imm32 == 0u;
                write(dest, zero ? 1u : 4u);
            }
            else if (op == 0xC7u && registerRm &&
                     instruction.modrm_reg == 0u)
            {
                write(rm, instruction.imm.imm32 == 0u ? 1u : 4u);
            }
            else if (op == 0x81u || op == 0x83u || op == 0x01u ||
                     op == 0x09u || op == 0x21u || op == 0x29u ||
                     op == 0x31u || op == 0xC1u || op == 0xD1u)
            {
                if (registerRm && !(op == 0x81u &&
                    instruction.modrm_reg == 7u))
                {
                    write(rm, 0u);
                }
            }
            else if (op == 0x03u || op == 0x0Bu || op == 0x23u ||
                     op == 0x2Bu || op == 0x33u)
            {
                if (modrm) write(reg, 0u);
            }
            else if (op >= 0x58u && op <= 0x5Fu)
            {
                write(static_cast<std::uint32_t>(
                    op - 0x58u + (instruction.rex_b != 0u ? 8u : 0u)), 0u);
            }
            else if (op == 0xC3u || op == 0xC2u || op == 0xCBu ||
                     op == 0xCAu)
            {
                return trace;
            }
            cursor += length;
        }
        std::array<std::uint8_t, 15> bytes{};
        std::memcpy(bytes.data(), code + callOffset, 5u);
        hde64s call{};
        const auto callLength = hde64_disasm(bytes.data(), &call);
        if (cursor == callOffset && callLength == 5u &&
            call.opcode == 0xE8u && (call.flags & F_ERROR) == 0u)
        {
            trace.Boundary = 1u;
            if (trace.RcxWriteOffset == UINT32_MAX ||
                callOffset - trace.RcxWriteOffset > 96u)
            {
                trace.RcxState = 0u;
                trace.RcxWriteOffset = UINT32_MAX;
            }
            if (trace.RdxWriteOffset == UINT32_MAX ||
                callOffset - trace.RdxWriteOffset > 96u)
            {
                trace.RdxState = 0u;
                trace.RdxWriteOffset = UINT32_MAX;
            }
        }
        return trace;
    }

    struct IncomingCfgTrace final
    {
        std::uint32_t Boundary{};
        std::uint32_t Instructions{};
        std::uint32_t Edges{};
        std::uint32_t UnknownExits{};
    };

    [[nodiscard]] IncomingCfgTrace TraceIncomingCallerGraph(
        const std::byte* code, std::size_t functionSize,
        std::size_t callOffset)
    {
        IncomingCfgTrace result{};
        if (code == nullptr || callOffset >= functionSize ||
            callOffset >= 32768u)
        {
            return result;
        }
        functionSize = std::min<std::size_t>(functionSize, 32768u);
        std::vector<std::uint8_t> visited(functionSize);
        std::vector<std::uint32_t> pending{0u};
        while (!pending.empty() && result.Instructions < 16384u)
        {
            std::size_t cursor = pending.back();
            pending.pop_back();
            while (cursor < functionSize && !visited[cursor] &&
                   result.Instructions < 16384u)
            {
                visited[cursor] = 1u;
                ++result.Instructions;
                const auto available = std::min<std::size_t>(
                    15u, functionSize - cursor);
                std::array<std::uint8_t, 15> bytes{};
                std::memcpy(bytes.data(), code + cursor, available);
                hde64s instruction{};
                const auto length = hde64_disasm(bytes.data(), &instruction);
                if (length == 0u || length > available ||
                    (instruction.flags & F_ERROR) != 0u)
                {
                    ++result.UnknownExits;
                    break;
                }
                const auto op = instruction.opcode;
                if (cursor == callOffset && op == 0xE8u && length == 5u)
                {
                    result.Boundary = 1u;
                    return result;
                }
                if (op == 0xC3u || op == 0xC2u || op == 0xCBu ||
                    op == 0xCAu || op == 0xCCu ||
                    (op == 0x0Fu && instruction.opcode2 == 0x0Bu))
                {
                    break;
                }
                const bool conditional =
                    (op >= 0x70u && op <= 0x7Fu) ||
                    (op >= 0xE0u && op <= 0xE3u) ||
                    (op == 0x0Fu && instruction.opcode2 >= 0x80u &&
                     instruction.opcode2 <= 0x8Fu);
                const bool directJump = op == 0xE9u || op == 0xEBu;
                if (conditional || directJump)
                {
                    const bool shortDisplacement =
                        (op >= 0x70u && op <= 0x7Fu) ||
                        (op >= 0xE0u && op <= 0xE3u) || op == 0xEBu;
                    const auto displacement = shortDisplacement
                        ? static_cast<std::int64_t>(
                            static_cast<std::int8_t>(instruction.imm.imm8))
                        : static_cast<std::int64_t>(
                            static_cast<std::int32_t>(instruction.imm.imm32));
                    const auto target = static_cast<std::int64_t>(cursor) +
                        static_cast<std::int64_t>(length) + displacement;
                    ++result.Edges;
                    if (target >= 0 &&
                        static_cast<std::uint64_t>(target) < functionSize)
                    {
                        if (!visited[static_cast<std::size_t>(target)])
                        {
                            pending.push_back(static_cast<std::uint32_t>(target));
                        }
                    }
                    else
                    {
                        ++result.UnknownExits;
                    }
                    if (directJump)
                    {
                        break;
                    }
                }
                if (op == 0xFFu &&
                    (instruction.flags & F_MODRM) != 0u &&
                    (instruction.modrm_reg == 4u ||
                     instruction.modrm_reg == 5u))
                {
                    ++result.UnknownExits;
                    break;
                }
                if (op == 0xEAu)
                {
                    ++result.UnknownExits;
                    break;
                }
                cursor += length;
            }
        }
        if (!pending.empty())
        {
            ++result.UnknownExits;
        }
        return result;
    }

    struct IncomingCfgArgumentTrace final
    {
        std::uint32_t Boundary{};
        std::uint32_t Visits{};
        std::uint32_t Edges{};
        std::uint32_t UnknownExits{};
        std::uint32_t RcxMask{};
        std::uint32_t RdxMask{};
        std::uint32_t RcxWriteOffset{UINT32_MAX};
        std::uint32_t RdxWriteOffset{UINT32_MAX};
    };

    struct IncomingCfgRegisterState final
    {
        std::uint32_t RcxMask{16u};
        std::uint32_t RdxMask{16u};
        std::uint32_t RcxWriteOffset{UINT32_MAX};
        std::uint32_t RdxWriteOffset{UINT32_MAX};
    };

    [[nodiscard]] IncomingCfgArgumentTrace TraceIncomingCallerArgumentsGraph(
        const std::byte* code, std::size_t functionSize,
        std::size_t callOffset)
    {
        IncomingCfgArgumentTrace result{};
        if (code == nullptr || callOffset >= functionSize ||
            callOffset >= 32768u)
        {
            return result;
        }
        functionSize = std::min<std::size_t>(functionSize, 32768u);
        std::vector<IncomingCfgRegisterState> entries(functionSize);
        std::vector<std::uint8_t> seen(functionSize);
        std::vector<std::uint32_t> pending;
        const auto merge = [](IncomingCfgRegisterState& destination,
                              const IncomingCfgRegisterState& source)
        {
            const auto old = destination;
            destination.RcxMask |= source.RcxMask;
            destination.RdxMask |= source.RdxMask;
            if (destination.RcxWriteOffset != source.RcxWriteOffset)
                destination.RcxWriteOffset = UINT32_MAX;
            if (destination.RdxWriteOffset != source.RdxWriteOffset)
                destination.RdxWriteOffset = UINT32_MAX;
            return destination.RcxMask != old.RcxMask ||
                destination.RdxMask != old.RdxMask ||
                destination.RcxWriteOffset != old.RcxWriteOffset ||
                destination.RdxWriteOffset != old.RdxWriteOffset;
        };
        const auto enqueue = [&](std::size_t offset,
                                 const IncomingCfgRegisterState& state)
        {
            if (offset >= functionSize)
            {
                ++result.UnknownExits;
                return;
            }
            if (seen[offset] == 0u)
            {
                seen[offset] = 1u;
                entries[offset] = state;
                pending.push_back(static_cast<std::uint32_t>(offset));
            }
            else if (merge(entries[offset], state))
            {
                pending.push_back(static_cast<std::uint32_t>(offset));
            }
        };
        enqueue(0u, IncomingCfgRegisterState{});
        while (!pending.empty() && result.Visits < 32768u)
        {
            const auto offset = pending.back();
            pending.pop_back();
            auto state = entries[offset];
            ++result.Visits;
            const auto available = std::min<std::size_t>(
                15u, functionSize - offset);
            std::array<std::uint8_t, 15> bytes{};
            std::memcpy(bytes.data(), code + offset, available);
            hde64s instruction{};
            const auto length = hde64_disasm(bytes.data(), &instruction);
            if (length == 0u || length > available ||
                (instruction.flags & F_ERROR) != 0u)
            {
                ++result.UnknownExits;
                continue;
            }
            const auto op = instruction.opcode;
            if (offset == callOffset && op == 0xE8u && length == 5u)
            {
                if (result.Boundary == 0u)
                {
                    result.Boundary = 1u;
                    result.RcxMask = state.RcxMask;
                    result.RdxMask = state.RdxMask;
                    result.RcxWriteOffset = state.RcxWriteOffset;
                    result.RdxWriteOffset = state.RdxWriteOffset;
                }
                else
                {
                    IncomingCfgRegisterState accumulated{
                        result.RcxMask, result.RdxMask,
                        result.RcxWriteOffset, result.RdxWriteOffset};
                    merge(accumulated, state);
                    result.RcxMask = accumulated.RcxMask;
                    result.RdxMask = accumulated.RdxMask;
                    result.RcxWriteOffset = accumulated.RcxWriteOffset;
                    result.RdxWriteOffset = accumulated.RdxWriteOffset;
                }
                continue;
            }
            if (op == 0xC3u || op == 0xC2u || op == 0xCBu ||
                op == 0xCAu || op == 0xCCu ||
                (op == 0x0Fu && instruction.opcode2 == 0x0Bu))
            {
                continue;
            }
            const bool modrm = (instruction.flags & F_MODRM) != 0u;
            const auto reg = static_cast<std::uint32_t>(
                instruction.modrm_reg + (instruction.rex_r != 0u ? 8u : 0u));
            const auto rm = static_cast<std::uint32_t>(
                instruction.modrm_rm + (instruction.rex_b != 0u ? 8u : 0u));
            const bool registerRm = modrm && instruction.modrm_mod == 3u;
            const auto write = [&](std::uint32_t destination,
                                   std::uint32_t mask)
            {
                if (destination == 1u)
                {
                    state.RcxMask = mask;
                    state.RcxWriteOffset = static_cast<std::uint32_t>(offset);
                }
                else if (destination == 2u)
                {
                    state.RdxMask = mask;
                    state.RdxWriteOffset = static_cast<std::uint32_t>(offset);
                }
            };
            if (op == 0x8Bu && modrm)
            {
                write(reg, instruction.rex_w != 0u ?
                    (registerRm ? 2u : 4u) : 16u);
            }
            else if (op == 0x8Du && modrm)
            {
                write(reg, instruction.rex_w != 0u ? 4u : 16u);
            }
            else if (op == 0x89u && registerRm)
            {
                write(rm, instruction.rex_w != 0u ? 2u : 16u);
            }
            else if ((op == 0x31u || op == 0x29u) && registerRm)
            {
                write(rm, reg == rm ? 1u : 16u);
            }
            else if ((op == 0x33u || op == 0x2Bu) && registerRm)
            {
                write(reg, reg == rm ? 1u : 16u);
            }
            else if (op >= 0xB8u && op <= 0xBFu)
            {
                const auto destination = static_cast<std::uint32_t>(
                    op - 0xB8u + (instruction.rex_b != 0u ? 8u : 0u));
                const bool zero = (instruction.flags & F_IMM64) != 0u
                    ? instruction.imm.imm64 == 0u
                    : (instruction.flags & F_IMM32) != 0u &&
                      instruction.imm.imm32 == 0u;
                write(destination, zero ? 1u : 8u);
            }
            else if (op == 0xC7u && registerRm &&
                     instruction.modrm_reg == 0u)
            {
                write(rm, instruction.imm.imm32 == 0u ? 1u : 8u);
            }
            else if (op >= 0x58u && op <= 0x5Fu)
            {
                write(static_cast<std::uint32_t>(
                    op - 0x58u + (instruction.rex_b != 0u ? 8u : 0u)), 16u);
            }
            else if ((op == 0x81u || op == 0x83u || op == 0x01u ||
                      op == 0x09u || op == 0x21u || op == 0x29u ||
                      op == 0x31u || op == 0xC1u || op == 0xD1u ||
                      op == 0x87u) && registerRm)
            {
                if (!((op == 0x81u || op == 0x83u) &&
                      instruction.modrm_reg == 7u))
                    write(rm, 16u);
                if (op == 0x87u) write(reg, 16u);
            }
            else if ((op == 0x03u || op == 0x0Bu || op == 0x23u ||
                      op == 0x2Bu || op == 0x33u) && modrm)
            {
                write(reg, 16u);
            }
            const bool conditional =
                (op >= 0x70u && op <= 0x7Fu) ||
                (op >= 0xE0u && op <= 0xE3u) ||
                (op == 0x0Fu && instruction.opcode2 >= 0x80u &&
                 instruction.opcode2 <= 0x8Fu);
            const bool directJump = op == 0xE9u || op == 0xEBu;
            if (conditional || directJump)
            {
                const bool shortDisplacement =
                    (op >= 0x70u && op <= 0x7Fu) ||
                    (op >= 0xE0u && op <= 0xE3u) || op == 0xEBu;
                const auto displacement = shortDisplacement
                    ? static_cast<std::int64_t>(
                        static_cast<std::int8_t>(instruction.imm.imm8))
                    : static_cast<std::int64_t>(
                        static_cast<std::int32_t>(instruction.imm.imm32));
                const auto target = static_cast<std::int64_t>(offset) +
                    static_cast<std::int64_t>(length) + displacement;
                ++result.Edges;
                if (target >= 0 &&
                    static_cast<std::uint64_t>(target) < functionSize)
                    enqueue(static_cast<std::size_t>(target), state);
                else
                    ++result.UnknownExits;
                if (directJump) continue;
            }
            if (op == 0xFFu && modrm &&
                (instruction.modrm_reg == 4u ||
                 instruction.modrm_reg == 5u))
            {
                ++result.UnknownExits;
                continue;
            }
            if (op == 0xEAu)
            {
                ++result.UnknownExits;
                continue;
            }
            if (op == 0xE8u ||
                (op == 0xFFu && modrm &&
                 (instruction.modrm_reg == 2u ||
                  instruction.modrm_reg == 3u)))
            {
                state.RcxMask = state.RdxMask = 16u;
                state.RcxWriteOffset = state.RdxWriteOffset =
                    static_cast<std::uint32_t>(offset);
            }
            enqueue(offset + length, state);
        }
        if (!pending.empty()) ++result.UnknownExits;
        return result;
    }

    void RecordEnhancedInboundCallEvidence(
        ImageView image,
        const EnhancedReceiverTriplet& selected,
        CEventGenerator::EventLocatorDiagnostics& diagnostics)
    {
        if (selected.UniqueGroups != 1u || image.Base == nullptr)
        {
            return;
        }
        const std::uintptr_t base =
            reinterpret_cast<std::uintptr_t>(image.Base);
        const std::uintptr_t limit = base + image.Size;
        if (limit < base)
        {
            return;
        }
        std::array<std::uint32_t, 3> entries{};
        for (std::size_t i = 0; i < entries.size(); ++i)
        {
            if (!Contains(image, selected.Entries[i], 1))
            {
                return;
            }
            entries[i] = static_cast<std::uint32_t>(
                selected.Entries[i] - image.Base);
        }

        for (std::uintptr_t address = base; address < limit;)
        {
            MEMORY_BASIC_INFORMATION info{};
            if (VirtualQuery(reinterpret_cast<const void*>(address),
                    &info, sizeof(info)) != sizeof(info))
            {
                break;
            }
            const std::uintptr_t regionBase =
                reinterpret_cast<std::uintptr_t>(info.BaseAddress);
            const std::uintptr_t regionLimit = regionBase + info.RegionSize;
            if (regionLimit <= address || regionLimit < regionBase)
            {
                break;
            }
            const std::uintptr_t end = std::min(regionLimit, limit);
            const DWORD protection = info.Protect & 0xFFu;
            const bool executable = info.State == MEM_COMMIT &&
                (info.Protect & PAGE_GUARD) == 0u &&
                (protection == PAGE_EXECUTE ||
                 protection == PAGE_EXECUTE_READ ||
                 protection == PAGE_EXECUTE_READWRITE ||
                 protection == PAGE_EXECUTE_WRITECOPY);
            if (executable && end - address >= 5u)
            {
                for (std::uintptr_t current = address;
                     current <= end - 5u; ++current)
                {
                    const auto* instruction =
                        reinterpret_cast<const std::byte*>(current);
                    if (ByteAt(instruction) != 0xE8u)
                    {
                        continue;
                    }
                    const std::int64_t target =
                        static_cast<std::int64_t>(current - base) + 5 +
                        ReadInt32(instruction + 1);
                    if (target < 0 ||
                        static_cast<std::uint64_t>(target) >= image.Size)
                    {
                        continue;
                    }
                    for (std::uint32_t role = 0; role < 3u; ++role)
                    {
                        if (static_cast<std::uint32_t>(target) != entries[role])
                        {
                            continue;
                        }
                        const auto caller = ResolveRuntimeFunction(
                            image, instruction);
                        if (!caller ||
                            static_cast<std::uint64_t>(current - base) + 5u >
                                caller.EndRva)
                        {
                            break;
                        }
                        ++diagnostics.EnhancedIncomingCallCounts[role];
                        IncomingCallTrace trace{};
                        IncomingCfgTrace graph{};
                        IncomingCfgArgumentTrace arguments{};
                        const auto* callerStart = image.Base + caller.BeginRva;
                        MEMORY_BASIC_INFORMATION callerPage{};
                        if (VirtualQuery(callerStart, &callerPage,
                                sizeof(callerPage)) == sizeof(callerPage))
                        {
                            const auto pageBase = reinterpret_cast<std::uintptr_t>(
                                callerPage.BaseAddress);
                            const auto pageLimit = pageBase + callerPage.RegionSize;
                            const DWORD codeProtection = callerPage.Protect & 0xFFu;
                            const bool callerExecutable = callerPage.State == MEM_COMMIT &&
                                (callerPage.Protect & PAGE_GUARD) == 0u &&
                                (codeProtection == PAGE_EXECUTE ||
                                 codeProtection == PAGE_EXECUTE_READ ||
                                 codeProtection == PAGE_EXECUTE_READWRITE ||
                                 codeProtection == PAGE_EXECUTE_WRITECOPY);
                            if (callerExecutable && pageLimit >= pageBase &&
                                pageLimit >= current + 5u)
                            {
                                const auto callOffset = current -
                                    reinterpret_cast<std::uintptr_t>(callerStart);
                                trace = TraceIncomingCallerCode(callerStart,
                                    callOffset);
                                const auto graphLimit = std::min<std::size_t>(
                                    caller.Size(), pageLimit -
                                        reinterpret_cast<std::uintptr_t>(callerStart));
                                graph = TraceIncomingCallerGraph(callerStart,
                                    graphLimit, callOffset);
                                if (graph.Boundary != 0u)
                                    arguments = TraceIncomingCallerArgumentsGraph(
                                        callerStart, graphLimit, callOffset);
                            }
                        }
                        if (graph.Boundary == 1u)
                        {
                            ++diagnostics.EnhancedIncomingCfgCounts[role];
                        }
                        else
                        {
                            ++diagnostics.EnhancedIncomingCfgUnresolvedCounts[role];
                        }
                        if (trace.Boundary == 1u)
                        {
                            ++diagnostics.EnhancedIncomingDecodedCounts[role];
                            if (trace.RdxState == 1u)
                            {
                                ++diagnostics.EnhancedIncomingRdxZeroCounts[role];
                            }
                            else if (trace.RdxState == 2u ||
                                     trace.RdxState == 3u)
                            {
                                ++diagnostics.EnhancedIncomingRdxDataCounts[role];
                            }
                        }
                        else
                        {
                            ++diagnostics.EnhancedIncomingUnresolvedCounts[role];
                        }
                        std::uint32_t shown = 0;
                        for (std::uint32_t i = 0;
                             i < diagnostics.EnhancedIncomingCallSampleCount;
                             ++i)
                        {
                            if (diagnostics.EnhancedIncomingCallSamples[i].Role ==
                                role)
                            {
                                ++shown;
                            }
                        }
                        if (shown >= 6u ||
                            diagnostics.EnhancedIncomingCallSampleCount >=
                                diagnostics.EnhancedIncomingCallSamples.size())
                        {
                            break;
                        }
                        auto& sample = diagnostics.EnhancedIncomingCallSamples[
                            diagnostics.EnhancedIncomingCallSampleCount++];
                        sample.Role = role;
                        sample.CallerRva = caller.BeginRva;
                        sample.CallsiteRva = static_cast<std::uint32_t>(
                            current - base);
                        sample.TargetRva = entries[role];
                        sample.DecodedBoundary = trace.Boundary;
                        sample.CfgBoundary = graph.Boundary;
                        sample.CfgInstructions = graph.Instructions;
                        sample.CfgEdges = graph.Edges;
                        sample.CfgUnknownExits = graph.UnknownExits;
                        sample.CfgArgumentBoundary = arguments.Boundary;
                        sample.CfgArgumentVisits = arguments.Visits;
                        sample.CfgArgumentEdges = arguments.Edges;
                        sample.CfgArgumentUnknownExits = arguments.UnknownExits;
                        sample.CfgRcxMask = arguments.RcxMask;
                        sample.CfgRdxMask = arguments.RdxMask;
                        sample.CfgRcxWriteRva = arguments.Boundary != 0u &&
                            arguments.RcxWriteOffset != UINT32_MAX
                            ? caller.BeginRva + arguments.RcxWriteOffset : 0u;
                        sample.CfgRdxWriteRva = arguments.Boundary != 0u &&
                            arguments.RdxWriteOffset != UINT32_MAX
                            ? caller.BeginRva + arguments.RdxWriteOffset : 0u;
                        if (arguments.Boundary != 0u)
                        {
                            ++diagnostics.EnhancedIncomingCfgArgumentCounts[role];
                            if (arguments.RdxMask == 2u ||
                                arguments.RdxMask == 4u)
                                ++diagnostics.EnhancedIncomingCfgRdxDataCounts[role];
                            if (arguments.RdxMask == 1u)
                                ++diagnostics.EnhancedIncomingCfgRdxZeroCounts[role];
                        }
                        sample.LinearBranches = trace.Branches;
                        sample.RcxState = trace.Boundary ? trace.RcxState : 0u;
                        sample.RdxState = trace.Boundary ? trace.RdxState : 0u;
                        sample.RcxWriteRva = trace.Boundary &&
                            trace.RcxWriteOffset != UINT32_MAX
                            ? caller.BeginRva + trace.RcxWriteOffset : 0u;
                        sample.RdxWriteRva = trace.Boundary &&
                            trace.RdxWriteOffset != UINT32_MAX
                            ? caller.BeginRva + trace.RdxWriteOffset : 0u;
                        const std::uintptr_t windowBegin = std::max(
                            {address, base + caller.BeginRva,
                             current > base + 24u ? current - 24u : base});
                        sample.ContextRva = static_cast<std::uint32_t>(
                            windowBegin - base);
                        sample.ContextLength = static_cast<std::uint32_t>(
                            std::min<std::uintptr_t>(
                                sample.Context.size(), current + 5u - windowBegin));
                        std::memcpy(sample.Context.data(),
                            reinterpret_cast<const void*>(windowBegin),
                            sample.ContextLength);
                        break;
                    }
                }
            }
            address = end;
        }
    }

    void RecordEnhancedReceiverEvidence(
        ImageView image,
        const std::vector<EnhancedReceiverCandidate>& ped,
        const std::vector<EnhancedReceiverCandidate>& global,
        bool pointerFallback,
        CEventGenerator::EventLocatorDiagnostics& diagnostics)
    {
        using namespace CEventGenerator;

        struct CalleeGroup final
        {
            const std::byte* Target{};
            RuntimeFunctionView Function{};
            std::uint32_t PedCount{};
            std::uint32_t GlobalCount{};
        };
        struct ReceiverEvidence final
        {
            const EnhancedReceiverCandidate* Candidate{};
            std::size_t GroupIndex{};
            std::uint32_t Kind{};
        };

        diagnostics.ProducerCandidateCount =
            static_cast<std::uint32_t>(ped.size() + global.size());
        std::vector<CalleeGroup> groups;
        std::vector<ReceiverEvidence> receivers;
        groups.reserve(ped.size() + global.size());
        receivers.reserve(ped.size() + global.size());
        const auto gather = [&](const auto& candidates, std::uint32_t kind)
        {
            for (const auto& candidate : candidates)
            {
                const RuntimeFunctionView function =
                    ResolveRuntimeFunction(image, candidate.Callee);
                if (!function)
                {
                    continue;
                }
                auto found = std::find_if(groups.begin(), groups.end(),
                    [&](const CalleeGroup& group)
                    {
                        return group.Target == candidate.Callee;
                    });
                if (found == groups.end())
                {
                    groups.push_back({candidate.Callee, function});
                    found = groups.end() - 1;
                }
                const std::size_t index = static_cast<std::size_t>(
                    found - groups.begin());
                if (kind == 1u)
                {
                    ++found->PedCount;
                }
                else
                {
                    ++found->GlobalCount;
                }
                receivers.push_back({&candidate, index, kind});
            }
        };
        gather(ped, 1u);
        gather(global, 2u);

        diagnostics.DispatchCalleeCandidateCount =
            static_cast<std::uint32_t>(groups.size());
        diagnostics.EnhancedMixedCalleeCount =
            static_cast<std::uint32_t>(std::count_if(groups.begin(), groups.end(),
                [](const CalleeGroup& group)
                {
                    return group.PedCount > 0 && group.GlobalCount > 0;
                }));

        std::vector<std::size_t> order;
        order.reserve(groups.size());
        for (std::size_t index = 0; index < groups.size(); ++index)
        {
            order.push_back(index);
        }
        std::sort(order.begin(), order.end(),
            [&](std::size_t leftIndex, std::size_t rightIndex)
            {
                const auto& left = groups[leftIndex];
                const auto& right = groups[rightIndex];
                const bool leftMixed = left.PedCount && left.GlobalCount;
                const bool rightMixed = right.PedCount && right.GlobalCount;
                if (leftMixed != rightMixed)
                {
                    return leftMixed;
                }
                const auto leftCount = left.PedCount + left.GlobalCount;
                const auto rightCount = right.PedCount + right.GlobalCount;
                if (leftCount != rightCount)
                {
                    return leftCount > rightCount;
                }
                return left.Function.BeginRva < right.Function.BeginRva;
            });

        for (const auto index : order)
        {
            if (diagnostics.DispatchCalleeDiagnosticCount >=
                diagnostics.DispatchCallees.size())
            {
                break;
            }
            const auto& group = groups[index];
            auto& output = diagnostics.DispatchCallees[
                diagnostics.DispatchCalleeDiagnosticCount++];
            output.Rva = group.Function.BeginRva;
            output.Size = group.Function.Size();
            output.Relations = (group.PedCount ? 1u : 0u) |
                (group.GlobalCount ? 2u : 0u) |
                (pointerFallback ? 4u : 0u);
            output.CallsiteCount = group.PedCount + group.GlobalCount;
            output.WalkerCount = group.PedCount;
        }

        const auto recordReceiver = [&](const ReceiverEvidence& receiver)
        {
            if (diagnostics.ProducerDiagnosticCount >=
                diagnostics.Producers.size())
            {
                return;
            }
            const RuntimeFunctionView function =
                ResolveRuntimeFunction(image, receiver.Candidate->Entry);
            if (!function)
            {
                return;
            }
            auto& output = diagnostics.Producers[
                diagnostics.ProducerDiagnosticCount++];
            output.Rva = function.BeginRva;
            output.Size = function.Size();
            output.Relations = receiver.Kind |
                (pointerFallback ? 4u : 0u) |
                (groups[receiver.GroupIndex].PedCount &&
                 groups[receiver.GroupIndex].GlobalCount ? 8u : 0u);
        };
        for (const auto index : order)
        {
            if (diagnostics.ProducerDiagnosticCount >=
                diagnostics.Producers.size())
            {
                break;
            }
            for (std::uint32_t kind : {1u, 2u})
            {
                const auto found = std::find_if(receivers.begin(), receivers.end(),
                    [&](const ReceiverEvidence& receiver)
                    {
                        return receiver.GroupIndex == index &&
                            receiver.Kind == kind;
                    });
                if (found != receivers.end())
                {
                    recordReceiver(*found);
                }
            }
        }
    }

    void RecordEnhancedSelectedPathEvidence(
        ImageView image,
        const EnhancedReceiverTriplet& selected,
        const std::vector<EnhancedReceiverCandidate>& ped,
        const std::vector<EnhancedReceiverCandidate>& global,
        CEventGenerator::EventLocatorDiagnostics& diagnostics)
    {
        if (selected.UniqueGroups != 1 || selected.Callee == nullptr)
        {
            return;
        }
        for (std::size_t index = 0; index < selected.Entries.size(); ++index)
        {
            const auto& source = index == 2 ? global : ped;
            const auto match = std::find_if(source.begin(), source.end(),
                [&](const EnhancedReceiverCandidate& item)
                {
                    return item.Entry == selected.Entries[index] &&
                        item.Callee == selected.Callee;
                });
            if (match == source.end() || match->Callsite == nullptr ||
                diagnostics.EnhancedSelectedPathDiagnosticCount >=
                    diagnostics.EnhancedSelectedPaths.size())
            {
                continue;
            }
            const RuntimeFunctionView function =
                ResolveRuntimeFunction(image, match->Callsite);
            if (!function)
            {
                continue;
            }
            auto& output = diagnostics.EnhancedSelectedPaths[
                diagnostics.EnhancedSelectedPathDiagnosticCount++];
            output.EntryRva = static_cast<std::uint32_t>(match->Entry - image.Base);
            output.ComparisonRva = static_cast<std::uint32_t>(
                match->Comparison - image.Base);
            output.TransferRva = static_cast<std::uint32_t>(
                match->Transfer - image.Base);
            output.CallsiteRva = static_cast<std::uint32_t>(
                match->Callsite - image.Base);
            output.CalleeRva = static_cast<std::uint32_t>(
                match->Callee - image.Base);
            output.GroupRegister = match->GroupRegister;
            output.Flags = (index == 2 ? 2u : 1u) |
                (match->Indirect ? 4u : 0u) |
                (match->WideImmediate ? 8u : 0u);
            if (output.GroupRegister == 6u &&
                output.CallsiteRva >= 5u &&
                Contains(image, match->Callsite - 5, 10) &&
                ByteAt(match->Callsite - 5) == 0x48u &&
                ByteAt(match->Callsite - 4) == 0x89u &&
                ByteAt(match->Callsite - 3) == 0xF1u &&
                ByteAt(match->Callsite - 2) == 0x31u &&
                ByteAt(match->Callsite - 1) == 0xD2u &&
                ByteAt(match->Callsite) == 0xE8u)
            {
                output.Flags |= 0x20u;
            }
            const std::uint32_t begin =
                std::max(function.BeginRva,
                    output.CallsiteRva > 24u ? output.CallsiteRva - 24u : 0u);
            const std::uint32_t finish = std::min(function.EndRva,
                static_cast<std::uint32_t>(std::min<std::uint64_t>(
                    image.Size, std::uint64_t(output.CallsiteRva) + 40u)));
            output.ContextBeginRva = begin;
            output.ContextLength = std::min<std::uint32_t>(
                finish > begin ? finish - begin : 0u,
                static_cast<std::uint32_t>(output.Context.size()));
            if (output.ContextLength != 0)
            {
                std::memcpy(output.Context.data(), image.Base + begin,
                    output.ContextLength);
            }
        }
        if (diagnostics.EnhancedSelectedPathDiagnosticCount <
            diagnostics.EnhancedSelectedPaths.size())
        {
            const RuntimeFunctionView function =
                ResolveRuntimeFunction(image, selected.Callee);
            if (function)
            {
                auto& output = diagnostics.EnhancedSelectedPaths[
                    diagnostics.EnhancedSelectedPathDiagnosticCount++];
                output.EntryRva = function.BeginRva;
                output.CalleeRva = static_cast<std::uint32_t>(
                    selected.Callee - image.Base);
                output.CallsiteRva = output.CalleeRva;
                output.Flags = 16u;
                output.ContextBeginRva = function.BeginRva;
                output.ContextLength = std::min<std::uint32_t>(
                    function.Size(), static_cast<std::uint32_t>(
                        output.Context.size()));
                std::memcpy(output.Context.data(), image.Base + function.BeginRva,
                    output.ContextLength);
            }
        }
    }

    struct DecodedInstructionTrace final
    {
        std::uint32_t Rva{};
        hde64s Instruction{};
    };

    [[nodiscard]] std::uint32_t GeneralRegister(std::uint32_t low,
        bool extended) noexcept
    {
        return low | (extended ? 8u : 0u);
    }

    [[nodiscard]] std::uint32_t ByteRegister(std::uint32_t low,
        bool extended, bool rexPresent) noexcept
    {
        return !rexPresent && low >= 4u ? low - 4u :
            GeneralRegister(low, extended);
    }

    [[nodiscard]] bool DecodeRegisterMove(const hde64s& instruction,
        std::uint32_t& destination,
        std::uint32_t& source) noexcept
    {
        if (instruction.rex_w == 0 ||
            (instruction.flags & F_MODRM) == 0 ||
            instruction.modrm_mod != 3u)
        {
            return false;
        }
        if (instruction.opcode == 0x89u)
        {
            destination = GeneralRegister(instruction.modrm_rm,
                instruction.rex_b != 0);
            source = GeneralRegister(instruction.modrm_reg,
                instruction.rex_r != 0);
            return true;
        }
        if (instruction.opcode == 0x8Bu)
        {
            destination = GeneralRegister(instruction.modrm_reg,
                instruction.rex_r != 0);
            source = GeneralRegister(instruction.modrm_rm,
                instruction.rex_b != 0);
            return true;
        }
        return false;
    }

    [[nodiscard]] bool IsIndependentArgumentSetup(
        const hde64s& instruction,
        std::uint32_t protectedRegisterA,
        std::uint32_t protectedRegisterB,
        std::uint32_t protectedRegisterC) noexcept
    {
        if ((instruction.flags & F_MODRM) == 0)
        {
            return false;
        }
        std::uint32_t destination{};
        if (instruction.opcode == 0x8Du && instruction.rex_w != 0 &&
            instruction.modrm_mod == 0u && instruction.modrm_rm == 5u)
        {
            destination = GeneralRegister(instruction.modrm_reg,
                instruction.rex_r != 0);
        }
        else if (instruction.opcode == 0x31u &&
            instruction.modrm_mod == 3u)
        {
            destination = GeneralRegister(instruction.modrm_rm,
                instruction.rex_b != 0);
            const std::uint32_t source = GeneralRegister(
                instruction.modrm_reg, instruction.rex_r != 0);
            if (destination != source)
            {
                return false;
            }
        }
        else
        {
            return false;
        }
        return destination != protectedRegisterA &&
            destination != protectedRegisterB &&
            destination != protectedRegisterC;
    }

    void RecordStraightLineVtableChain(
        const std::array<DecodedInstructionTrace, 8>& recent,
        std::uint32_t recentCount,
        std::uint32_t callsite,
        CEventGenerator::EnhancedVirtualCallEvidence& found) noexcept
    {
        if (recentCount == 0u)
        {
            return;
        }
        std::uint32_t expectedEnd = callsite;
        for (std::uint32_t cursor = recentCount; cursor != 0u; --cursor)
        {
            const auto& item =
                recent[static_cast<std::size_t>(cursor) - std::size_t{1}];
            const auto& instruction = item.Instruction;
            if (item.Rva + instruction.len != expectedEnd)
            {
                return;
            }
            expectedEnd = item.Rva;
            if (instruction.opcode == 0x8Bu && instruction.rex_w != 0 &&
                (instruction.flags & F_MODRM) != 0 &&
                instruction.modrm_mod == 0u &&
                instruction.modrm_rm != 4u &&
                instruction.modrm_rm != 5u &&
                GeneralRegister(instruction.modrm_reg,
                    instruction.rex_r != 0) == found.BaseRegister)
            {
                const std::uint32_t sourceObject = GeneralRegister(
                    instruction.modrm_rm, instruction.rex_b != 0);
                std::uint32_t preservedObject = sourceObject;
                std::uint32_t aliasCopyRva{};
                if (sourceObject == found.BaseRegister)
                {
                    if (cursor < 2u)
                    {
                        return;
                    }
                    const auto& previous = recent[
                        static_cast<std::size_t>(cursor) - std::size_t{2}];
                    std::uint32_t destination{}, source{};
                    if (previous.Rva + previous.Instruction.len != item.Rva ||
                        !DecodeRegisterMove(previous.Instruction,
                            destination, source) ||
                        source != sourceObject || destination == sourceObject)
                    {
                        return;
                    }
                    preservedObject = destination;
                    aliasCopyRva = previous.Rva;
                }

                bool thisReady = preservedObject == 1u;
                std::uint32_t thisMove{};
                bool transparent = true;
                for (std::uint32_t step = cursor;
                     step < recentCount; ++step)
                {
                    const auto& next = recent[step].Instruction;
                    std::uint32_t destination{}, source{};
                    if (DecodeRegisterMove(next, destination, source))
                    {
                        if (destination == found.BaseRegister ||
                            destination == preservedObject)
                        {
                            if (destination != source)
                            {
                                transparent = false;
                                break;
                            }
                        }
                        if (destination == 1u)
                        {
                            thisReady = source == preservedObject;
                            thisMove = thisReady ? recent[step].Rva : 0u;
                        }
                        continue;
                    }
                    if (IsIndependentArgumentSetup(next,
                            found.BaseRegister, preservedObject, 1u))
                    {
                        continue;
                    }
                    transparent = false;
                    break;
                }
                found.ObjectRegister = preservedObject;
                found.VtableLoadRva = item.Rva;
                found.ThisMoveRva = thisMove;
                found.AliasMoveRva = aliasCopyRva;
                found.ProvenanceFlags |= 1u;
                if (thisReady)
                {
                    found.ProvenanceFlags |= 2u;
                }
                if (transparent && thisReady)
                {
                    found.ProvenanceFlags |= 4u;
                }
                if (aliasCopyRva != 0u)
                {
                    found.ProvenanceFlags |= 8u;
                }
                if (preservedObject == 1u)
                {
                    found.ProvenanceFlags |= 16u;
                }
                return;
            }
            std::uint32_t destination{}, source{};
            if (!DecodeRegisterMove(instruction, destination, source) &&
                !IsIndependentArgumentSetup(instruction,
                    found.BaseRegister, 1u, 1u))
            {
                return;
            }
        }
    }

    void RecordEnhancedVirtualCallEvidence(
        ImageView image,
        const EnhancedReceiverTriplet& selected,
        const CEventGenerator::EventLocatorDiagnostics& anchors,
        CEventGenerator::EventLocatorDiagnostics& output)
    {
        using namespace CEventGenerator;
        struct Owner final { std::uint32_t Rva; std::uint32_t Kind; };
        std::vector<Owner> owners;
        const auto addOwner = [&](std::uint32_t rva, std::uint32_t kind)
        {
            if (rva == 0 || rva >= image.Size ||
                std::any_of(owners.begin(), owners.end(),
                    [rva](const Owner& owner) { return owner.Rva == rva; }))
            {
                return;
            }
            owners.push_back({rva, kind});
        };
        addOwner(anchors.CEventCountFunctionRva, 5u);
        addOwner(anchors.EventTypeFunctionRva, 6u);
        for (std::uint32_t index = 0;
             index < anchors.DispatchCallsiteDiagnosticCount &&
             index < anchors.DispatchCallsites.size(); ++index)
        {
            const auto& caller = anchors.DispatchCallsites[index];
            if (caller.Relations == 2u)
            {
                addOwner(caller.WalkerRva, 7u);
            }
        }
        if (selected.UniqueGroups == 1 && selected.Callee != nullptr)
        {
            addOwner(static_cast<std::uint32_t>(
                selected.Callee - image.Base), 4u);
            for (std::size_t index = 0; index < selected.Entries.size(); ++index)
            {
                addOwner(static_cast<std::uint32_t>(
                    selected.Entries[index] - image.Base),
                    static_cast<std::uint32_t>(index + 1));
            }
        }

        for (const Owner& owner : owners)
        {
            const RuntimeFunctionView function = ResolveRuntimeFunction(
                image, image.Base + owner.Rva);
            if (!function || function.Size() > 0x20000u)
            {
                continue;
            }
            struct RecentLoad final
            {
                std::uint32_t Rva{};
                std::uint32_t FromRegister{0xFFFFFFFFu};
                std::int32_t Displacement{};
            };
            std::array<RecentLoad, 16> recentLoads{};
            std::array<DecodedInstructionTrace, 8> recentInstructions{};
            std::uint32_t recentCount{};
            std::uint32_t cursor = function.BeginRva;
            while (cursor < function.EndRva)
            {
                std::array<std::uint8_t, 15> bytes{};
                const std::uint32_t available = std::min<std::uint32_t>(
                    15u, function.EndRva - cursor);
                std::memcpy(bytes.data(), image.Base + cursor, available);
                hde64s decoded{};
                const unsigned int length = hde64_disasm(
                    bytes.data(), &decoded);
                if (length == 0u || length > available ||
                    (decoded.flags & F_ERROR) != 0)
                {
                    ++output.EnhancedVirtualCallDecoderStops;
                    if (output.EnhancedDecoderStopRva == 0u)
                    {
                        output.EnhancedDecoderStopOwnerRva = function.BeginRva;
                        output.EnhancedDecoderStopRva = cursor;
                        output.EnhancedDecoderStopReason =
                            length > available ? 2u : 1u;
                        output.EnhancedDecoderStopByteCount = available;
                        std::memcpy(output.EnhancedDecoderStopBytes.data(),
                            bytes.data(), available);
                    }
                    break;
                }
                if (decoded.opcode == 0x8Bu && decoded.rex_w != 0 &&
                    (decoded.flags & F_MODRM) != 0 &&
                    decoded.modrm_mod != 3u && decoded.modrm_rm != 4u &&
                    !(decoded.modrm_mod == 0u && decoded.modrm_rm == 5u))
                {
                    const std::uint32_t destination =
                        decoded.modrm_reg | (decoded.rex_r ? 8u : 0u);
                    auto& load = recentLoads[destination];
                    load.Rva = cursor;
                    load.FromRegister = decoded.modrm_rm |
                        (decoded.rex_b ? 8u : 0u);
                    load.Displacement = 0;
                    if (decoded.modrm_mod == 1u &&
                        (decoded.flags & F_DISP8) != 0)
                    {
                        load.Displacement = static_cast<std::int8_t>(
                            decoded.disp.disp8);
                    }
                    else if (decoded.modrm_mod == 2u &&
                        (decoded.flags & F_DISP32) != 0)
                    {
                        load.Displacement = static_cast<std::int32_t>(
                            decoded.disp.disp32);
                    }
                }
                if (decoded.opcode == 0xFFu &&
                    (decoded.flags & F_MODRM) != 0 &&
                    decoded.modrm_reg == 2u &&
                    (decoded.modrm_mod == 1u || decoded.modrm_mod == 2u) &&
                    decoded.modrm_rm != 4u)
                {
                    std::int64_t slot = -1;
                    if (decoded.modrm_mod == 1u &&
                        (decoded.flags & F_DISP8) != 0)
                    {
                        slot = static_cast<std::int8_t>(decoded.disp.disp8);
                    }
                    else if (decoded.modrm_mod == 2u &&
                        (decoded.flags & F_DISP32) != 0)
                    {
                        slot = static_cast<std::int32_t>(decoded.disp.disp32);
                    }
                    if (slot >= 0 && slot <= 0x800 &&
                        slot % sizeof(void*) == 0)
                    {
                        ++output.EnhancedVirtualCallCandidateCount;
                        if (output.EnhancedVirtualCallDiagnosticCount <
                            output.EnhancedVirtualCalls.size())
                        {
                            auto& found = output.EnhancedVirtualCalls[
                                output.EnhancedVirtualCallDiagnosticCount++];
                            found.OwnerRva = function.BeginRva;
                            found.CallsiteRva = cursor;
                            found.SlotOffset = static_cast<std::uint32_t>(slot);
                            found.BaseRegister = decoded.modrm_rm |
                                (decoded.rex_b ? 8u : 0u);
                            found.OwnerKind = owner.Kind;
                            RecordStraightLineVtableChain(recentInstructions,
                                recentCount, cursor, found);
                            const auto& previous = recentLoads[found.BaseRegister];
                            if (previous.Rva != 0u && cursor > previous.Rva &&
                                cursor - previous.Rva <= 96u)
                            {
                                found.NearbyLoadRva = previous.Rva;
                                found.NearbyLoadSourceRegister =
                                    previous.FromRegister;
                                found.NearbyLoadDisplacement =
                                    previous.Displacement;
                            }
                            found.ContextBeginRva = std::max<std::uint32_t>(
                                function.BeginRva,
                                cursor > 32u ? cursor - 32u : 0u);
                            found.ContextLength = std::min<std::uint32_t>(
                                static_cast<std::uint32_t>(found.Context.size()),
                                function.EndRva - found.ContextBeginRva);
                            std::memcpy(found.Context.data(),
                                image.Base + found.ContextBeginRva,
                                found.ContextLength);
                        }
                    }
                }
                const bool terminator = decoded.opcode == 0xE8u ||
                    decoded.opcode == 0xE9u || decoded.opcode == 0xEBu ||
                    decoded.opcode == 0xC2u || decoded.opcode == 0xC3u ||
                    (decoded.opcode >= 0x70u && decoded.opcode <= 0x7Fu) ||
                    (decoded.opcode == 0x0Fu &&
                        decoded.opcode2 >= 0x80u && decoded.opcode2 <= 0x8Fu) ||
                    (decoded.opcode == 0xFFu &&
                        (decoded.flags & F_MODRM) != 0 &&
                        (decoded.modrm_reg == 2u || decoded.modrm_reg == 4u));
                if (terminator)
                {
                    recentCount = 0;
                }
                else
                {
                    if (recentCount == recentInstructions.size())
                    {
                        std::move(recentInstructions.begin() + 1,
                            recentInstructions.end(), recentInstructions.begin());
                        --recentCount;
                    }
                    recentInstructions[recentCount++] = {cursor, decoded};
                }
                cursor += length;
            }
        }
    }

    [[nodiscard]] bool IsExecutableImageAddress(
        ImageView image, std::uint32_t rva) noexcept
    {
        if (image.Base == nullptr || rva >= image.Size)
        {
            return false;
        }
        MEMORY_BASIC_INFORMATION page{};
        const auto* address = image.Base + rva;
        if (VirtualQuery(address, &page, sizeof(page)) == 0 ||
            page.State != MEM_COMMIT ||
            (page.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0u)
        {
            return false;
        }
        const DWORD protection = page.Protect & 0xFFu;
        return protection == PAGE_EXECUTE ||
            protection == PAGE_EXECUTE_READ ||
            protection == PAGE_EXECUTE_READWRITE ||
            protection == PAGE_EXECUTE_WRITECOPY;
    }

    struct SectionAtRva final
    {
        std::uint32_t Start{};
        std::uint32_t End{};
        std::uint32_t Characteristics{};
        std::array<char, 9> Name{};
    };

    [[nodiscard]] SectionAtRva FindImageSection(ImageView image,
        std::uint32_t rva) noexcept
    {
        if (image.Base == nullptr ||
            image.Size < sizeof(IMAGE_DOS_HEADER) ||
            image.Size < sizeof(IMAGE_NT_HEADERS64))
        {
            return {};
        }
        const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(image.Base);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew < 0 ||
            static_cast<std::size_t>(dos->e_lfanew) >
                image.Size - sizeof(IMAGE_NT_HEADERS64))
        {
            return {};
        }
        const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(
            image.Base + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE ||
            nt->FileHeader.NumberOfSections > 96u)
        {
            return {};
        }
        const auto* sections = IMAGE_FIRST_SECTION(nt);
        const auto sectionAddress = reinterpret_cast<std::uintptr_t>(sections);
        const auto baseAddress = reinterpret_cast<std::uintptr_t>(image.Base);
        if (sectionAddress < baseAddress ||
            sectionAddress - baseAddress > image.Size ||
            nt->FileHeader.NumberOfSections >
                (image.Size - (sectionAddress - baseAddress)) /
                    sizeof(IMAGE_SECTION_HEADER))
        {
            return {};
        }
        for (unsigned int index = 0u; index < nt->FileHeader.NumberOfSections;
            ++index)
        {
            const auto& section = sections[index];
            const auto begin = static_cast<std::uint64_t>(section.VirtualAddress);
            const auto length = static_cast<std::uint64_t>(
                std::max(section.Misc.VirtualSize, section.SizeOfRawData));
            const auto end = std::min<std::uint64_t>(image.Size, begin + length);
            if (rva < begin || rva >= end)
            {
                continue;
            }
            SectionAtRva result{};
            result.Start = static_cast<std::uint32_t>(begin);
            result.End = static_cast<std::uint32_t>(end);
            result.Characteristics = section.Characteristics;
            std::memcpy(result.Name.data(), section.Name, 8);
            return result;
        }
        return {};
    }

    [[nodiscard]] std::uint32_t CaptureReadableBytes(ImageView image,
        std::uint32_t rva, std::span<std::uint8_t> output) noexcept
    {
        if (image.Base == nullptr || rva >= image.Size || output.empty())
        {
            return 0u;
        }
        MEMORY_BASIC_INFORMATION page{};
        const auto* address = image.Base + rva;
        if (VirtualQuery(address, &page, sizeof(page)) == 0 ||
            page.State != MEM_COMMIT ||
            (page.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0u)
        {
            return 0u;
        }
        const auto start = reinterpret_cast<std::uintptr_t>(page.BaseAddress);
        const auto current = reinterpret_cast<std::uintptr_t>(address);
        if (current < start || current - start >= page.RegionSize)
        {
            return 0u;
        }
        const auto remainingPage = page.RegionSize - (current - start);
        const auto count = std::min<std::size_t>(
            {output.size(), image.Size - rva, remainingPage});
        std::memcpy(output.data(), address, count);
        return static_cast<std::uint32_t>(count);
    }

    struct ProvisionalCodeProbe final
    {
        RuntimeFunctionView Region{};
        std::uint32_t Instructions{};
    };

    [[nodiscard]] ProvisionalCodeProbe ProbeUnwindlessBranchTarget(
        ImageView image, std::uint32_t target,
        const SectionAtRva& section) noexcept
    {
        constexpr std::uint32_t probeBytes = 128u;
        if (section.End <= target ||
            (section.Characteristics & IMAGE_SCN_MEM_EXECUTE) == 0u ||
            !IsExecutableImageAddress(image, target))
        {
            return {};
        }
        MEMORY_BASIC_INFORMATION page{};
        const auto* address = image.Base + target;
        if (VirtualQuery(address, &page, sizeof(page)) == 0 ||
            page.State != MEM_COMMIT)
        {
            return {};
        }
        const std::uintptr_t pageBegin =
            reinterpret_cast<std::uintptr_t>(page.BaseAddress);
        const std::uintptr_t current =
            reinterpret_cast<std::uintptr_t>(address);
        if (current < pageBegin || current - pageBegin >= page.RegionSize)
        {
            return {};
        }
        const auto pageAvailable = page.RegionSize - (current - pageBegin);
        const auto available = std::min<std::uint64_t>({
            probeBytes, static_cast<std::uint64_t>(image.Size - target),
            static_cast<std::uint64_t>(section.End - target),
            static_cast<std::uint64_t>(pageAvailable)});
        if (available < 5u)
        {
            return {};
        }
        std::uint32_t cursor = target;
        std::uint32_t decodedCount = 0u;
        bool hasTerminatingBranch = false;
        const auto end = target + static_cast<std::uint32_t>(available);
        while (cursor < end && decodedCount < 3u)
        {
            std::array<std::uint8_t, 15> bytes{};
            const auto byteCount = CaptureReadableBytes(image, cursor, bytes);
            const auto bounded = std::min<std::uint32_t>(
                byteCount, end - cursor);
            if (bounded == 0u)
            {
                break;
            }
            hde64s decoded{};
            const auto length = hde64_disasm(bytes.data(), &decoded);
            if (length == 0u || length > bounded ||
                (decoded.flags & F_ERROR) != 0u)
            {
                return {};
            }
            ++decodedCount;
            cursor += length;
            hasTerminatingBranch = decoded.opcode == 0xE9u ||
                decoded.opcode == 0xEBu || decoded.opcode == 0xC3u;
            if (hasTerminatingBranch)
            {
                break;
            }
        }
        if (decodedCount < 3u && !hasTerminatingBranch)
        {
            return {};
        }
        return {{target, end}, decodedCount};
    }

    void TraceEnhancedControlFlow(ImageView image, std::uint32_t suspect,
        std::uint32_t linearStopRva,
        CEventGenerator::EnhancedControlFlowEvidence& result,
        const CEventGenerator::EventLocatorDiagnostics& diagnostics)
    {
        if (suspect == 0u || image.Base == nullptr || suspect >= image.Size)
        {
            return;
        }
        const RuntimeFunctionView origin = ResolveRuntimeFunction(
            image, image.Base + suspect);
        if (!origin || origin.BeginRva != suspect ||
            origin.Size() > 0x20000u ||
            !IsExecutableImageAddress(image, origin.BeginRva))
        {
            return;
        }

        result.OwnerRva = origin.BeginRva;
        result.LinearStopRva = linearStopRva;
        constexpr std::uint32_t instructionLimit = 4096u;
        constexpr std::uint32_t blockLimit = 512u;
        constexpr std::uint32_t regionLimit = 8u;
        constexpr std::uint32_t crossDepthLimit = 4u;

        struct PendingBlock final
        {
            std::uint32_t Rva{};
            std::uint32_t CrossDepth{};
        };
        std::unordered_set<std::uint32_t> visited;
        visited.reserve(instructionLimit);
        std::vector<RuntimeFunctionView> regions{origin};
        std::vector<RuntimeFunctionView> provisionalRegions;
        std::vector<PendingBlock> pending{{origin.BeginRva, 0u}};
        result.AcceptedFunctionRegions = 1u;

        const auto resolveRegion = [&](std::uint32_t rva) -> RuntimeFunctionView
        {
            const auto it = std::find_if(regions.begin(), regions.end(),
                [rva](const RuntimeFunctionView& region)
                {
                    return rva >= region.BeginRva && rva < region.EndRva;
                });
            if (it != regions.end())
            {
                return *it;
            }
            return ResolveRuntimeFunction(image, image.Base + rva);
        };

        const auto enqueue = [&](std::uint32_t source,
            const RuntimeFunctionView& sourceRegion,
            std::int64_t target, std::uint32_t depth,
            std::uint32_t instructionLength, std::int64_t displacement)
        {
            const bool external = target < sourceRegion.BeginRva ||
                target >= sourceRegion.EndRva;
            if (external)
            {
                ++result.OutOfFunctionEdges;
            }

            CEventGenerator::EnhancedControlFlowEdge evidence{};
            evidence.SourceRva = source;
            evidence.JumpLength = instructionLength;
            evidence.JumpDisplacement = displacement;
            evidence.SourceByteCount = CaptureReadableBytes(image, source,
                evidence.SourceBytes);
            evidence.TargetRva = target >= 0 &&
                static_cast<std::uint64_t>(target) <= UINT32_MAX
                ? static_cast<std::uint32_t>(target) : 0u;
            if (target < 0 ||
                static_cast<std::uint64_t>(target) >= image.Size ||
                static_cast<std::uint64_t>(target) > UINT32_MAX)
            {
                evidence.Status = 2u;
            }
            else
            {
                const auto rva = static_cast<std::uint32_t>(target);
                MEMORY_BASIC_INFORMATION targetPage{};
                if (VirtualQuery(image.Base + rva, &targetPage,
                    sizeof(targetPage)) != 0)
                {
                    evidence.PageState = targetPage.State;
                    evidence.PageProtection = targetPage.Protect;
                }
                const SectionAtRva section = FindImageSection(image, rva);
                evidence.SectionStartRva = section.Start;
                evidence.SectionEndRva = section.End;
                evidence.SectionCharacteristics = section.Characteristics;
                evidence.SectionName = section.Name;
                evidence.DestinationByteCount = CaptureReadableBytes(image,
                    rva, evidence.DestinationBytes);
                RuntimeFunctionView targetRegion = resolveRegion(rva);
                bool provisional = false;
                if (!IsExecutableImageAddress(image, rva))
                {
                    evidence.Status = 4u;
                }
                else if (!targetRegion)
                {
                    if ((section.Characteristics & IMAGE_SCN_MEM_EXECUTE) == 0u)
                    {
                        evidence.Status = 8u;
                    }
                    else
                    {
                        const ProvisionalCodeProbe probe =
                            ProbeUnwindlessBranchTarget(image, rva, section);
                        evidence.ProbeInstructions = probe.Instructions;
                        evidence.ProbeRegionBytes = probe.Region.Size();
                        if (!probe.Region)
                        {
                            evidence.Status = 9u;
                        }
                        else
                        {
                            targetRegion = probe.Region;
                            provisional = true;
                        }
                    }
                }
                if (targetRegion && evidence.Status == 0u)
                {
                    evidence.DestinationFunctionRva = targetRegion.BeginRva;
                    evidence.DestinationFunctionEndRva = targetRegion.EndRva;
                    if (targetRegion.Size() > 0x20000u)
                    {
                        evidence.Status = 5u;
                    }
                    else
                    {
                        const bool newRegion = std::none_of(
                            regions.begin(), regions.end(),
                            [&targetRegion](const RuntimeFunctionView& region)
                            {
                                return region.BeginRva == targetRegion.BeginRva &&
                                    region.EndRva == targetRegion.EndRva;
                            });
                        if (external && (depth >= crossDepthLimit ||
                            (newRegion && regions.size() >= regionLimit) ||
                            (provisional && provisionalRegions.size() >= 3u)))
                        {
                            evidence.Status = 6u;
                        }
                        else
                        {
                            evidence.Status = provisional ? 7u : 1u;
                            if (newRegion)
                            {
                                regions.push_back(targetRegion);
                                result.AcceptedFunctionRegions =
                                    static_cast<std::uint32_t>(regions.size());
                                if (provisional)
                                {
                                    provisionalRegions.push_back(targetRegion);
                                    ++result.ProvisionalRegions;
                                }
                            }
                            if (!visited.contains(rva))
                            {
                                pending.push_back({rva,
                                    depth + (external ? 1u : 0u)});
                            }
                            if (external)
                            {
                                ++result.FollowedCrossFunctionEdges;
                                result.ProvisionalEdges += provisional ? 1u : 0u;
                            }
                        }
                    }
                }
            }

            if (external)
            {
                if (evidence.Status != 1u && evidence.Status != 7u)
                {
                    ++result.UnfollowedCrossFunctionEdges;
                }
                if (result.EdgeDiagnosticCount < result.EdgeDiagnostics.size())
                {
                    result.EdgeDiagnostics[result.EdgeDiagnosticCount++] =
                        evidence;
                }
            }
        };

        while (!pending.empty() && result.Instructions < instructionLimit &&
            result.BlockStarts < blockLimit)
        {
            const PendingBlock block = pending.back();
            pending.pop_back();
            if (visited.contains(block.Rva))
            {
                continue;
            }
            const RuntimeFunctionView region = resolveRegion(block.Rva);
            if (!region || region.Size() > 0x20000u ||
                !IsExecutableImageAddress(image, block.Rva))
            {
                ++result.DecodeFailures;
                if (result.FirstDecodeFailureRva == 0u)
                {
                    result.FirstDecodeFailureRva = block.Rva;
                }
                continue;
            }
            ++result.BlockStarts;
            const bool provisionalBlock = std::any_of(
                provisionalRegions.begin(), provisionalRegions.end(),
                [&region](const RuntimeFunctionView& candidate)
                {
                    return candidate.BeginRva == region.BeginRva &&
                        candidate.EndRva == region.EndRva;
                });
            for (std::uint32_t cursor = block.Rva;
                cursor < region.EndRva &&
                result.Instructions < instructionLimit &&
                !visited.contains(cursor);)
            {
                std::array<std::uint8_t, 15> bytes{};
                const auto available = std::min<std::uint32_t>(
                    static_cast<std::uint32_t>(bytes.size()),
                    region.EndRva - cursor);
                const auto readable = CaptureReadableBytes(image, cursor, bytes);
                if (readable == 0u)
                {
                    ++result.DecodeFailures;
                    if (result.FirstDecodeFailureRva == 0u)
                    {
                        result.FirstDecodeFailureRva = cursor;
                    }
                    break;
                }
                hde64s instruction{};
                const unsigned int length = hde64_disasm(
                    bytes.data(), &instruction);
                if (length == 0u || length > available || length > readable ||
                    (instruction.flags & F_ERROR) != 0u)
                {
                    ++result.DecodeFailures;
                    if (result.FirstDecodeFailureRva == 0u)
                    {
                        result.FirstDecodeFailureRva = cursor;
                    }
                    break;
                }
                if (result.LinearStopRva > cursor &&
                    static_cast<std::uint64_t>(result.LinearStopRva) <
                        static_cast<std::uint64_t>(cursor) + length)
                {
                    result.StopInsideInstruction = true;
                    result.StopContainingInstructionRva = cursor;
                    result.StopContainingInstructionLength = length;
                    result.StopContainingOpcode = instruction.opcode;
                }
                visited.insert(cursor);
                ++result.Instructions;
                result.ProvisionalInstructions += provisionalBlock ? 1u : 0u;
                result.LinearStopReachable |= cursor == result.LinearStopRva;
                if (instruction.opcode == 0xFFu &&
                    (instruction.flags & F_MODRM) != 0u &&
                    instruction.modrm_reg == 2u)
                {
                    ++result.IndirectCallSites;
                }

                const auto next = static_cast<std::int64_t>(cursor) + length;
                const bool nearJump = instruction.opcode == 0xE9u;
                const bool shortJump = instruction.opcode == 0xEBu;
                const bool shortConditional = instruction.opcode >= 0x70u &&
                    instruction.opcode <= 0x7Fu;
                const bool nearConditional = instruction.opcode == 0x0Fu &&
                    instruction.opcode2 >= 0x80u &&
                    instruction.opcode2 <= 0x8Fu;
                if (nearJump || shortJump ||
                    shortConditional || nearConditional)
                {
                    const std::int64_t displacement = nearJump || nearConditional
                        ? static_cast<std::int32_t>(instruction.imm.imm32)
                        : static_cast<std::int8_t>(instruction.imm.imm8);
                    enqueue(cursor, region, next + displacement,
                        block.CrossDepth, length, displacement);
                    if (shortConditional || nearConditional)
                    {
                        ++result.ConditionalBranches;
                        cursor = static_cast<std::uint32_t>(next);
                        continue;
                    }
                    ++result.DirectBranches;
                    break;
                }
                if (instruction.opcode >= 0xE0u &&
                    instruction.opcode <= 0xE3u)
                {
                    ++result.IndirectExits;
                    break;
                }
                if (instruction.opcode == 0xFFu &&
                    (instruction.flags & F_MODRM) != 0u &&
                    (instruction.modrm_reg == 4u ||
                     instruction.modrm_reg == 5u))
                {
                    ++result.IndirectExits;
                    break;
                }
                if (instruction.opcode == 0xC2u ||
                    instruction.opcode == 0xC3u ||
                    instruction.opcode == 0xCAu ||
                    instruction.opcode == 0xCBu ||
                    instruction.opcode == 0xCFu)
                {
                    break;
                }
                cursor = static_cast<std::uint32_t>(next);
            }
        }
        result.BudgetExceeded = !pending.empty() ||
            result.Instructions >= instructionLimit ||
            result.BlockStarts >= blockLimit;
        for (std::uint32_t index = 0u;
            index < diagnostics.EnhancedVirtualCallDiagnosticCount &&
            index < diagnostics.EnhancedVirtualCalls.size(); ++index)
        {
            const auto& candidate = diagnostics.EnhancedVirtualCalls[index];
            if (candidate.OwnerRva != result.OwnerRva)
            {
                continue;
            }
            ++result.CandidateVirtualCallsites;
            if (visited.contains(candidate.CallsiteRva))
            {
                ++result.ReachableVirtualCallsites;
            }
        }
    }

    void TraceEnhancedStackLoopBack(ImageView image,
        CEventGenerator::EnhancedStackPathEvidence& evidence)
    {
        if ((evidence.Flags & 4u) == 0u ||
            evidence.FirstBranchRva == 0u ||
            !evidence.FirstBranchLeavesLocalPath ||
            evidence.FirstBranchTargetRva == 0u)
        {
            return;
        }
        evidence.LoopBackAnalyzed = true;
        const RuntimeFunctionView region = ResolveRuntimeFunction(image,
            image.Base + evidence.StackLoadRva);
        if (!region || region.Size() > 0x20000u)
        {
            ++evidence.LoopBackUnresolvedExits;
            evidence.LoopBackFirstUnresolvedRva =
                evidence.FirstBranchTargetRva;
            return;
        }
        constexpr std::size_t instructionBudget = 256u;
        constexpr std::size_t branchBudget = 64u;
        std::vector<std::uint32_t> pending{evidence.FirstBranchTargetRva};
        std::unordered_set<std::uint32_t> visited;
        visited.reserve(instructionBudget);
        const auto unresolved = [&](std::uint32_t address)
        {
            ++evidence.LoopBackUnresolvedExits;
            if (evidence.LoopBackFirstUnresolvedRva == 0u)
            {
                evidence.LoopBackFirstUnresolvedRva = address;
            }
        };
        while (!pending.empty() &&
            evidence.LoopBackInstructions < instructionBudget &&
            evidence.LoopBackBranches < branchBudget)
        {
            const std::uint32_t cursor = pending.back();
            pending.pop_back();
            if (cursor == evidence.StackLoadRva)
            {
                ++evidence.LoopBackReachesStackLoad;
                continue;
            }
            if (cursor == evidence.CallsiteRva)
            {
                ++evidence.LoopBackReachesCallWithoutLoad;
                continue;
            }
            if (!visited.insert(cursor).second)
            {
                continue;
            }
            if (cursor < region.BeginRva || cursor >= region.EndRva ||
                !IsExecutableImageAddress(image, cursor))
            {
                unresolved(cursor);
                continue;
            }
            std::array<std::uint8_t, 15> bytes{};
            const std::uint32_t available = CaptureReadableBytes(
                image, cursor, bytes);
            hde64s instruction{};
            const unsigned int length = available == 0u ? 0u :
                hde64_disasm(bytes.data(), &instruction);
            if (length == 0u || length > available ||
                length > region.EndRva - cursor ||
                (instruction.flags & F_ERROR) != 0u)
            {
                unresolved(cursor);
                continue;
            }
            ++evidence.LoopBackInstructions;
            const std::uint32_t next = cursor + length;
            const bool jumpNear = instruction.opcode == 0xE9u;
            const bool jumpShort = instruction.opcode == 0xEBu;
            const bool jccShort = instruction.opcode >= 0x70u &&
                instruction.opcode <= 0x7Fu;
            const bool jccNear = instruction.opcode == 0x0Fu &&
                instruction.opcode2 >= 0x80u &&
                instruction.opcode2 <= 0x8Fu;
            if (jumpNear || jumpShort || jccShort || jccNear)
            {
                ++evidence.LoopBackBranches;
                const std::int64_t displacement = jumpNear || jccNear
                    ? static_cast<std::int32_t>(instruction.imm.imm32)
                    : static_cast<std::int8_t>(instruction.imm.imm8);
                const std::int64_t target =
                    static_cast<std::int64_t>(next) + displacement;
                if (target < 0 || target > UINT32_MAX)
                {
                    unresolved(cursor);
                }
                else
                {
                    pending.push_back(static_cast<std::uint32_t>(target));
                }
                if (jccShort || jccNear)
                {
                    pending.push_back(next);
                }
                continue;
            }
            if (instruction.opcode == 0xC2u ||
                instruction.opcode == 0xC3u ||
                instruction.opcode == 0xCAu ||
                instruction.opcode == 0xCBu)
            {
                ++evidence.LoopBackTerminalExits;
                continue;
            }
            if ((instruction.opcode == 0xFFu &&
                (instruction.flags & F_MODRM) != 0u &&
                (instruction.modrm_reg == 4u ||
                 instruction.modrm_reg == 5u)) ||
                (instruction.opcode >= 0xE0u && instruction.opcode <= 0xE3u))
            {
                unresolved(cursor);
                continue;
            }
            pending.push_back(next);
        }
        evidence.LoopBackBudgetExceeded = !pending.empty();
        if (evidence.LoopBackBudgetExceeded)
        {
            unresolved(pending.back());
        }
    }

    enum class StackRegisterEffect : std::uint8_t
    {
        Preserved,
        Overwritten,
        Unknown
    };

    [[nodiscard]] StackRegisterEffect InspectStackRegisterEffect(
        const hde64s& instruction, std::uint32_t objectRegister) noexcept
    {
        const std::uint32_t opcode = instruction.opcode;
        if (opcode == 0x90u || opcode == 0x85u ||
            opcode == 0x39u || opcode == 0x3Bu ||
            opcode == 0x3Du || opcode == 0x84u ||
            opcode == 0xE8u || opcode == 0xE9u ||
            opcode == 0xEBu || opcode == 0xC3u ||
            opcode == 0xC2u ||
            (opcode >= 0x70u && opcode <= 0x7Fu) ||
            (opcode == 0x0Fu && instruction.opcode2 >= 0x80u &&
                instruction.opcode2 <= 0x8Fu) ||
            (opcode >= 0x50u && opcode <= 0x57u) ||
            (opcode >= 0xE0u && opcode <= 0xE3u))
        {
            return StackRegisterEffect::Preserved;
        }
        if (opcode >= 0x58u && opcode <= 0x5Fu)
        {
            const std::uint32_t destination =
                (opcode - 0x58u) + (instruction.rex_b != 0u ? 8u : 0u);
            return destination == objectRegister
                ? StackRegisterEffect::Overwritten
                : StackRegisterEffect::Preserved;
        }
        if (opcode >= 0xB0u && opcode <= 0xB7u)
        {
            const auto encoded = opcode - 0xB0u;
            const auto destination = instruction.rex == 0u &&
                encoded >= 4u ? encoded - 4u :
                GeneralRegister(encoded, instruction.rex_b != 0u);
            return destination == objectRegister
                ? StackRegisterEffect::Overwritten
                : StackRegisterEffect::Preserved;
        }
        if (opcode >= 0xB8u && opcode <= 0xBFu)
        {
            const std::uint32_t destination =
                (opcode - 0xB8u) + (instruction.rex_b != 0u ? 8u : 0u);
            return destination == objectRegister
                ? StackRegisterEffect::Overwritten
                : StackRegisterEffect::Preserved;
        }
        if ((instruction.flags & F_MODRM) == 0u)
        {
            return StackRegisterEffect::Unknown;
        }
        const std::uint32_t reg = GeneralRegister(
            instruction.modrm_reg, instruction.rex_r != 0u);
        const std::uint32_t rm = GeneralRegister(
            instruction.modrm_rm, instruction.rex_b != 0u);
        if (opcode == 0x8Bu || opcode == 0x8Du ||
            opcode == 0x33u || opcode == 0x03u || opcode == 0x2Bu)
        {
            return reg == objectRegister
                ? StackRegisterEffect::Overwritten
                : StackRegisterEffect::Preserved;
        }
        if (opcode == 0x89u || opcode == 0x31u ||
            opcode == 0x01u || opcode == 0x29u ||
            opcode == 0xC7u)
        {
            return instruction.modrm_mod == 3u && rm == objectRegister
                ? StackRegisterEffect::Overwritten
                : StackRegisterEffect::Preserved;
        }
        if (opcode == 0x83u || opcode == 0x81u)
        {
            if (instruction.modrm_reg == 7u)
            {
                return StackRegisterEffect::Preserved;
            }
            return instruction.modrm_mod == 3u && rm == objectRegister
                ? StackRegisterEffect::Overwritten
                : StackRegisterEffect::Preserved;
        }
        if (opcode == 0xFFu)
        {
            if (instruction.modrm_reg == 2u ||
                instruction.modrm_reg == 3u)
            {
                return StackRegisterEffect::Preserved;
            }
            if (instruction.modrm_reg == 0u ||
                instruction.modrm_reg == 1u)
            {
                return instruction.modrm_mod == 3u && rm == objectRegister
                    ? StackRegisterEffect::Overwritten
                    : StackRegisterEffect::Preserved;
            }
        }
        return StackRegisterEffect::Unknown;
    }

    struct RaxDefinition final
    {
        std::uint32_t Kind{};
        std::uint32_t InstructionRva{};
        std::uint32_t SourceRegister{0xFFFFFFFFu};
        std::int32_t Displacement{};
        std::uintptr_t Absolute{};
        bool operator==(const RaxDefinition&) const = default;
    };

    void AdvanceRaxDefinition(ImageView image, std::uint32_t rva,
        unsigned int length, const hde64s& instruction,
        RaxDefinition& definition) noexcept
    {
        const auto set = [&](std::uint32_t kind,
            std::uint32_t reg = 0xFFFFFFFFu, std::int32_t displacement = 0,
            std::uintptr_t absolute = 0u)
        {
            definition = {kind, rva, reg, displacement, absolute};
        };
        const auto reg = GeneralRegister(instruction.modrm_reg,
            instruction.rex_r != 0u);
        const auto rm = GeneralRegister(instruction.modrm_rm,
            instruction.rex_b != 0u);
        const auto byteReg = ByteRegister(instruction.modrm_reg,
            instruction.rex_r != 0u, instruction.rex != 0u);
        const auto byteRm = ByteRegister(instruction.modrm_rm,
            instruction.rex_b != 0u, instruction.rex != 0u);
        const auto displacement = [&]() -> std::int32_t
        {
            if ((instruction.flags & F_DISP8) != 0u)
            {
                return static_cast<std::int8_t>(instruction.disp.disp8);
            }
            if ((instruction.flags & F_DISP32) != 0u)
            {
                return static_cast<std::int32_t>(instruction.disp.disp32);
            }
            return 0;
        };
        if (instruction.opcode == 0xE8u ||
            (instruction.opcode == 0xFFu &&
             (instruction.flags & F_MODRM) != 0u &&
             (instruction.modrm_reg == 2u || instruction.modrm_reg == 3u)))
        {
            set(5u);
            return;
        }
        if (instruction.opcode >= 0x58u && instruction.opcode <= 0x5Fu)
        {
            if (instruction.opcode == 0x58u && instruction.rex_b == 0u)
            {
                set(6u);
            }
            return;
        }
        if (instruction.opcode >= 0xB0u && instruction.opcode <= 0xB7u)
        {
            if (ByteRegister(instruction.opcode - 0xB0u,
                instruction.rex_b != 0u, instruction.rex != 0u) == 0u)
            {
                set(7u);
            }
            return;
        }
        if (instruction.opcode >= 0xB8u && instruction.opcode <= 0xBFu)
        {
            if (instruction.opcode == 0xB8u && instruction.rex_b == 0u)
            {
                if (instruction.rex_w != 0u &&
                    (instruction.flags & F_IMM64) != 0u)
                {
                    set(1u, 0xFFFFFFFFu, 0,
                        static_cast<std::uintptr_t>(instruction.imm.imm64));
                }
                else if ((instruction.flags & F_IMM32) != 0u)
                {
                    set(1u, 0xFFFFFFFFu, 0,
                        static_cast<std::uintptr_t>(instruction.imm.imm32));
                }
                else
                {
                    set(7u);
                }
            }
            return;
        }
        if (instruction.opcode == 0x0Fu &&
            (instruction.flags & F_MODRM) != 0u)
        {
            if ((instruction.opcode2 >= 0x40u &&
                 instruction.opcode2 <= 0x4Fu) ||
                instruction.opcode2 == 0xAFu ||
                instruction.opcode2 == 0xB6u ||
                instruction.opcode2 == 0xB7u ||
                instruction.opcode2 == 0xB8u ||
                instruction.opcode2 == 0xBEu ||
                instruction.opcode2 == 0xBFu ||
                instruction.opcode2 == 0xBCu ||
                instruction.opcode2 == 0xBDu)
            {
                if (reg == 0u)
                {
                    set(7u);
                }
                return;
            }
            if (instruction.opcode2 >= 0x90u &&
                instruction.opcode2 <= 0x9Fu)
            {
                if (instruction.modrm_mod == 3u && byteRm == 0u)
                {
                    set(7u);
                }
                return;
            }
        }
        if ((instruction.flags & F_MODRM) != 0u)
        {
            if (instruction.opcode == 0x63u)
            {
                if (reg == 0u)
                {
                    set(7u);
                }
                return;
            }
            if (instruction.opcode == 0x8Bu ||
                instruction.opcode == 0x8Du)
            {
                if (reg != 0u)
                {
                    return;
                }
                if (instruction.opcode == 0x8Bu)
                {
                    if (instruction.modrm_mod == 3u)
                    {
                        if (rm != 0u || instruction.rex_w == 0u)
                        {
                            set(4u, rm);
                        }
                    }
                    else
                    {
                        set(3u, (instruction.flags & F_SIB) != 0u ?
                            0xFFFFFFFFu : rm, displacement());
                    }
                }
                else if (instruction.modrm_mod == 0u &&
                    instruction.modrm_rm == 5u &&
                    (instruction.flags & F_SIB) == 0u &&
                    (instruction.flags & F_DISP32) != 0u &&
                    instruction.rex_w != 0u && instruction.p_67 == 0u &&
                    instruction.p_seg == 0u)
                {
                    const std::int64_t target =
                        static_cast<std::int64_t>(rva) + length +
                        static_cast<std::int32_t>(instruction.disp.disp32);
                    if (target >= 0 &&
                        static_cast<std::uint64_t>(target) < image.Size)
                    {
                        set(2u, 0xFFFFFFFFu, displacement(),
                            reinterpret_cast<std::uintptr_t>(image.Base +
                                static_cast<std::size_t>(target)));
                    }
                    else
                    {
                        set(7u);
                    }
                }
                else
                {
                    set(4u, (instruction.flags & F_SIB) != 0u ?
                        0xFFFFFFFFu : rm, displacement());
                }
                return;
            }
            if (instruction.opcode == 0x89u)
            {
                if (instruction.modrm_mod == 3u && rm == 0u &&
                    (reg != 0u || instruction.rex_w == 0u))
                {
                    set(4u, reg);
                }
                return;
            }
            if (instruction.opcode == 0x31u || instruction.opcode == 0x33u)
            {
                const bool destinationIsRax =
                    (instruction.opcode == 0x31u &&
                     instruction.modrm_mod == 3u && rm == 0u) ||
                    (instruction.opcode == 0x33u && reg == 0u);
                if (destinationIsRax)
                {
                    if (instruction.modrm_mod == 3u && reg == 0u && rm == 0u)
                    {
                        set(8u, 0xFFFFFFFFu, 0, 0u);
                    }
                    else
                    {
                        set(7u);
                    }
                }
                return;
            }
            if (instruction.opcode == 0x83u || instruction.opcode == 0x81u ||
                instruction.opcode == 0x80u || instruction.opcode == 0xC7u ||
                instruction.opcode == 0xC6u || instruction.opcode == 0xFFu)
            {
                if (instruction.opcode == 0xFFu)
                {
                    if (instruction.modrm_reg == 4u ||
                        instruction.modrm_reg == 5u ||
                        instruction.modrm_reg == 6u)
                    {
                        return;
                    }
                }
                if ((instruction.opcode == 0x83u ||
                     instruction.opcode == 0x81u ||
                     instruction.opcode == 0x80u) &&
                    instruction.modrm_reg == 7u)
                {
                    return;
                }
                if (instruction.modrm_mod == 3u &&
                    ((instruction.opcode == 0x80u ||
                      instruction.opcode == 0xC6u) ? byteRm : rm) == 0u)
                {
                    set(7u);
                }
                return;
            }
            if (instruction.opcode == 0x03u || instruction.opcode == 0x2Bu ||
                instruction.opcode == 0x33u || instruction.opcode == 0x0Bu ||
                instruction.opcode == 0x23u || instruction.opcode == 0x8Au)
            {
                if ((instruction.opcode == 0x8Au ? byteReg : reg) == 0u)
                {
                    set(7u);
                }
                return;
            }
            if (instruction.opcode == 0x01u || instruction.opcode == 0x29u ||
                instruction.opcode == 0x09u || instruction.opcode == 0x21u ||
                instruction.opcode == 0x88u)
            {
                if (instruction.modrm_mod == 3u &&
                    (instruction.opcode == 0x88u ? byteRm : rm) == 0u)
                {
                    set(7u);
                }
                return;
            }
            if (instruction.opcode == 0x8Fu &&
                instruction.modrm_reg == 0u)
            {
                if (instruction.modrm_mod == 3u && rm == 0u)
                {
                    set(7u);
                }
                return;
            }
            if (instruction.opcode == 0x39u || instruction.opcode == 0x3Bu ||
                instruction.opcode == 0x85u || instruction.opcode == 0x84u)
            {
                return;
            }
        }
        if (instruction.opcode == 0x90u || instruction.opcode == 0xE9u ||
            instruction.opcode == 0xEBu || instruction.opcode == 0xC3u ||
            instruction.opcode == 0xC2u || instruction.opcode == 0x3Du ||
            instruction.opcode == 0x3Cu || instruction.opcode == 0xA8u ||
            instruction.opcode == 0xA9u || instruction.opcode == 0x68u ||
            instruction.opcode == 0x6Au || instruction.opcode == 0x9Cu ||
            instruction.opcode == 0x9Du || instruction.opcode == 0xC9u ||
            (instruction.opcode >= 0xF8u && instruction.opcode <= 0xFDu) ||
            (instruction.opcode >= 0xE0u && instruction.opcode <= 0xE3u) ||
            (instruction.opcode >= 0x50u && instruction.opcode <= 0x57u) ||
            (instruction.opcode >= 0x70u && instruction.opcode <= 0x7Fu) ||
            (instruction.opcode == 0x0Fu &&
             ((instruction.opcode2 >= 0x80u && instruction.opcode2 <= 0x8Fu) ||
              (instruction.opcode2 == 0x1Fu &&
               instruction.modrm_reg == 0u))))
        {
            return;
        }
        set(7u);
    }

    void AdvanceNonvolatileDefinition(ImageView image, std::uint32_t rva,
        unsigned int length, const hde64s& instruction,
        RaxDefinition& definition,
        std::uint32_t trackedRegister) noexcept
    {
        const auto set = [&](std::uint32_t kind,
            std::uint32_t source = 0xFFFFFFFFu,
            std::int32_t displacement = 0,
            std::uintptr_t absolute = 0u)
        {
            definition = {kind, rva, source, displacement, absolute};
        };
        const auto reg = GeneralRegister(instruction.modrm_reg,
            instruction.rex_r != 0u);
        const auto rm = GeneralRegister(instruction.modrm_rm,
            instruction.rex_b != 0u);
        const auto byteReg = ByteRegister(instruction.modrm_reg,
            instruction.rex_r != 0u, instruction.rex != 0u);
        const auto byteRm = ByteRegister(instruction.modrm_rm,
            instruction.rex_b != 0u, instruction.rex != 0u);
        const auto displacement = [&]() -> std::int32_t
        {
            if ((instruction.flags & F_DISP8) != 0u)
            {
                return static_cast<std::int8_t>(instruction.disp.disp8);
            }
            if ((instruction.flags & F_DISP32) != 0u)
            {
                return static_cast<std::int32_t>(instruction.disp.disp32);
            }
            return 0;
        };
        if (instruction.opcode == 0xE8u ||
            (instruction.opcode == 0xFFu &&
             (instruction.flags & F_MODRM) != 0u &&
             (instruction.modrm_reg == 2u || instruction.modrm_reg == 3u)))
        {
            return;
        }
        if (instruction.opcode >= 0x58u && instruction.opcode <= 0x5Fu)
        {
            if (GeneralRegister(instruction.opcode - 0x58u,
                instruction.rex_b != 0u) == trackedRegister)
            {
                set(6u);
            }
            return;
        }
        if (instruction.opcode >= 0xB0u && instruction.opcode <= 0xB7u)
        {
            const auto destination = ByteRegister(
                instruction.opcode - 0xB0u, instruction.rex_b != 0u,
                instruction.rex != 0u);
            if (destination == trackedRegister)
            {
                set(7u);
            }
            return;
        }
        if (instruction.opcode >= 0xB8u && instruction.opcode <= 0xBFu)
        {
            if (GeneralRegister(instruction.opcode - 0xB8u,
                instruction.rex_b != 0u) == trackedRegister)
            {
                if (instruction.rex_w != 0u &&
                    (instruction.flags & F_IMM64) != 0u)
                {
                    set(1u, 0xFFFFFFFFu, 0,
                        static_cast<std::uintptr_t>(instruction.imm.imm64));
                }
                else if ((instruction.flags & F_IMM32) != 0u)
                {
                    set(1u, 0xFFFFFFFFu, 0,
                        static_cast<std::uintptr_t>(instruction.imm.imm32));
                }
                else
                {
                    set(7u);
                }
            }
            return;
        }
        if (instruction.opcode == 0x0Fu &&
            (instruction.flags & F_MODRM) != 0u)
        {
            if ((instruction.opcode2 >= 0x40u &&
                 instruction.opcode2 <= 0x4Fu) ||
                instruction.opcode2 == 0xAFu ||
                instruction.opcode2 == 0xB6u ||
                instruction.opcode2 == 0xB7u ||
                instruction.opcode2 == 0xB8u ||
                instruction.opcode2 == 0xBEu ||
                instruction.opcode2 == 0xBFu ||
                instruction.opcode2 == 0xBCu ||
                instruction.opcode2 == 0xBDu)
            {
                if (reg == trackedRegister)
                {
                    set(7u);
                }
                return;
            }
            if (instruction.opcode2 >= 0x90u &&
                instruction.opcode2 <= 0x9Fu)
            {
                if (instruction.modrm_mod == 3u &&
                    byteRm == trackedRegister)
                {
                    set(7u);
                }
                return;
            }
        }
        if ((instruction.flags & F_MODRM) != 0u)
        {
            if (instruction.opcode == 0x63u)
            {
                if (reg == trackedRegister)
                {
                    set(7u);
                }
                return;
            }
            if (instruction.opcode == 0x8Bu || instruction.opcode == 0x8Du)
            {
                if (reg != trackedRegister)
                {
                    return;
                }
                if (instruction.opcode == 0x8Bu)
                {
                    if (instruction.modrm_mod == 3u)
                    {
                        set(4u, rm);
                    }
                    else
                    {
                        set(3u, (instruction.flags & F_SIB) != 0u ?
                            0xFFFFFFFFu : rm, displacement());
                    }
                }
                else if (instruction.modrm_mod == 0u &&
                    instruction.modrm_rm == 5u &&
                    (instruction.flags & F_SIB) == 0u &&
                    (instruction.flags & F_DISP32) != 0u &&
                    instruction.rex_w != 0u && instruction.p_67 == 0u &&
                    instruction.p_seg == 0u)
                {
                    const std::int64_t target =
                        static_cast<std::int64_t>(rva) + length +
                        static_cast<std::int32_t>(instruction.disp.disp32);
                    if (target >= 0 &&
                        static_cast<std::uint64_t>(target) < image.Size)
                    {
                        set(2u, 0xFFFFFFFFu, displacement(),
                            reinterpret_cast<std::uintptr_t>(image.Base +
                                static_cast<std::size_t>(target)));
                    }
                    else
                    {
                        set(7u);
                    }
                }
                else
                {
                    set(4u, (instruction.flags & F_SIB) != 0u ?
                        0xFFFFFFFFu : rm, displacement());
                }
                return;
            }
            if (instruction.opcode == 0x89u)
            {
                if (instruction.modrm_mod == 3u && rm == trackedRegister)
                {
                    set(4u, reg);
                }
                return;
            }
            if (instruction.opcode == 0x31u || instruction.opcode == 0x33u)
            {
                const bool destination =
                    (instruction.opcode == 0x31u &&
                     instruction.modrm_mod == 3u && rm == trackedRegister) ||
                    (instruction.opcode == 0x33u && reg == trackedRegister);
                if (destination)
                {
                    if (instruction.modrm_mod == 3u &&
                        reg == trackedRegister && rm == trackedRegister)
                    {
                        set(8u, 0xFFFFFFFFu, 0, 0u);
                    }
                    else
                    {
                        set(7u);
                    }
                }
                return;
            }
            if (instruction.opcode == 0x83u ||
                instruction.opcode == 0x81u ||
                instruction.opcode == 0x80u)
            {
                if (instruction.modrm_reg == 7u)
                {
                    return;
                }
                if (instruction.modrm_mod == 3u &&
                    (instruction.opcode == 0x80u ? byteRm : rm) ==
                        trackedRegister)
                {
                    set(7u);
                }
                return;
            }
            if (instruction.opcode == 0xC7u ||
                instruction.opcode == 0xC6u ||
                instruction.opcode == 0xFFu)
            {
                if (instruction.opcode == 0xFFu &&
                    (instruction.modrm_reg == 4u ||
                     instruction.modrm_reg == 5u ||
                     instruction.modrm_reg == 6u))
                {
                    return;
                }
                if (instruction.modrm_mod == 3u &&
                    (instruction.opcode == 0xC6u ? byteRm : rm) ==
                        trackedRegister)
                {
                    set(7u);
                }
                return;
            }
            if (instruction.opcode == 0x39u || instruction.opcode == 0x3Bu ||
                instruction.opcode == 0x85u || instruction.opcode == 0x84u)
            {
                return;
            }
            if (instruction.opcode == 0x01u || instruction.opcode == 0x29u ||
                instruction.opcode == 0x09u || instruction.opcode == 0x21u ||
                instruction.opcode == 0x88u || instruction.opcode == 0x87u)
            {
                if (instruction.modrm_mod == 3u &&
                    ((instruction.opcode == 0x88u ? byteRm : rm) ==
                        trackedRegister ||
                     (instruction.opcode == 0x87u && reg == trackedRegister)))
                {
                    set(7u);
                }
                return;
            }
            if (instruction.opcode == 0x03u || instruction.opcode == 0x2Bu ||
                instruction.opcode == 0x0Bu || instruction.opcode == 0x23u ||
                instruction.opcode == 0x8Au)
            {
                if ((instruction.opcode == 0x8Au ? byteReg : reg) ==
                    trackedRegister)
                {
                    set(7u);
                }
                return;
            }
            if (instruction.opcode == 0x8Fu &&
                instruction.modrm_reg == 0u)
            {
                if (instruction.modrm_mod == 3u && rm == trackedRegister)
                {
                    set(7u);
                }
                return;
            }
        }
        if (instruction.opcode == 0x90u || instruction.opcode == 0xE9u ||
            instruction.opcode == 0xEBu || instruction.opcode == 0xC3u ||
            instruction.opcode == 0xC2u || instruction.opcode == 0x3Du ||
            instruction.opcode == 0x3Cu || instruction.opcode == 0xA8u ||
            instruction.opcode == 0xA9u || instruction.opcode == 0x68u ||
            instruction.opcode == 0x6Au || instruction.opcode == 0x9Cu ||
            instruction.opcode == 0x9Du || instruction.opcode == 0xC9u ||
            (instruction.opcode >= 0xF8u && instruction.opcode <= 0xFDu) ||
            (instruction.opcode >= 0xE0u && instruction.opcode <= 0xE3u) ||
            (instruction.opcode >= 0x50u && instruction.opcode <= 0x57u) ||
            (instruction.opcode >= 0x70u && instruction.opcode <= 0x7Fu) ||
            (instruction.opcode == 0x0Fu &&
             ((instruction.opcode2 >= 0x80u &&
               instruction.opcode2 <= 0x8Fu) ||
              (instruction.opcode2 == 0x1Fu &&
               instruction.modrm_reg == 0u))))
        {
            return;
        }
        set(7u);
    }

    struct IndirectJumpProbe final
    {
        std::uint32_t Mode{};
        std::uint32_t Register{0xFFFFFFFFu};
        std::int32_t Displacement{};
        std::uint32_t SlotRva{};
        std::uint32_t TargetRva{};
        std::uint32_t Status{};
    };

    [[nodiscard]] IndirectJumpProbe ProbeStaticIndirectJump(
        ImageView image, std::uint32_t source, unsigned int length,
        const hde64s& instruction, std::uint32_t previousRva,
        unsigned int previousLength) noexcept
    {
        IndirectJumpProbe result{};
        if (instruction.modrm_mod == 3u)
        {
            result.Mode = 1u;
            result.Register = GeneralRegister(instruction.modrm_rm,
                instruction.rex_b != 0u);
            if (previousRva != 0u &&
                static_cast<std::uint64_t>(previousRva) + previousLength == source)
            {
                std::array<std::uint8_t, 15> previousBytes{};
                const auto available = CaptureReadableBytes(image, previousRva,
                    previousBytes);
                hde64s previous{};
                if (available >= previousLength && previousLength != 0u &&
                    hde64_disasm(previousBytes.data(), &previous) == previousLength &&
                    (previous.flags & F_ERROR) == 0u &&
                    result.Register == GeneralRegister(0u, false))
                {
                    std::uint64_t absolute = 0u;
                    bool known = false;
                    if (previous.opcode == 0xB8u && previous.rex_w != 0u &&
                        previous.rex_b == 0u &&
                        (previous.flags & F_IMM64) != 0u)
                    {
                        absolute = previous.imm.imm64;
                        known = true;
                    }
                    else if (previous.opcode == 0x8Du && previous.rex_w != 0u &&
                        previous.modrm_reg == 0u && previous.rex_r == 0u &&
                        previous.modrm_mod == 0u && previous.modrm_rm == 5u &&
                        (previous.flags & F_DISP32) != 0u &&
                        previous.p_67 == 0u && previous.p_seg == 0u)
                    {
                        const std::int64_t target =
                            static_cast<std::int64_t>(previousRva) +
                            previousLength +
                            static_cast<std::int32_t>(previous.disp.disp32);
                        if (target >= 0 &&
                            static_cast<std::uint64_t>(target) < image.Size)
                        {
                            absolute = reinterpret_cast<std::uintptr_t>(
                                image.Base + static_cast<std::size_t>(target));
                            known = true;
                        }
                    }
                    if (known)
                    {
                        const auto base = reinterpret_cast<std::uintptr_t>(image.Base);
                        if (absolute >= base && absolute - base < image.Size)
                        {
                            const auto target = static_cast<std::uint32_t>(absolute - base);
                            if (IsExecutableImageAddress(image, target) &&
                                (FindImageSection(image, target).Characteristics &
                                    IMAGE_SCN_MEM_EXECUTE) != 0u)
                            {
                                result.TargetRva = target;
                                result.Status = 1u;
                            }
                        }
                    }
                }
            }
            return result;
        }
        result.Mode = 2u;
        result.Register = GeneralRegister(instruction.modrm_rm,
            instruction.rex_b != 0u);
        if ((instruction.flags & F_DISP8) != 0u)
        {
            result.Displacement = static_cast<std::int8_t>(instruction.disp.disp8);
        }
        else if ((instruction.flags & F_DISP32) != 0u)
        {
            result.Displacement = static_cast<std::int32_t>(instruction.disp.disp32);
        }
        if (instruction.modrm_mod != 0u || instruction.modrm_rm != 5u ||
            (instruction.flags & F_SIB) != 0u ||
            (instruction.flags & F_DISP32) == 0u ||
            instruction.p_67 != 0u || instruction.p_seg != 0u)
        {
            return result;
        }
        result.Mode = 3u;
        result.Register = 0xFFFFFFFFu;
        const std::int64_t slot = static_cast<std::int64_t>(source) +
            length + result.Displacement;
        if (slot < 0 || static_cast<std::uint64_t>(slot) >= image.Size)
        {
            return result;
        }
        result.SlotRva = static_cast<std::uint32_t>(slot);
        const SectionAtRva section = FindImageSection(image, result.SlotRva);
        if (section.End <= result.SlotRva ||
            (section.Characteristics & IMAGE_SCN_MEM_WRITE) != 0u)
        {
            return result;
        }
        std::array<std::uint8_t, sizeof(std::uintptr_t)> pointerBytes{};
        if (CaptureReadableBytes(image, result.SlotRva, pointerBytes) !=
            pointerBytes.size())
        {
            return result;
        }
        std::uintptr_t absolute{};
        std::memcpy(&absolute, pointerBytes.data(), sizeof(absolute));
        const auto base = reinterpret_cast<std::uintptr_t>(image.Base);
        if (absolute < base || absolute - base >= image.Size)
        {
            return result;
        }
        const auto target = static_cast<std::uint32_t>(absolute - base);
        if (!IsExecutableImageAddress(image, target) ||
            (FindImageSection(image, target).Characteristics &
                IMAGE_SCN_MEM_EXECUTE) == 0u)
        {
            return result;
        }
        result.TargetRva = target;
        result.Status = 2u;
        return result;
    }

    [[nodiscard]] bool ResolveRaxDefinedJump(ImageView image,
        const hde64s& instruction, const RaxDefinition& definition,
        IndirectJumpProbe& probe) noexcept
    {
        if ((definition.Kind != 1u && definition.Kind != 2u) ||
            probe.Register != 0u || probe.Status != 0u ||
            instruction.modrm_reg != 4u)
        {
            return false;
        }
        std::uintptr_t absolute = definition.Absolute;
        const auto base = reinterpret_cast<std::uintptr_t>(image.Base);
        if (probe.Mode == 2u)
        {
            if (instruction.modrm_mod == 3u ||
                (instruction.flags & F_SIB) != 0u)
            {
                return false;
            }
            if (probe.Displacement >= 0)
            {
                if (absolute > UINTPTR_MAX -
                    static_cast<std::uintptr_t>(probe.Displacement))
                {
                    return false;
                }
                absolute += static_cast<std::uintptr_t>(probe.Displacement);
            }
            else
            {
                const auto magnitude = static_cast<std::uintptr_t>(
                    -static_cast<std::int64_t>(probe.Displacement));
                if (absolute < magnitude)
                {
                    return false;
                }
                absolute -= magnitude;
            }
            if (absolute < base || absolute - base > image.Size ||
                image.Size - (absolute - base) < sizeof(std::uintptr_t))
            {
                return false;
            }
            const auto slot = static_cast<std::uint32_t>(absolute - base);
            const auto section = FindImageSection(image, slot);
            if (section.End < slot ||
                section.End - slot < sizeof(std::uintptr_t) ||
                (section.Characteristics & IMAGE_SCN_MEM_WRITE) != 0u)
            {
                return false;
            }
            std::array<std::uint8_t, sizeof(std::uintptr_t)> bytes{};
            if (CaptureReadableBytes(image, slot, bytes) != bytes.size())
            {
                return false;
            }
            std::memcpy(&absolute, bytes.data(), sizeof(absolute));
            probe.SlotRva = slot;
        }
        else if (probe.Mode != 1u)
        {
            return false;
        }
        if (absolute < base || absolute - base >= image.Size)
        {
            return false;
        }
        const auto target = static_cast<std::uint32_t>(absolute - base);
        if (!IsExecutableImageAddress(image, target) ||
            (FindImageSection(image, target).Characteristics &
                IMAGE_SCN_MEM_EXECUTE) == 0u)
        {
            return false;
        }
        probe.TargetRva = target;
        probe.Status = probe.Mode == 1u ? 3u : 4u;
        return true;
    }

    void TraceEnhancedStackEntryFlow(ImageView image,
        CEventGenerator::EnhancedStackPathEvidence& evidence)
    {
        if ((evidence.Flags & 4u) == 0u ||
            evidence.StackLoadRva == 0u || evidence.CallsiteRva == 0u)
        {
            return;
        }
        const RuntimeFunctionView rootRegion = ResolveRuntimeFunction(image,
            image.Base + evidence.StackLoadRva);
        if (!rootRegion || rootRegion.Size() > 0x20000u ||
            evidence.CallsiteRva < rootRegion.BeginRva ||
            evidence.CallsiteRva >= rootRegion.EndRva)
        {
            return;
        }
        evidence.EntryAnalyzed = true;
        evidence.EntryFunctionRva = rootRegion.BeginRva;
        constexpr std::uint32_t instructionBudget = 4096u;
        constexpr std::uint32_t branchBudget = 1024u;
        constexpr std::uint32_t crossDepthLimit = 6u;
        constexpr std::size_t regionLimit = 12u;
        constexpr std::uint32_t provisionalLimit = 4u;
        struct EntryState final
        {
            std::uint32_t Rva{};
            std::uint8_t Definition{};
            std::uint8_t RegionIndex{};
            std::uint8_t Depth{};
            std::uint32_t PreviousRva{};
            std::uint8_t PreviousLength{};
            RaxDefinition Rax{};
            RaxDefinition Rdi{};
            RaxDefinition Rbx{};
            RaxDefinition RaxBase{};
            RaxDefinition RaxBaseParent{};
            RaxDefinition RaxBaseGrandparent{};
            RaxDefinition RdiBase{};
            RaxDefinition RbxBase{};
            RaxDefinition Rsi{};
            RaxDefinition Rdx{};
            std::uint32_t IncomingRva{};
            std::uint8_t IncomingKind{};
        };
        struct EntryRegion final
        {
            RuntimeFunctionView Range{};
            bool Provisional{};
        };
        std::vector<EntryRegion> regions{{rootRegion, false}};
        std::vector<EntryState> pending{{rootRegion.BeginRva, 0u, 0u, 0u}};
        struct VisitKey final
        {
            std::uint32_t Rva{};
            std::uint32_t PreviousRva{};
            std::uint8_t Definition{};
            std::uint8_t Region{};
            RaxDefinition Rax{};
            RaxDefinition Rdi{};
            RaxDefinition Rbx{};
            RaxDefinition RaxBase{};
            RaxDefinition RaxBaseParent{};
            RaxDefinition RaxBaseGrandparent{};
            RaxDefinition RdiBase{};
            RaxDefinition RbxBase{};
            RaxDefinition Rsi{};
            RaxDefinition Rdx{};
            bool operator==(const VisitKey&) const = default;
        };
        struct VisitHash final
        {
            [[nodiscard]] std::size_t operator()(const VisitKey& key) const noexcept
            {
                std::uint64_t value =
                    (static_cast<std::uint64_t>(key.Rva) << 32u) |
                    key.PreviousRva;
                value ^= static_cast<std::uint64_t>(key.Definition) << 16u;
                value ^= static_cast<std::uint64_t>(key.Region) << 24u;
                const auto mix = [&](const RaxDefinition& source)
                {
                    value ^= static_cast<std::uint64_t>(source.Kind) *
                        0xD6E8FEB86659FD93ULL;
                    value ^= static_cast<std::uint64_t>(source.InstructionRva) *
                        0x9E3779B185EBCA87ULL;
                    value ^= static_cast<std::uint64_t>(source.SourceRegister) *
                        0xA0761D6478BD642FULL;
                    value ^= static_cast<std::uint32_t>(source.Displacement) *
                        0xE7037ED1A0B428DBULL;
                    value ^= static_cast<std::uint64_t>(source.Absolute) *
                        0x8EBC6AF09C88C6E3ULL;
                    value ^= value >> 33u;
                    value *= 0xff51afd7ed558ccdULL;
                };
                mix(key.Rax);
                mix(key.Rdi);
                mix(key.Rbx);
                mix(key.RaxBase);
                mix(key.RaxBaseParent);
                mix(key.RaxBaseGrandparent);
                mix(key.RdiBase);
                mix(key.RbxBase);
                mix(key.Rsi);
                mix(key.Rdx);
                return static_cast<std::size_t>(value ^ (value >> 33u));
            }
        };
        std::unordered_map<VisitKey, std::uint8_t, VisitHash> bestDepth;
        bestDepth.reserve(instructionBudget);
        evidence.EntryAcceptedRegions = 1u;
        const auto unresolved = [&](std::uint32_t source,
            std::uint32_t target, std::uint32_t reason,
            const hde64s* instruction = nullptr,
            unsigned int length = 0u)
        {
            ++evidence.EntryUnresolvedExits;
            if (reason < evidence.EntryUnresolvedReasonCounts.size())
            {
                ++evidence.EntryUnresolvedReasonCounts[reason];
            }
            if (evidence.EntryFirstUnresolvedRva == 0u)
            {
                evidence.EntryFirstUnresolvedRva = target;
            }
            if (evidence.EntryUnresolvedSampleCount <
                evidence.EntryUnresolvedSamples.size())
            {
                auto& sample = evidence.EntryUnresolvedSamples[
                    evidence.EntryUnresolvedSampleCount++];
                sample.SourceRva = source;
                sample.TargetRva = target;
                sample.Reason = reason;
                if (instruction != nullptr)
                {
                    sample.Opcode = instruction->opcode;
                    sample.Opcode2 = instruction->opcode2;
                    sample.Modrm = instruction->modrm;
                    sample.Length = length;
                }
            }
        };
        const auto enqueue = [&](const EntryState& source,
            std::uint32_t target, std::uint8_t edgeKind)
        {
            const bool directBranch = edgeKind >= 2u;
            const auto& current = regions[source.RegionIndex].Range;
            if (target >= current.BeginRva && target < current.EndRva)
            {
                EntryState nextState = source;
                nextState.Rva = target;
                nextState.PreviousRva = directBranch ? 0u : source.Rva;
                nextState.PreviousLength = directBranch ? 0u :
                    static_cast<std::uint8_t>(target >= source.Rva ?
                        target - source.Rva : 0u);
                nextState.IncomingRva = source.Rva;
                nextState.IncomingKind = edgeKind;
                pending.push_back(nextState);
                return;
            }
            ++evidence.EntryCrossFunctionEdges;
            if (!directBranch)
            {
                ++evidence.EntryCrossRejected;
                unresolved(source.Rva, target, 8u);
                return;
            }
            if (target >= image.Size)
            {
                ++evidence.EntryCrossRejected;
                unresolved(source.Rva, target, 1u);
                return;
            }
            const SectionAtRva section = FindImageSection(image, target);
            if ((section.Characteristics & IMAGE_SCN_MEM_EXECUTE) == 0u ||
                !IsExecutableImageAddress(image, target))
            {
                ++evidence.EntryCrossRejected;
                unresolved(source.Rva, target, 2u);
                return;
            }
            std::size_t index = regions.size();
            for (std::size_t i = 0u; i < regions.size(); ++i)
            {
                const auto& range = regions[i].Range;
                if (target >= range.BeginRva && target < range.EndRva)
                {
                    index = i;
                    break;
                }
            }
            RuntimeFunctionView destination{};
            bool provisional = false;
            if (index == regions.size())
            {
                destination = ResolveRuntimeFunction(image,
                    image.Base + target);
                if (!destination)
                {
                    const ProvisionalCodeProbe probe =
                        ProbeUnwindlessBranchTarget(image, target, section);
                    destination = probe.Region;
                    provisional = static_cast<bool>(destination);
                }
                if (!destination || destination.Size() > 0x20000u ||
                    target < destination.BeginRva ||
                    target >= destination.EndRva)
                {
                    ++evidence.EntryCrossRejected;
                    unresolved(source.Rva, target, 3u);
                    return;
                }
            }
            if (source.Depth >= crossDepthLimit)
            {
                ++evidence.EntryCrossRejected;
                unresolved(source.Rva, target, 4u);
                return;
            }
            if (index == regions.size() && regions.size() >= regionLimit)
            {
                ++evidence.EntryCrossRejected;
                unresolved(source.Rva, target, 9u);
                return;
            }
            if (index == regions.size() && provisional &&
                evidence.EntryProvisionalRegions >= provisionalLimit)
            {
                ++evidence.EntryCrossRejected;
                unresolved(source.Rva, target, 10u);
                return;
            }
            if (index == regions.size())
            {
                index = regions.size();
                regions.push_back({destination, provisional});
                evidence.EntryAcceptedRegions =
                    static_cast<std::uint32_t>(regions.size());
                evidence.EntryProvisionalRegions += provisional ? 1u : 0u;
            }
            ++evidence.EntryCrossFollowed;
            EntryState nextState = source;
            nextState.Rva = target;
            nextState.RegionIndex = static_cast<std::uint8_t>(index);
            nextState.Depth = static_cast<std::uint8_t>(source.Depth + 1u);
            nextState.PreviousRva = 0u;
            nextState.PreviousLength = 0u;
            nextState.IncomingRva = source.Rva;
            nextState.IncomingKind = edgeKind;
            pending.push_back(nextState);
        };
        while (!pending.empty() &&
            evidence.EntryInstructions < instructionBudget &&
            evidence.EntryBranches < branchBudget)
        {
            EntryState state = pending.back();
            pending.pop_back();
            if (state.Rva == evidence.CallsiteRva)
            {
                switch (state.Definition)
                {
                case 0u: ++evidence.EntryCallWithoutLoad; break;
                case 1u: ++evidence.EntryCallWithLoad; break;
                case 2u: ++evidence.EntryCallWithOverwrite; break;
                default: ++evidence.EntryCallWithUnknownWrite; break;
                }
                continue;
            }
            const VisitKey key{state.Rva, state.PreviousRva,
                state.Definition, state.RegionIndex, state.Rax,
                state.Rdi, state.Rbx, state.RaxBase,
                state.RaxBaseParent, state.RaxBaseGrandparent,
                state.RdiBase, state.RbxBase, state.Rsi, state.Rdx};
            const auto [it, inserted] = bestDepth.try_emplace(key, state.Depth);
            if (!inserted)
            {
                if (state.Depth >= it->second)
                {
                    continue;
                }
                it->second = state.Depth;
                ++evidence.EntryLowerDepthRevisits;
            }
            const auto& region = regions[state.RegionIndex].Range;
            if (state.Rva < region.BeginRva ||
                state.Rva >= region.EndRva ||
                !IsExecutableImageAddress(image, state.Rva))
            {
                unresolved(state.Rva, state.Rva, 2u);
                continue;
            }
            std::array<std::uint8_t, 15> bytes{};
            const std::uint32_t available = CaptureReadableBytes(
                image, state.Rva, bytes);
            hde64s instruction{};
            const unsigned int length = available == 0u ? 0u :
                hde64_disasm(bytes.data(), &instruction);
            if (length == 0u || length > available ||
                length > region.EndRva - state.Rva ||
                (instruction.flags & F_ERROR) != 0u)
            {
                unresolved(state.Rva, state.Rva, 5u);
                continue;
            }
            ++evidence.EntryInstructions;
            if (instruction.opcode == 0xCCu ||
                (instruction.opcode == 0x0Fu &&
                 instruction.opcode2 == 0x0Bu))
            {
                bool verified = false;
                std::uint32_t predecessorOpcode{};
                unsigned int predecessorLength{};
                if (state.IncomingKind != 0u &&
                    state.IncomingRva < image.Size)
                {
                    std::array<std::uint8_t, 15> predecessorBytes{};
                    const auto readable = CaptureReadableBytes(image,
                        state.IncomingRva, predecessorBytes);
                    hde64s predecessor{};
                    predecessorLength = readable == 0u ? 0u :
                        hde64_disasm(predecessorBytes.data(), &predecessor);
                    if (predecessorLength != 0u &&
                        predecessorLength <= readable &&
                        (predecessor.flags & F_ERROR) == 0u)
                    {
                        predecessorOpcode = predecessor.opcode;
                        const auto following =
                            static_cast<std::uint64_t>(state.IncomingRva) +
                            predecessorLength;
                        if (state.IncomingKind == 1u)
                        {
                            verified = following == state.Rva;
                        }
                        else
                        {
                            const bool isNearBranch = predecessor.opcode == 0xE9u ||
                                (predecessor.opcode == 0x0Fu &&
                                 predecessor.opcode2 >= 0x80u &&
                                 predecessor.opcode2 <= 0x8Fu);
                            const bool shortBranch =
                                predecessor.opcode == 0xEBu ||
                                (predecessor.opcode >= 0x70u &&
                                 predecessor.opcode <= 0x7Fu) ||
                                (predecessor.opcode >= 0xE0u &&
                                 predecessor.opcode <= 0xE3u);
                            if (isNearBranch || shortBranch)
                            {
                                const std::int64_t displacement = isNearBranch ?
                                    static_cast<std::int32_t>(
                                        predecessor.imm.imm32) :
                                    static_cast<std::int8_t>(
                                        predecessor.imm.imm8);
                                verified = static_cast<std::int64_t>(following) +
                                    displacement == state.Rva;
                            }
                        }
                    }
                }
                if (!verified)
                {
                    ++evidence.EntryTrapUnverifiedEdges;
                }
                else if (state.IncomingKind == 1u)
                {
                    ++evidence.EntryTrapFallthroughEdges;
                }
                else
                {
                    ++evidence.EntryTrapDirectEdges;
                }
                if (evidence.EntryTrapSampleCount <
                    evidence.EntryTrapSamples.size())
                {
                    auto& sample = evidence.EntryTrapSamples[
                        evidence.EntryTrapSampleCount++];
                    sample.TrapRva = state.Rva;
                    sample.SourceRva = state.IncomingRva;
                    sample.EdgeKind = state.IncomingKind;
                    sample.SourceOpcode = predecessorOpcode;
                    sample.SourceLength = predecessorLength;
                    sample.EdgeVerified = verified;
                }
                unresolved(state.Rva, state.Rva, 11u,
                    &instruction, length);
                continue;
            }
            if (state.Rva == evidence.StackLoadRva)
            {
                state.Definition = 1u;
            }
            else if (state.Definition == 1u)
            {
                switch (InspectStackRegisterEffect(instruction,
                    evidence.ObjectRegister))
                {
                case StackRegisterEffect::Overwritten:
                    state.Definition = 2u;
                    break;
                case StackRegisterEffect::Unknown:
                    state.Definition = 3u;
                    break;
                default:
                    break;
                }
            }
            const RaxDefinition previousRax = state.Rax;
            const RaxDefinition previousRdi = state.Rdi;
            const RaxDefinition previousRbx = state.Rbx;
            const RaxDefinition previousRsi = state.Rsi;
            const RaxDefinition previousRdx = state.Rdx;
            const RaxDefinition previousRaxBase = state.RaxBase;
            const RaxDefinition previousRaxBaseParent = state.RaxBaseParent;
            const RaxDefinition previousRdiBase = state.RdiBase;
            const RaxDefinition previousRbxBase = state.RbxBase;
            AdvanceRaxDefinition(image, state.Rva, length,
                instruction, state.Rax);
            AdvanceNonvolatileDefinition(image, state.Rva, length,
                instruction, state.Rdi, 7u);
            AdvanceNonvolatileDefinition(image, state.Rva, length,
                instruction, state.Rbx, 3u);
            AdvanceNonvolatileDefinition(image, state.Rva, length,
                instruction, state.Rsi, 6u);
            if (instruction.opcode == 0xE8u ||
                (instruction.opcode == 0xFFu &&
                 (instruction.flags & F_MODRM) != 0u &&
                 (instruction.modrm_reg == 2u ||
                  instruction.modrm_reg == 3u)))
            {
                state.Rdx = {7u, state.Rva};
            }
            else
            {
                AdvanceNonvolatileDefinition(image, state.Rva, length,
                    instruction, state.Rdx, 2u);
            }
            if (state.Rax.InstructionRva == state.Rva)
            {
                if (state.Rax.SourceRegister == 7u &&
                    (state.Rax.Kind == 3u || state.Rax.Kind == 4u))
                {
                    state.RaxBase = previousRdi;
                    state.RaxBaseParent = previousRdiBase;
                    state.RaxBaseGrandparent = {};
                }
                else if (state.Rax.SourceRegister == 0u &&
                    (state.Rax.Kind == 3u || state.Rax.Kind == 4u))
                {
                    state.RaxBase = previousRax;
                    state.RaxBaseParent = previousRaxBase;
                    state.RaxBaseGrandparent = previousRaxBaseParent;
                }
                else if (state.Rax.SourceRegister == 3u &&
                    (state.Rax.Kind == 3u || state.Rax.Kind == 4u))
                {
                    state.RaxBase = previousRbx;
                    state.RaxBaseParent = previousRbxBase;
                    state.RaxBaseGrandparent = {};
                }
                else
                {
                    state.RaxBase = {};
                    state.RaxBaseParent = {};
                    state.RaxBaseGrandparent = {};
                }
            }
            if (state.Rdi.InstructionRva == state.Rva)
            {
                state.RdiBase = (state.Rdi.Kind == 3u ||
                    state.Rdi.Kind == 4u) ?
                    state.Rdi.SourceRegister == 0u ? previousRax :
                    state.Rdi.SourceRegister == 3u ? previousRbx :
                    state.Rdi.SourceRegister == 7u ? previousRdi :
                    state.Rdi.SourceRegister == 6u ? previousRsi :
                    RaxDefinition{} : RaxDefinition{};
            }
            if (state.Rbx.InstructionRva == state.Rva)
            {
                state.RbxBase = (state.Rbx.Kind == 3u ||
                    state.Rbx.Kind == 4u) ?
                    state.Rbx.SourceRegister == 0u ? previousRax :
                    state.Rbx.SourceRegister == 7u ? previousRdi :
                    state.Rbx.SourceRegister == 3u ? previousRbx :
                    state.Rbx.SourceRegister == 2u ? previousRdx :
                    RaxDefinition{} : RaxDefinition{};
            }
            const std::uint32_t next = state.Rva + length;
            const bool nearJump = instruction.opcode == 0xE9u;
            const bool shortJump = instruction.opcode == 0xEBu;
            const bool shortConditional = instruction.opcode >= 0x70u &&
                instruction.opcode <= 0x7Fu;
            const bool nearConditional = instruction.opcode == 0x0Fu &&
                instruction.opcode2 >= 0x80u &&
                instruction.opcode2 <= 0x8Fu;
            const bool loopConditional = instruction.opcode >= 0xE0u &&
                instruction.opcode <= 0xE3u;
            if (nearJump || shortJump ||
                shortConditional || nearConditional || loopConditional)
            {
                ++evidence.EntryBranches;
                const std::int64_t displacement =
                    nearJump || nearConditional
                    ? static_cast<std::int32_t>(instruction.imm.imm32)
                    : static_cast<std::int8_t>(instruction.imm.imm8);
                const std::int64_t target =
                    static_cast<std::int64_t>(next) + displacement;
                if (target < 0 || target > UINT32_MAX)
                {
                    unresolved(state.Rva, state.Rva, 1u);
                }
                else
                {
                    enqueue(state, static_cast<std::uint32_t>(target),
                        shortConditional || nearConditional || loopConditional ?
                            std::uint8_t{3u} : std::uint8_t{2u});
                }
                if (shortConditional || nearConditional || loopConditional)
                {
                    if (loopConditional)
                    {
                        ++evidence.EntryLoopBranches;
                    }
                    enqueue(state, next, 1u);
                }
                continue;
            }
            if (instruction.opcode == 0xC2u ||
                instruction.opcode == 0xC3u ||
                instruction.opcode == 0xCAu ||
                instruction.opcode == 0xCBu)
            {
                continue;
            }
            if (instruction.opcode == 0xFFu &&
                (instruction.flags & F_MODRM) != 0u &&
                (instruction.modrm_reg == 4u ||
                 instruction.modrm_reg == 5u))
            {
                ++evidence.EntryIndirectExits;
                auto probe = instruction.modrm_reg == 4u ?
                    ProbeStaticIndirectJump(image, state.Rva, length,
                        instruction, state.PreviousRva,
                        state.PreviousLength) : IndirectJumpProbe{};
                if (probe.Status == 0u && instruction.modrm_reg == 4u)
                {
                    static_cast<void>(ResolveRaxDefinedJump(
                        image, instruction, state.Rax, probe));
                }
                if (evidence.EntryIndirectSampleCount <
                    evidence.EntryIndirectSamples.size())
                {
                    auto& sample = evidence.EntryIndirectSamples[
                        evidence.EntryIndirectSampleCount++];
                    sample.SourceRva = state.Rva;
                    sample.PreviousRva = state.PreviousRva;
                    sample.Mode = probe.Mode;
                    sample.BaseRegister = probe.Register;
                    sample.Displacement = probe.Displacement;
                    sample.SlotRva = probe.SlotRva;
                    sample.TargetRva = probe.TargetRva;
                    sample.Status = probe.Status;
                    if (probe.Mode == 2u &&
                        probe.Register == 0u &&
                        probe.Displacement == 0x38 &&
                        state.Rax.Kind == 3u &&
                        state.Rax.SourceRegister == 7u &&
                        state.Rax.Displacement == 0x368 &&
                        state.RaxBase.Kind == 3u &&
                        state.RaxBase.Displacement == 0x10A0)
                    {
                        sample.DispatchShape = 1u;
                    }
                    else if (probe.Mode == 1u &&
                        probe.Register == 0u &&
                        state.Rax.Kind == 3u &&
                        state.Rax.SourceRegister == 0u &&
                        state.Rax.Displacement == 0x30 &&
                        state.RaxBase.Kind == 3u &&
                        state.RaxBase.SourceRegister == 3u &&
                        state.RaxBase.Displacement == 0)
                    {
                        sample.DispatchShape = 2u;
                    }
                    sample.StackObjectLink =
                        state.RaxBase.InstructionRva ==
                            evidence.StackLoadRva ||
                        state.RaxBaseParent.InstructionRva ==
                            evidence.StackLoadRva ? 1u : 0u;
                    sample.Opcode = instruction.opcode;
                    sample.Modrm = instruction.modrm;
                    sample.Rex = instruction.rex;
                    sample.Sib = instruction.sib;
                    sample.Length = length;
                    sample.RaxSourceKind = state.Rax.Kind;
                    sample.RaxDefinitionRva = state.Rax.InstructionRva;
                    sample.RaxSourceRegister = state.Rax.SourceRegister;
                    sample.RaxSourceDisplacement = state.Rax.Displacement;
                    const auto captureOrigin = [&](
                        const RaxDefinition& source,
                        CEventGenerator::EnhancedStackPathEvidence::
                            IndirectJumpDiagnostic::OriginDiagnostic& target)
                    {
                        target.Kind = source.Kind;
                        target.DefinitionRva = source.InstructionRva;
                        target.SourceRegister = source.SourceRegister;
                        target.Displacement = source.Displacement;
                        const auto base =
                            reinterpret_cast<std::uintptr_t>(image.Base);
                        if (source.Absolute >= base &&
                            source.Absolute - base < image.Size)
                        {
                            target.KnownRva = static_cast<std::uint32_t>(
                                source.Absolute - base);
                        }
                        if (source.InstructionRva >= image.Size ||
                            source.InstructionRva == 0u)
                        {
                            return;
                        }
                        std::array<std::uint8_t, 15> bytes{};
                        const auto available = CaptureReadableBytes(
                            image, source.InstructionRva, bytes);
                        hde64s definitionInstruction{};
                        const unsigned int decoded = available == 0u ? 0u :
                            hde64_disasm(bytes.data(), &definitionInstruction);
                        if (decoded == 0u || decoded > available ||
                            (definitionInstruction.flags & F_ERROR) != 0u)
                        {
                            return;
                        }
                        target.Opcode = definitionInstruction.opcode;
                        target.Modrm = definitionInstruction.modrm;
                        target.Length = decoded;
                        target.ByteCount = std::min<std::uint32_t>(decoded,
                            static_cast<std::uint32_t>(target.Bytes.size()));
                        std::copy_n(bytes.begin(), target.ByteCount,
                            target.Bytes.begin());
                    };
                    captureOrigin(state.RaxBase, sample.BaseOrigin);
                    captureOrigin(state.RaxBaseParent, sample.ParentOrigin);
                    captureOrigin(state.RaxBaseGrandparent,
                        sample.GrandparentOrigin);
                    if (state.Rax.InstructionRva < image.Size)
                    {
                        std::array<std::uint8_t, 15> definitionBytes{};
                        const auto available = CaptureReadableBytes(
                            image, state.Rax.InstructionRva, definitionBytes);
                        hde64s definitionInstruction{};
                        const auto definitionLength = available == 0u ? 0u :
                            hde64_disasm(definitionBytes.data(),
                                &definitionInstruction);
                        if (definitionLength != 0u &&
                            definitionLength <= available &&
                            (definitionInstruction.flags & F_ERROR) == 0u)
                        {
                            sample.RaxDefinitionOpcode = definitionInstruction.opcode;
                            sample.RaxDefinitionOpcode2 = definitionInstruction.opcode2;
                            sample.RaxDefinitionModrm = definitionInstruction.modrm;
                            sample.RaxDefinitionLength = definitionLength;
                            sample.RaxDefinitionByteCount =
                                std::min<std::uint32_t>(definitionLength,
                                    static_cast<std::uint32_t>(
                                        sample.RaxDefinitionBytes.size()));
                            std::copy_n(definitionBytes.begin(),
                                sample.RaxDefinitionByteCount,
                                sample.RaxDefinitionBytes.begin());
                        }
                    }
                    const auto base = reinterpret_cast<std::uintptr_t>(image.Base);
                    if (state.Rax.Absolute >= base &&
                        state.Rax.Absolute - base < image.Size)
                    {
                        sample.RaxKnownRva = static_cast<std::uint32_t>(
                            state.Rax.Absolute - base);
                    }
                }
                if (probe.Status != 0u)
                {
                    ++evidence.EntryIndirectStaticCandidates;
                    enqueue(state, probe.TargetRva, 2u);
                }
                else
                {
                    unresolved(state.Rva, state.Rva, 6u,
                        &instruction, length);
                }
                continue;
            }
            enqueue(state, next, 1u);
        }
        evidence.EntryBudgetExceeded = !pending.empty() ||
            evidence.EntryInstructions >= instructionBudget ||
            evidence.EntryBranches >= branchBudget;
        if (evidence.EntryBudgetExceeded)
        {
            unresolved(pending.empty() ? rootRegion.BeginRva :
                pending.back().Rva,
                pending.empty() ? rootRegion.BeginRva :
                pending.back().Rva, 7u);
        }
    }

    void RecordEnhancedStackPathEvidence(ImageView image,
        CEventGenerator::EventLocatorDiagnostics& diagnostics)
    {
        using namespace CEventGenerator;
        diagnostics.EnhancedStackPath = {};
        const std::uint32_t anchor = diagnostics.CEventStackAnchorRva;
        const std::uint32_t expectedOffset = diagnostics.CEventStackOffset;
        if (anchor == 0u || expectedOffset == 0u || anchor >= image.Size)
        {
            return;
        }
        std::array<std::uint8_t, 15> bytes{};
        const std::uint32_t available = CaptureReadableBytes(image, anchor, bytes);
        hde64s load{};
        const unsigned int length = hde64_disasm(bytes.data(), &load);
        if (length == 0u || length > available ||
            (load.flags & F_ERROR) != 0u || load.opcode != 0x8Bu ||
            load.rex_w == 0u || (load.flags & (F_MODRM | F_SIB)) !=
                (F_MODRM | F_SIB) || load.modrm_mod != 2u ||
            load.modrm_rm != 4u || load.sib_scale != 3u ||
            load.sib_index == 4u ||
            static_cast<std::uint32_t>(load.disp.disp32) != expectedOffset)
        {
            return;
        }
        const std::uint32_t objectRegister = GeneralRegister(
            load.modrm_reg, load.rex_r != 0u);
        EnhancedStackPathEvidence evidence{};
        evidence.StackLoadRva = anchor;
        evidence.StackDisplacement = expectedOffset;
        evidence.SourceBaseRegister = GeneralRegister(
            load.sib_base, load.rex_b != 0u);
        evidence.SourceIndexRegister = GeneralRegister(
            load.sib_index, load.rex_x != 0u);
        evidence.ObjectRegister = objectRegister;
        evidence.Flags = 1u;
        for (std::uint32_t index = 0u;
            index < diagnostics.EnhancedVirtualCallDiagnosticCount &&
            index < diagnostics.EnhancedVirtualCalls.size(); ++index)
        {
            const auto& call = diagnostics.EnhancedVirtualCalls[index];
            if (call.OwnerRva != diagnostics.CEventCountFunctionRva ||
                call.ObjectRegister != objectRegister ||
                call.SlotOffset != diagnostics.EventTypeVFuncOffset ||
                (call.ProvenanceFlags & 4u) == 0u ||
                call.CallsiteRva <= anchor ||
                static_cast<std::uint64_t>(call.CallsiteRva) >
                    static_cast<std::uint64_t>(anchor) + 64u)
            {
                continue;
            }
            evidence.CallsiteRva = call.CallsiteRva;
            evidence.SlotOffset = call.SlotOffset;
            evidence.Flags |= 2u;
            std::uint32_t cursor = anchor + length;
            bool cleanPath = true;
            std::uint32_t steps{};
            for (; cursor < call.CallsiteRva && steps < 16u; ++steps)
            {
                std::array<std::uint8_t, 15> instructionBytes{};
                const auto readable = CaptureReadableBytes(image,
                    cursor, instructionBytes);
                hde64s instruction{};
                const unsigned int decoded = hde64_disasm(
                    instructionBytes.data(), &instruction);
                if (decoded == 0u || decoded > readable ||
                    decoded > call.CallsiteRva - cursor ||
                    (instruction.flags & F_ERROR) != 0u)
                {
                    cleanPath = false;
                    break;
                }
                const bool testedObject = instruction.opcode == 0x85u &&
                    (instruction.flags & F_MODRM) != 0u &&
                    instruction.modrm_mod == 3u &&
                    GeneralRegister(instruction.modrm_reg,
                        instruction.rex_r != 0u) == objectRegister &&
                    GeneralRegister(instruction.modrm_rm,
                        instruction.rex_b != 0u) == objectRegister;
                const bool conditional =
                    (instruction.opcode >= 0x70u &&
                        instruction.opcode <= 0x7Fu) ||
                    (instruction.opcode == 0x0Fu &&
                        instruction.opcode2 >= 0x80u &&
                        instruction.opcode2 <= 0x8Fu);
                const bool vtableLoad = instruction.opcode == 0x8Bu &&
                    instruction.rex_w != 0u &&
                    (instruction.flags & F_MODRM) != 0u &&
                    instruction.modrm_mod == 0u &&
                    instruction.modrm_rm != 4u &&
                    GeneralRegister(instruction.modrm_rm,
                        instruction.rex_b != 0u) == objectRegister &&
                    GeneralRegister(instruction.modrm_reg,
                        instruction.rex_r != 0u) == call.BaseRegister;
                std::uint32_t destination{}, source{};
                const bool thisMove = DecodeRegisterMove(instruction,
                    destination, source) && destination == 1u &&
                    source == objectRegister;
                if (!testedObject && !conditional && !vtableLoad && !thisMove)
                {
                    cleanPath = false;
                    break;
                }
                if (conditional)
                {
                    ++evidence.ConditionalBranches;
                    if (evidence.FirstBranchRva == 0u)
                    {
                        const std::int64_t displacement =
                            instruction.opcode == 0x0Fu
                            ? static_cast<std::int32_t>(instruction.imm.imm32)
                            : static_cast<std::int8_t>(instruction.imm.imm8);
                        const std::int64_t fallthrough =
                            static_cast<std::int64_t>(cursor) + decoded;
                        const std::int64_t target = fallthrough + displacement;
                        if (target >= 0 &&
                            static_cast<std::uint64_t>(target) <= UINT32_MAX)
                        {
                            evidence.FirstBranchRva = cursor;
                            evidence.FirstBranchTargetRva =
                                static_cast<std::uint32_t>(target);
                            evidence.FirstBranchFallthroughRva =
                                static_cast<std::uint32_t>(fallthrough);
                            evidence.FirstBranchLeavesLocalPath =
                                target < anchor ||
                                target >= call.CallsiteRva;
                        }
                    }
                }
                cursor += decoded;
            }
            if (cleanPath && cursor == call.CallsiteRva)
            {
                evidence.Flags |= 4u;
            }
            break;
        }
        TraceEnhancedStackLoopBack(image, evidence);
        TraceEnhancedStackEntryFlow(image, evidence);
        evidence.StaticStructureSignals =
            (evidence.StackLoadRva != 0u ? 1u : 0u) |
            ((evidence.Flags & 4u) != 0u ? 2u : 0u) |
            (evidence.LoopBackReachesStackLoad != 0u ? 4u : 0u) |
            (evidence.EntryCallWithLoad != 0u ? 8u : 0u);
        for (std::uint32_t index = 0u;
            index < evidence.EntryIndirectSampleCount; ++index)
        {
            if (evidence.EntryIndirectSamples[index].DispatchShape != 0u)
            {
                evidence.StaticStructureSignals |= 16u;
                break;
            }
        }
        evidence.AdmissionBlockers =
            (!evidence.EntryAnalyzed || evidence.EntryBudgetExceeded ||
             evidence.EntryUnresolvedExits != 0u ||
             evidence.EntryCallWithLoad == 0u ||
             evidence.EntryCallWithoutLoad != 0u ||
             evidence.EntryCallWithOverwrite != 0u ||
             evidence.EntryCallWithUnknownWrite != 0u ? 1u : 0u) |
            2u | 4u | 8u | 16u | 32u | 64u;
        diagnostics.EnhancedStackPath = evidence;
    }

    void RecordEnhancedControlFlowEvidence(ImageView image,
        CEventGenerator::EventLocatorDiagnostics& diagnostics)
    {
        TraceEnhancedControlFlow(image,
            diagnostics.EnhancedDecoderStopOwnerRva,
            diagnostics.EnhancedDecoderStopRva,
            diagnostics.EnhancedControlFlow, diagnostics);
        TraceEnhancedControlFlow(image,
            diagnostics.CEventCountFunctionRva, 0u,
            diagnostics.EnhancedCountControlFlow, diagnostics);
        TraceEnhancedControlFlow(image,
            diagnostics.EventTypeFunctionRva, 0u,
            diagnostics.EnhancedTypeControlFlow, diagnostics);
    }

    void RecordEnhancedCallEvidence(
        ImageView image,
        const EnhancedReceiverTriplet& selected,
        std::uint32_t countFunctionRva,
        std::uint32_t typeFunctionRva,
        CEventGenerator::EventLocatorDiagnostics& diagnostics)
    {
        using namespace CEventGenerator;
        if (selected.UniqueGroups != 1 || selected.Callee == nullptr)
        {
            return;
        }
        const std::uint32_t sharedRva = static_cast<std::uint32_t>(
            selected.Callee - image.Base);
        struct RankedEvidence final
        {
            EnhancedCallEvidence Data{};
            std::uint32_t Priority{};
        };
        std::vector<RankedEvidence> collected;
        for (std::size_t ownerIndex = 0; ownerIndex < 4; ++ownerIndex)
        {
            const std::byte* owner = ownerIndex < 3
                ? selected.Entries[ownerIndex] : selected.Callee;
            const RuntimeFunctionView function = ResolveRuntimeFunction(image, owner);
            if (!function || function.Size() > 0x20000u)
            {
                continue;
            }
            const std::uint32_t begin = function.BeginRva;
            std::uint32_t cursor = begin;
            while (cursor < function.EndRva)
            {
                std::array<std::uint8_t, 15> instructionBytes{};
                const std::uint32_t available = std::min<std::uint32_t>(
                    15u, function.EndRva - cursor);
                std::memcpy(instructionBytes.data(), image.Base + cursor, available);
                hde64s decoded{};
                const unsigned int length = hde64_disasm(
                    instructionBytes.data(), &decoded);
                if (length == 0 || length > available ||
                    (decoded.flags & F_ERROR) != 0)
                {
                    ++diagnostics.EnhancedCallDecoderStopCount;
                    break;
                }
                const bool direct = decoded.opcode == 0xE8u && length == 5u;
                const bool indirect = decoded.opcode == 0xFFu &&
                    (decoded.flags & F_MODRM) != 0 &&
                    decoded.modrm_reg == 2u;
                if (direct || indirect)
                {
                    EnhancedCallEvidence evidence{};
                    evidence.OwnerRva = begin;
                    evidence.CallsiteRva = cursor;
                    evidence.Length = length;
                    std::copy_n(instructionBytes.begin(), length,
                        evidence.Bytes.begin());
                    std::uint32_t priority = 9;
                    if (direct)
                    {
                        std::int32_t displacement{};
                        std::memcpy(&displacement,
                            instructionBytes.data() + 1, sizeof(displacement));
                        const std::int64_t target = std::int64_t(cursor) +
                            5 + displacement;
                        if (target >= 0 &&
                            std::uint64_t(target) < image.Size)
                        {
                            evidence.TargetRva = static_cast<std::uint32_t>(target);
                        }
                        evidence.Kind = 6;
                        if (evidence.TargetRva == sharedRva)
                        {
                            evidence.Kind = 1;
                            priority = 0;
                        }
                        else if (evidence.TargetRva == typeFunctionRva)
                        {
                            evidence.Kind = 2;
                            priority = 1;
                        }
                        else if (evidence.TargetRva == countFunctionRva)
                        {
                            evidence.Kind = 3;
                            priority = 1;
                        }
                    }
                    else
                    {
                        evidence.Kind = decoded.modrm_mod == 3u ? 5u : 4u;
                        priority = evidence.Kind == 4u ? 2u : 3u;
                        if (decoded.modrm_mod != 3u &&
                            decoded.modrm_rm != 4u &&
                            (decoded.flags & F_DISP8))
                        {
                            evidence.OperandOffset = static_cast<std::uint32_t>(
                                static_cast<std::int8_t>(decoded.disp.disp8));
                        }
                        else if (decoded.modrm_mod == 2u &&
                            decoded.modrm_rm != 4u &&
                            (decoded.flags & F_DISP32))
                        {
                            evidence.OperandOffset = decoded.disp.disp32;
                        }
                        evidence.BaseRegister = decoded.modrm_rm |
                            (decoded.rex_b ? 8u : 0u);
                    }
                    collected.push_back({evidence, priority});
                }
                cursor += length;
            }
        }
        diagnostics.EnhancedCallEvidenceCandidateCount =
            static_cast<std::uint32_t>(collected.size());
        std::sort(collected.begin(), collected.end(),
            [](const RankedEvidence& left, const RankedEvidence& right)
            {
                if (left.Priority != right.Priority)
                {
                    return left.Priority < right.Priority;
                }
                if (left.Data.OwnerRva != right.Data.OwnerRva)
                {
                    return left.Data.OwnerRva < right.Data.OwnerRva;
                }
                return left.Data.CallsiteRva < right.Data.CallsiteRva;
            });
        for (const auto& entry : collected)
        {
            if (diagnostics.EnhancedCallEvidenceDiagnosticCount >=
                diagnostics.EnhancedCallEvidenceSamples.size())
            {
                break;
            }
            diagnostics.EnhancedCallEvidenceSamples[
                diagnostics.EnhancedCallEvidenceDiagnosticCount++] = entry.Data;
        }
    }

    void RecordEnhancedAnchorCallEvidence(
        ImageView image,
        std::uint32_t countFunctionRva,
        std::uint32_t typeFunctionRva,
        CEventGenerator::EventLocatorDiagnostics& diagnostics)
    {
        std::vector<std::uint32_t> countCallers;
        std::vector<std::uint32_t> typeCallers;
        for (std::size_t offset = 0; offset + 5 <= image.Size; ++offset)
        {
            const std::byte* callsite = image.Base + offset;
            if (ByteAt(callsite) != 0xE8u)
            {
                continue;
            }
            const std::int64_t callee = static_cast<std::int64_t>(offset) +
                5 + ReadInt32(callsite + 1);
            const bool countTarget = callee == countFunctionRva;
            const bool typeTarget = callee == typeFunctionRva;
            if (!countTarget && !typeTarget)
            {
                continue;
            }
            const RuntimeFunctionView function =
                ResolveRuntimeFunction(image, callsite);
            if (!function)
            {
                continue;
            }
            auto& callers = countTarget ? countCallers : typeCallers;
            if (std::find(callers.begin(), callers.end(),
                function.BeginRva) == callers.end())
            {
                callers.push_back(function.BeginRva);
            }
            if (countTarget)
            {
                ++diagnostics.DirectCountCallerCount;
            }
            else
            {
                ++diagnostics.DirectEventTypeCallerCount;
            }
            ++diagnostics.DispatchCallsiteCandidateCount;
            if (diagnostics.DispatchCallsiteDiagnosticCount <
                diagnostics.DispatchCallsites.size())
            {
                auto& record = diagnostics.DispatchCallsites[
                    diagnostics.DispatchCallsiteDiagnosticCount++];
                record.WalkerRva = function.BeginRva;
                record.CallsiteRva = static_cast<std::uint32_t>(offset);
                record.CalleeRva = countTarget
                    ? countFunctionRva : typeFunctionRva;
                record.Relations = countTarget ? 1u : 2u;
            }
        }
        for (const std::uint32_t entry : countCallers)
        {
            if (std::find(typeCallers.begin(), typeCallers.end(), entry) ==
                typeCallers.end())
            {
                continue;
            }
            ++diagnostics.RelatedEventWalkerCandidateCount;
            if (diagnostics.RelatedEventWalkerCandidateCount == 1)
            {
                diagnostics.EventWalkerCandidateRva = entry;
                const RuntimeFunctionView function =
                    ResolveRuntimeFunction(image, image.Base + entry);
                diagnostics.EventWalkerCandidateSize = function.Size();
            }
        }
        diagnostics.EventWalkerCandidateCount =
            diagnostics.RelatedEventWalkerCandidateCount;
    }

    [[nodiscard]] void* FindLegacyDamageProcess(ImageView image)
    {
        const auto current = FindAll(image, DamageProcessPatternCurrent, 2);
        const auto legacy = FindAll(image, DamageProcessPatternLegacy, 2);
        if (current.size() == 1 && legacy.empty())
        {
            constexpr std::ptrdiff_t EntryOffset = -0x56;
            return const_cast<std::byte*>(current[0] + EntryOffset);
        }
        if (legacy.size() == 1 && current.empty())
        {
            constexpr std::ptrdiff_t EntryOffset = -0x5D;
            return const_cast<std::byte*>(legacy[0] + EntryOffset);
        }
        return nullptr;
    }

    [[nodiscard]] std::uint32_t GetGameBuild(
        const std::array<wchar_t, MAX_PATH>& path) noexcept
    {
        if (path[0] == L'\0')
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

    [[nodiscard]] CEventGenerator::EventLocatorResult LocateLegacy(
        ImageView image,
        CEventGenerator::EventLocatorResult result)
    {
        using namespace CEventGenerator;

        const auto pedMatches = FindAll(image, PedEventPattern, 4);
        const auto globalMatches = FindAll(image, GlobalEventPattern, 2);
        const auto damageMatches = FindAll(image, DamageEventPattern, 2);
        result.Diagnostics.PedMatchCount =
            static_cast<std::uint32_t>(pedMatches.size());
        result.Diagnostics.GlobalMatchCount =
            static_cast<std::uint32_t>(globalMatches.size());
        result.Diagnostics.DamageMatchCount =
            static_cast<std::uint32_t>(damageMatches.size());

        if (pedMatches.size() != 2)
        {
            result.Diagnostics.Failure = LocatorFailure::PedMatchCount;
            return result;
        }
        if (globalMatches.size() != 1)
        {
            result.Diagnostics.Failure = LocatorFailure::GlobalMatchCount;
            return result;
        }
        if (damageMatches.size() != 1)
        {
            result.Diagnostics.Failure = LocatorFailure::DamageMatchCount;
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
        result.Targets.DamageProcessFunction = FindLegacyDamageProcess(image);
        const std::uint32_t legacySlotShift =
            result.Targets.GameBuild >= 2802 ? 6u : 0u;
        result.Targets.EventIdVFuncOffset =
            (3u + legacySlotShift) * static_cast<std::uint32_t>(sizeof(void*));
        result.Targets.EventArgumentsVFuncOffset =
            (6u + legacySlotShift) * static_cast<std::uint32_t>(sizeof(void*));
        result.Targets.EventEntityVFuncOffset =
            (25u + legacySlotShift) * static_cast<std::uint32_t>(sizeof(void*));
        result.Diagnostics.EventTypeVFuncOffset =
            result.Targets.EventIdVFuncOffset;

        if (result.Targets.Functions[0] == result.Targets.Functions[1] ||
            result.Targets.Functions[0] == result.Targets.Functions[2] ||
            result.Targets.Functions[1] == result.Targets.Functions[2] ||
            result.Targets.DamageFunction == nullptr ||
            result.Targets.DamageFunction == result.Targets.Functions[0] ||
            result.Targets.DamageFunction == result.Targets.Functions[1] ||
            result.Targets.DamageFunction == result.Targets.Functions[2])
        {
            result.Diagnostics.Failure = LocatorFailure::TargetInvalid;
            return result;
        }

        if (result.Targets.DamageProcessFunction == result.Targets.DamageFunction ||
            result.Targets.DamageProcessFunction == result.Targets.Functions[0] ||
            result.Targets.DamageProcessFunction == result.Targets.Functions[1] ||
            result.Targets.DamageProcessFunction == result.Targets.Functions[2])
        {
            result.Targets.DamageProcessFunction = nullptr;
        }

        result.Diagnostics.Failure = LocatorFailure::None;
        result.State = Status::Ready;
        return result;
    }

    [[nodiscard]] const std::byte* ResolveFiveMReceiverEntry(
        ImageView image,
        const std::byte* match,
        std::size_t entryBackOffset,
        std::size_t patternLength) noexcept
    {
        if (!Contains(image, match, patternLength))
        {
            return nullptr;
        }
        const std::size_t rva = static_cast<std::size_t>(match - image.Base);
        if (rva < entryBackOffset)
        {
            return nullptr;
        }
        const auto frame = ResolveRuntimeFunction(image, match);
        if (!frame || frame.BeginRva != rva - entryBackOffset ||
            frame.EndRva < rva + patternLength)
        {
            return nullptr;
        }
        const auto* entry = image.Base + frame.BeginRva;
        MEMORY_BASIC_INFORMATION memory{};
        if (VirtualQuery(entry, &memory, sizeof(memory)) != sizeof(memory) ||
            memory.State != MEM_COMMIT ||
            (memory.Protect & PAGE_GUARD) != 0)
        {
            return nullptr;
        }
        const DWORD pageProtection = memory.Protect & 0xFFu;
        if (pageProtection != PAGE_EXECUTE &&
            pageProtection != PAGE_EXECUTE_READ &&
            pageProtection != PAGE_EXECUTE_READWRITE &&
            pageProtection != PAGE_EXECUTE_WRITECOPY)
        {
            return nullptr;
        }
        return entry;
    }

    [[nodiscard]] bool TryLocateEnhancedFiveMReceivers(
        ImageView image,
        CEventGenerator::EventLocatorResult& result)
    {
        using namespace CEventGenerator;
        const auto ped = FindAllExecutable(image, PedEventPattern, 3);
        const auto global = FindAllExecutable(image, GlobalEventPattern, 2);
        const auto damage = FindAllExecutable(image, DamageEventPattern, 2);
        auto& diagnostic = result.Diagnostics;
        diagnostic.FiveMPedSignatureCount =
            static_cast<std::uint32_t>(ped.size());
        diagnostic.FiveMGlobalSignatureCount =
            static_cast<std::uint32_t>(global.size());
        diagnostic.FiveMDamageSignatureCount =
            static_cast<std::uint32_t>(damage.size());
        diagnostic.FiveMReceiverPathStatus = 1;
        if (ped.size() != 2 || global.size() != 1 || damage.size() != 1)
        {
            return false;
        }

        constexpr std::size_t EventEntryBackOffset = 0x36;
        constexpr std::size_t DamageEntryBackOffset = 0x1F;
        std::array<const std::byte*, 3> functions{
            ResolveFiveMReceiverEntry(image, ped[0], EventEntryBackOffset,
                PedEventPattern.size()),
            ResolveFiveMReceiverEntry(image, ped[1], EventEntryBackOffset,
                PedEventPattern.size()),
            ResolveFiveMReceiverEntry(image, global[0], EventEntryBackOffset,
                GlobalEventPattern.size())
        };
        const std::byte* damageEntry = ResolveFiveMReceiverEntry(
            image, damage[0], DamageEntryBackOffset, DamageEventPattern.size());
        diagnostic.FiveMReceiverPathStatus = 2;
        if (std::any_of(functions.begin(), functions.end(),
                [](const std::byte* function) { return function == nullptr; }) ||
            damageEntry == nullptr ||
            functions[0] == functions[1] || functions[0] == functions[2] ||
            functions[1] == functions[2] ||
            std::find(functions.begin(), functions.end(), damageEntry) !=
                functions.end())
        {
            return false;
        }

        constexpr std::uint32_t FiveMIdSlot = 0x48;
        const auto typeAnchors = FindAllExecutable(image, EnhancedEventTypePattern, 2);
        diagnostic.EventTypeAnchorCount =
            static_cast<std::uint32_t>(typeAnchors.size());
        diagnostic.FiveMReceiverPathStatus = 3;
        if (typeAnchors.size() != 1 ||
            !Contains(image, typeAnchors[0] +
                EnhancedEventTypePattern.size(), sizeof(std::uint8_t)) ||
            !ResolveRuntimeFunction(image, typeAnchors[0]) ||
            ByteAt(typeAnchors[0] + EnhancedEventTypePattern.size()) !=
                FiveMIdSlot)
        {
            return false;
        }

        for (std::size_t index = 0; index < functions.size(); ++index)
        {
            result.Targets.Functions[index] =
                const_cast<std::byte*>(functions[index]);
        }
        result.Targets.DamageFunction = const_cast<std::byte*>(damageEntry);
        result.Targets.DamageProcessFunction = nullptr;
        result.Targets.EventIdVFuncOffset = FiveMIdSlot;
        result.Targets.EventArgumentsVFuncOffset = 0x60;
        result.Targets.EventEntityVFuncOffset = 0xF8;
        diagnostic.PedMatchCount = 2;
        diagnostic.GlobalMatchCount = 1;
        diagnostic.DamageMatchCount = 1;
        diagnostic.EventTypeVFuncOffset = FiveMIdSlot;
        diagnostic.FiveMReceiverPathStatus = 4;
        diagnostic.Failure = LocatorFailure::None;
        result.State = Status::Ready;
        return true;
    }

    [[nodiscard]] CEventGenerator::EventLocatorResult LocateEnhanced(
        ImageView image,
        CEventGenerator::EventLocatorResult result)
    {
        using namespace CEventGenerator;

        if (TryLocateEnhancedFiveMReceivers(image, result))
        {
            return result;
        }

        constexpr std::size_t SearchLimit = 4096;
        const auto directPedMatches = FindEnhancedReceivers(
            image, false, SearchLimit, false);
        const auto directGlobalMatches = FindEnhancedReceivers(
            image, true, SearchLimit, false);
        const bool directComplete =
            directPedMatches.size() <= SearchLimit &&
            directGlobalMatches.size() <= SearchLimit;
        const auto directCorrelation = CorrelateEnhancedReceivers(
            directPedMatches, directGlobalMatches, directComplete);
        const bool usePointerFallback = directCorrelation.UniqueGroups != 1;
        const auto pedMatches = usePointerFallback
            ? FindEnhancedReceivers(image, false, SearchLimit, true)
            : directPedMatches;
        const auto globalMatches = usePointerFallback
            ? FindEnhancedReceivers(image, true, SearchLimit, true)
            : directGlobalMatches;
        const bool selectedComplete =
            pedMatches.size() <= SearchLimit &&
            globalMatches.size() <= SearchLimit;
        const auto correlation = usePointerFallback
            ? CorrelateEnhancedReceivers(pedMatches, globalMatches,
                selectedComplete)
            : directCorrelation;
        RecordEnhancedOutgoingAbi(
            image, pedMatches, 1u, result.Diagnostics);
        RecordEnhancedOutgoingAbi(
            image, globalMatches, 2u, result.Diagnostics);
        const auto& hybridCorrelation = correlation;
        RecordEnhancedInboundCallEvidence(
            image, hybridCorrelation, result.Diagnostics);
        result.Diagnostics.EnhancedHybridCorrelationGroupCount =
            hybridCorrelation.UniqueGroups;
        if (hybridCorrelation.UniqueGroups == 1u)
        {
            for (std::size_t index = 0;
                 index < hybridCorrelation.Entries.size(); ++index)
            {
                result.Diagnostics.EnhancedHybridSelectedEntries[index] =
                    static_cast<std::uint32_t>(
                        hybridCorrelation.Entries[index] - image.Base);
            }
            result.Diagnostics.EnhancedHybridSelectedCalleeRva =
                static_cast<std::uint32_t>(hybridCorrelation.Callee - image.Base);
        }
        result.Diagnostics.EnhancedReceiverScanLimit =
            static_cast<std::uint32_t>(SearchLimit);
        result.Diagnostics.EnhancedDirectPedCount =
            static_cast<std::uint32_t>(directPedMatches.size());
        result.Diagnostics.EnhancedDirectGlobalCount =
            static_cast<std::uint32_t>(directGlobalMatches.size());
        result.Diagnostics.EnhancedSelectedPedTruncated =
            pedMatches.size() > SearchLimit ? 1u : 0u;
        result.Diagnostics.EnhancedSelectedGlobalTruncated =
            globalMatches.size() > SearchLimit ? 1u : 0u;
        result.Diagnostics.EnhancedUsedPointerFallback =
            usePointerFallback ? 1u : 0u;
        result.Diagnostics.EnhancedCorrelationGroupCount =
            correlation.UniqueGroups;
        const auto damageMatches = FindAll(image, DamageEventPattern, 2);
        RecordEnhancedReceiverEvidence(
            image, pedMatches, globalMatches, usePointerFallback,
            result.Diagnostics);
        if (correlation.UniqueGroups == 1)
        {
            for (std::size_t index = 0; index < correlation.Entries.size(); ++index)
            {
                const std::byte* entry = correlation.Entries[index];
                const auto function = ResolveRuntimeFunction(image, entry);
                if (!function || result.Diagnostics.EnhancedCandidateDiagnosticCount >=
                    result.Diagnostics.EnhancedCandidates.size())
                {
                    continue;
                }
                auto& diagnostic = result.Diagnostics.EnhancedCandidates[
                    result.Diagnostics.EnhancedCandidateDiagnosticCount++];
                diagnostic.Rva = function.BeginRva;
                diagnostic.Size = function.Size();
                diagnostic.Relations = (index == 2 ? 2u : 1u) | 0x200u |
                    (usePointerFallback ? 0x100u : 0u);
            }
        }
        result.Diagnostics.PedMatchCount =
            static_cast<std::uint32_t>(pedMatches.size());
        result.Diagnostics.GlobalMatchCount =
            static_cast<std::uint32_t>(globalMatches.size());
        result.Diagnostics.DamageMatchCount =
            static_cast<std::uint32_t>(damageMatches.size());

        const auto countAnchors = FindAll(image, EnhancedCEventCountPattern, 4);
        result.Diagnostics.CEventCountAnchorCount =
            static_cast<std::uint32_t>(countAnchors.size());
        if (countAnchors.size() != 1)
        {
            result.Diagnostics.Failure = LocatorFailure::EnhancedCEventCountAnchor;
            return result;
        }

        const RuntimeFunctionView countFunction =
            ResolveRuntimeFunction(image, countAnchors.front());
        if (!countFunction)
        {
            result.Diagnostics.Failure = LocatorFailure::EnhancedStructureInvalid;
            return result;
        }
        result.Diagnostics.CEventCountFunctionRva = countFunction.BeginRva;
        result.Diagnostics.CEventCountFunctionSize = countFunction.Size();

        const auto stackAnchors = FindAll(
            {image.Base + countFunction.BeginRva, countFunction.Size()},
            EnhancedCEventStackPattern,
            4);
        result.Diagnostics.CEventStackAnchorCount =
            static_cast<std::uint32_t>(stackAnchors.size());
        if (stackAnchors.size() != 1)
        {
            result.Diagnostics.Failure = LocatorFailure::EnhancedCEventStackAnchor;
            return result;
        }
        result.Diagnostics.CEventStackFunctionMatchCount = 1;
        result.Diagnostics.CEventStackAnchorRva = static_cast<std::uint32_t>(
            stackAnchors.front() - image.Base);

        const auto typeAnchors = FindAll(image, EnhancedEventTypePattern, 4);
        result.Diagnostics.EventTypeAnchorCount =
            static_cast<std::uint32_t>(typeAnchors.size());
        if (typeAnchors.size() != 1)
        {
            result.Diagnostics.Failure = LocatorFailure::EnhancedEventTypeAnchor;
            return result;
        }

        const RuntimeFunctionView typeFunction =
            ResolveRuntimeFunction(image, typeAnchors.front());
        if (!typeFunction ||
            !Contains(image, countAnchors.front() + 2, sizeof(std::int32_t)) ||
            !Contains(image, stackAnchors.front() + 4, sizeof(std::int32_t)) ||
            !Contains(image, typeAnchors.front() + 20, sizeof(std::uint8_t)))
        {
            result.Diagnostics.Failure = LocatorFailure::EnhancedStructureInvalid;
            return result;
        }

        result.Diagnostics.EventTypeFunctionRva = typeFunction.BeginRva;
        result.Diagnostics.EventTypeFunctionSize = typeFunction.Size();
        const std::int32_t countOffset = ReadInt32(countAnchors.front() + 2);
        const std::int32_t stackOffset = ReadInt32(stackAnchors.front() + 4);
        const std::uint32_t typeOffset =
            std::to_integer<std::uint8_t>(typeAnchors.front()[20]);
        if (countOffset <= 0 || stackOffset <= 0 ||
            countOffset > 0x10000 || stackOffset > 0x10000 ||
            typeOffset == 0 || typeOffset > 0x800 ||
            typeOffset % sizeof(void*) != 0)
        {
            result.Diagnostics.Failure = LocatorFailure::EnhancedStructureInvalid;
            return result;
        }

        result.Diagnostics.CEventCountOffset =
            static_cast<std::uint32_t>(countOffset);
        result.Diagnostics.CEventStackOffset =
            static_cast<std::uint32_t>(stackOffset);
        result.Diagnostics.EventTypeVFuncOffset = typeOffset;

        RecordEnhancedAnchorCallEvidence(image,
            countFunction.BeginRva, typeFunction.BeginRva,
            result.Diagnostics);
        RecordEnhancedSelectedPathEvidence(image, correlation,
            pedMatches, globalMatches, result.Diagnostics);
        RecordEnhancedCallEvidence(image, correlation,
            countFunction.BeginRva, typeFunction.BeginRva,
            result.Diagnostics);
        RecordEnhancedVirtualCallEvidence(image, correlation,
            result.Diagnostics, result.Diagnostics);
        RecordEnhancedControlFlowEvidence(image, result.Diagnostics);
        RecordEnhancedStackPathEvidence(image, result.Diagnostics);
        result.Diagnostics.Failure = LocatorFailure::EnhancedDispatchUnresolved;
        return result;
    }

}

namespace CEventGenerator
{
    EventLocatorResult LocateEventHooks() noexcept
    {
        EventLocatorResult result{};
        const auto path = GetExecutablePath();
        result.Targets.Edition = DetectEdition(path);
        result.Targets.GameBuild = GetGameBuild(path);

        if (result.Targets.Edition == GameEdition::Unknown)
        {
            result.Diagnostics.Failure = LocatorFailure::EditionUnsupported;
            result.State = Status::UnsupportedGameBuild;
            return result;
        }

        const ImageView image = GetImageView();
        if (image.Base == nullptr)
        {
            result.Diagnostics.Failure = LocatorFailure::ImageUnavailable;
            return result;
        }

        if (result.Targets.Edition == GameEdition::Enhanced)
        {
            return LocateEnhanced(image, result);
        }
        return LocateLegacy(image, result);
    }
}



