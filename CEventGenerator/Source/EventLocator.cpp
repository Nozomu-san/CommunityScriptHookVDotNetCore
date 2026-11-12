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
        result.Targets.EventIdVFuncOffset =
            static_cast<std::uint32_t>(3u * sizeof(void*));
        result.Targets.EventArgumentsVFuncOffset =
            static_cast<std::uint32_t>(6u * sizeof(void*));
        result.Targets.EventEntityVFuncOffset =
            static_cast<std::uint32_t>(25u * sizeof(void*));
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

    [[nodiscard]] CEventGenerator::EventLocatorResult LocateEnhanced(
        ImageView image,
        CEventGenerator::EventLocatorResult result)
    {
        using namespace CEventGenerator;

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
        result.Targets.EventIdVFuncOffset = typeOffset;

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