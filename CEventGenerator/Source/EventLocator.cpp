#include "EventLocator.hpp"
#include "MinHook/src/hde/hde64.h"

#include <Windows.h>
#include <winver.h>

#include <algorithm>
#include <array>
#include <bit>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <iterator>
#include <optional>
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

    struct RelatedCandidate final
    {
        RuntimeFunctionView Function{};
        std::uint32_t Relations{};
    };

    struct DispatchCallsiteEvidence final
    {
        std::uint32_t WalkerRva{};
        std::uint32_t CallsiteRva{};
        std::uint32_t CalleeRva{};
        std::uint32_t Relations{};
    };

    struct DispatchCalleeEvidence final
    {
        std::uint32_t Rva{};
        std::uint32_t Size{};
        std::uint32_t Relations{};
        std::uint32_t CallsiteCount{};
        std::uint32_t WalkerCount{};
    };

    struct DispatchForwardCallsiteEvidence final
    {
        std::uint32_t ParentRva{};
        std::uint32_t CallsiteRva{};
        std::uint32_t CalleeRva{};
        std::uint32_t EventArgumentMask{};
        std::uint32_t GroupArgumentMask{};
    };

    struct DispatchForwardCalleeEvidence final
    {
        std::uint32_t Rva{};
        std::uint32_t Size{};
        std::uint32_t Relations{};
        std::uint32_t CallsiteCount{};
        std::uint32_t ParentCount{};
        std::uint32_t EventArgumentMask{};
        std::uint32_t GroupArgumentMask{};
    };

    struct DispatchCalleeAnalysis final
    {
        std::uint32_t CallsiteCount{};
        std::vector<DispatchCallsiteEvidence> Callsites;
        std::vector<DispatchCalleeEvidence> Callees;
        std::vector<DispatchForwardCallsiteEvidence> ForwardCallsites;
        std::vector<DispatchForwardCalleeEvidence> ForwardCallees;
    };

    struct DispatchFunctionAnalysis final
    {
        std::uint32_t Relations{};
        std::vector<DispatchForwardCallsiteEvidence> ForwardCallsites;
    };

    struct EventConsumerCallsiteEvidence final
    {
        std::uint32_t WalkerRva{};
        std::uint32_t CallsiteRva{};
        std::uint32_t CalleeRva{};
        std::uint32_t EventArgumentMask{};
        std::uint32_t Relations{};
        std::uint32_t InstructionLength{};
        std::array<std::uint32_t, 4> Encoding{};
        std::uint32_t OperandInfo{};
        std::uint32_t Displacement{};
    };

    struct EventConsumerEvidence final
    {
        std::uint32_t Rva{};
        std::uint32_t Size{};
        std::uint32_t Relations{};
        std::uint32_t EventArgumentMask{};
        std::uint32_t FirstUseRva{};
        std::uint32_t FirstClobberRva{};
        std::uint32_t CallsiteCount{};
        std::uint32_t WalkerCount{};
        std::uint32_t ReadCount{};
        std::uint32_t MemoryStoreCount{};
        std::uint32_t CompareCount{};
        std::uint32_t VtableLoadCount{};
        std::uint32_t ForwardCount{};
        std::uint32_t TailForwardCount{};
        std::uint32_t TailInstructionRva{};
        std::uint32_t TailInstructionLength{};
        std::array<std::uint32_t, 4> TailEncoding{};
        std::uint32_t TailOperandInfo{};
        std::uint32_t TailDisplacement{};
        std::uint32_t TailBaseRegister{0xFFFFFFFFu};
        std::uint32_t TailIndexRegister{0xFFFFFFFFu};
        std::uint32_t TailScale{};
        std::uint32_t TailImageSize{};
        std::uint32_t TailSlotRva{0xFFFFFFFFu};
        std::uint64_t TailSlotAddress{};
        std::uint32_t TailSlotProtection{};
        std::uint32_t TailSlotFlags{};
        std::uint64_t TailTargetAddress{};
        std::uint32_t TailTargetProtection{};
        std::uint32_t TailTargetFlags{};
        std::uint64_t TailTargetModuleBase{};
        std::uint32_t TailTargetModuleSize{};
        std::uint32_t TailTargetModuleRva{};
    };

    struct EventConsumerFunctionAnalysis final
    {
        std::uint32_t Relations{};
        std::uint32_t FirstUseRva{};
        std::uint32_t FirstClobberRva{};
        std::uint32_t ReadCount{};
        std::uint32_t MemoryStoreCount{};
        std::uint32_t CompareCount{};
        std::uint32_t VtableLoadCount{};
        std::uint32_t ForwardCount{};
        std::uint32_t TailForwardCount{};
        std::uint32_t TailTargetRva{};
        std::uint32_t TailEventArgumentMask{};
        std::uint32_t TailInstructionRva{};
        std::uint32_t TailInstructionLength{};
        std::array<std::uint32_t, 4> TailEncoding{};
        std::uint32_t TailOperandInfo{};
        std::uint32_t TailDisplacement{};
        std::uint32_t TailBaseRegister{0xFFFFFFFFu};
        std::uint32_t TailIndexRegister{0xFFFFFFFFu};
        std::uint32_t TailScale{};
        std::uint32_t TailImageSize{};
        std::uint32_t TailSlotRva{0xFFFFFFFFu};
        std::uint64_t TailSlotAddress{};
        std::uint32_t TailSlotProtection{};
        std::uint32_t TailSlotFlags{};
        std::uint64_t TailTargetAddress{};
        std::uint32_t TailTargetProtection{};
        std::uint32_t TailTargetFlags{};
        std::uint64_t TailTargetModuleBase{};
        std::uint32_t TailTargetModuleSize{};
        std::uint32_t TailTargetModuleRva{};
    };

    struct EventConsumerAnalysis final
    {
        std::vector<EventConsumerCallsiteEvidence> Callsites;
        std::vector<EventConsumerEvidence> Consumers;
    };

    struct VirtualArgumentSource final
    {
        std::uint32_t Rva{};
        std::uint32_t Kind{};
        std::uint32_t Encoding{};
        std::uint32_t OperandInfo{};
        std::uint32_t Displacement{};
        std::uint32_t Value{};
    };

    struct VirtualSlotCallsiteEvidence final
    {
        std::uint32_t WalkerRva{};
        std::uint32_t CallsiteRva{};
        std::uint32_t SlotOffset{};
        std::uint32_t Relations{};
        std::uint32_t EventArgumentMask{};
        std::uint32_t ReturnRelations{};
        std::uint32_t ReturnFirstUseRva{};
        std::uint32_t ReturnFirstClobberRva{};
        std::uint32_t PreparedArgumentMask{};
        std::uint32_t MemoryArgumentMask{};
        std::uint32_t AddressArgumentMask{};
        std::uint32_t ImmediateArgumentMask{};
        std::uint32_t ZeroArgumentMask{};
        VirtualArgumentSource Rdx{};
        VirtualArgumentSource R8{};
        VirtualArgumentSource R9{};
    };

    struct VirtualSlotEvidence final
    {
        std::uint32_t SlotOffset{};
        std::uint32_t Relations{};
        std::uint32_t CallsiteCount{};
        std::uint32_t WalkerCount{};
        std::uint32_t EventRcxCount{};
        std::uint32_t EventArgumentMask{};
        std::uint32_t ReturnRelations{};
        std::uint32_t ReturnUseCount{};
    };

    struct VirtualReturnUse final
    {
        std::uint32_t Relations{};
        std::uint32_t FirstUseRva{};
        std::uint32_t FirstClobberRva{};
    };

    struct VirtualInputUse final
    {
        std::uint32_t PreparedArgumentMask{};
        std::uint32_t MemoryArgumentMask{};
        std::uint32_t AddressArgumentMask{};
        std::uint32_t ImmediateArgumentMask{};
        std::uint32_t ZeroArgumentMask{};
        VirtualArgumentSource Rdx{};
        VirtualArgumentSource R8{};
        VirtualArgumentSource R9{};
    };

    struct VirtualSlotAnalysis final
    {
        std::vector<VirtualSlotEvidence> Slots;
        std::vector<VirtualSlotCallsiteEvidence> Callsites;
    };

    struct RttiEventTypeEvidence final
    {
        std::uint32_t TypeNameRva{};
        std::uint32_t TypeDescriptorRva{};
        std::uint32_t CompleteObjectLocatorRva{};
        std::uint32_t VftableRva{};
        std::uint32_t EventTypeValue{0xFFFFFFFFu};
        std::uint32_t Slot30TargetRva{0xFFFFFFFFu};
        std::uint32_t Slot48TargetRva{0xFFFFFFFFu};
        std::uint32_t Slot50TargetRva{0xFFFFFFFFu};
        std::uint32_t Slot60TargetRva{0xFFFFFFFFu};
        std::uint32_t Slot68TargetRva{0xFFFFFFFFu};
        std::uint32_t SlotB0TargetRva{0xFFFFFFFFu};
        std::uint32_t SlotF8TargetRva{0xFFFFFFFFu};
    };

    struct RttiEventTypeAnalysis final
    {
        std::uint32_t CandidateCount{};
        std::vector<RttiEventTypeEvidence> Types;
    };

    struct StackProvenanceSlot final
    {
        std::uint8_t Base{};
        std::int32_t Displacement{};
        std::uint32_t Provenance{};
    };

    struct KnownCallerSets final
    {
        std::vector<RuntimeFunctionView> Count;
        std::vector<RuntimeFunctionView> EventType;
    };

    constexpr std::uint32_t RelationCallsCount = 1u << 0;
    constexpr std::uint32_t RelationCallsEventType = 1u << 1;
    constexpr std::uint32_t RelationCalledByCount = 1u << 2;
    constexpr std::uint32_t RelationCalledByEventType = 1u << 3;
    constexpr std::uint32_t RelationSharesCallerWithCount = 1u << 4;
    constexpr std::uint32_t RelationSharesCallerWithEventType = 1u << 5;
    constexpr std::uint32_t RelationIsCountFunction = 1u << 6;
    constexpr std::uint32_t RelationIsEventTypeFunction = 1u << 7;
    constexpr std::uint32_t RelationEventObjectFlow = 1u << 8;
    constexpr std::uint32_t RelationIterationLoop = 1u << 9;
    constexpr std::uint32_t RelationAddressTaken = 1u << 10;

    constexpr std::uint32_t DispatchRelationEventArgumentRdx = 1u << 0;
    constexpr std::uint32_t DispatchRelationFirstArgumentPrepared = 1u << 1;
    constexpr std::uint32_t DispatchRelationAfterEventTypeCall = 1u << 2;
    constexpr std::uint32_t DispatchRelationMultipleCallsites = 1u << 3;
    constexpr std::uint32_t DispatchRelationMultipleWalkers = 1u << 4;
    constexpr std::uint32_t DispatchRelationIncomingGroupRead = 1u << 5;
    constexpr std::uint32_t DispatchRelationIncomingEventRead = 1u << 6;
    constexpr std::uint32_t DispatchRelationEventVtableLoaded = 1u << 7;
    constexpr std::uint32_t DispatchRelationCallsEventTypeVirtual = 1u << 8;
    constexpr std::uint32_t DispatchRelationCallsEventArgumentsVirtual = 1u << 9;
    constexpr std::uint32_t DispatchRelationCallsEventEntityVirtual = 1u << 10;
    constexpr std::uint32_t DispatchRelationEventSpilledToStack = 1u << 11;
    constexpr std::uint32_t DispatchRelationEventReloadedFromStack = 1u << 12;
    constexpr std::uint32_t DispatchRelationGroupSpilledToStack = 1u << 13;
    constexpr std::uint32_t DispatchRelationGroupReloadedFromStack = 1u << 14;
    constexpr std::uint32_t DispatchRelationForwardsEvent = 1u << 15;
    constexpr std::uint32_t DispatchRelationForwardsGroup = 1u << 16;

    constexpr std::uint32_t EventConsumerRelationRead = 1u << 0;
    constexpr std::uint32_t EventConsumerRelationMemoryStore = 1u << 1;
    constexpr std::uint32_t EventConsumerRelationCompare = 1u << 2;
    constexpr std::uint32_t EventConsumerRelationVtableLoaded = 1u << 3;
    constexpr std::uint32_t EventConsumerRelationForward = 1u << 4;
    constexpr std::uint32_t EventConsumerRelationTailForward = 1u << 5;
    constexpr std::uint32_t EventConsumerRelationSpilledToStack = 1u << 6;
    constexpr std::uint32_t EventConsumerRelationReloadedFromStack = 1u << 7;
    constexpr std::uint32_t EventConsumerRelationMultipleCallsites = 1u << 8;
    constexpr std::uint32_t EventConsumerRelationMultipleWalkers = 1u << 9;
    constexpr std::uint32_t EventConsumerRelationUseBeforeClobber = 1u << 10;
    constexpr std::uint32_t EventConsumerRelationRecoveredLeaf = 1u << 11;
    constexpr std::uint32_t EventConsumerRelationTailChainResolved = 1u << 12;
    constexpr std::uint32_t EventConsumerRelationTailChainCycle = 1u << 13;
    constexpr std::uint32_t EventConsumerRelationTailChainDepthLimit = 1u << 14;
    constexpr std::uint32_t EventConsumerRelationTailChainUnresolved = 1u << 15;
    constexpr std::uint32_t EventConsumerRelationIndirectTailResolved = 1u << 16;
    constexpr std::uint32_t EventConsumerRelationRegisterTailResolved = 1u << 17;
    constexpr std::uint32_t EventConsumerRelationIndirectTailRegister = 1u << 18;
    constexpr std::uint32_t EventConsumerRelationIndirectTailMemory = 1u << 19;
    constexpr std::uint32_t EventConsumerRelationMemoryTailEventOperand = 1u << 20;
    constexpr std::uint32_t EventConsumerRelationMemoryTailVtableOperand = 1u << 21;
    constexpr std::uint32_t EventConsumerRelationMemoryTailIndexed = 1u << 22;
    constexpr std::uint32_t EventConsumerRelationMemoryTailResolved = 1u << 23;
    constexpr std::uint32_t EventConsumerRelationSystemImportThunk = 1u << 24;
    constexpr std::uint32_t EventConsumerRelationGameLocalConsumer = 1u << 25;
    constexpr std::uint32_t EventConsumerCallsiteDirect = 1u << 0;
    constexpr std::uint32_t EventConsumerCallsiteTail = 1u << 1;
    constexpr std::uint32_t EventConsumerCallsiteIndirect = 1u << 2;
    constexpr std::uint32_t EventConsumerCallsiteUnresolved = 1u << 3;
    constexpr std::uint32_t EventConsumerCallsiteRegister = 1u << 4;
    constexpr std::uint32_t EventConsumerCallsiteMemory = 1u << 5;
    constexpr std::uint32_t TailSlotFlagRipRelative = 1u << 0;
    constexpr std::uint32_t TailSlotFlagInImage = 1u << 1;
    constexpr std::uint32_t TailSlotFlagReadable = 1u << 2;
    constexpr std::uint32_t TailSlotFlagInIat = 1u << 3;
    constexpr std::uint32_t TailTargetFlagReadable = 1u << 0;
    constexpr std::uint32_t TailTargetFlagExecutable = 1u << 1;
    constexpr std::uint32_t TailTargetFlagInMainImage = 1u << 2;
    constexpr std::uint32_t TailTargetFlagModuleResolved = 1u << 3;
    constexpr std::uint32_t TailTargetFlagOtherModule = 1u << 4;
    constexpr std::uint32_t VirtualSlotRelationVtableProvenance = 1u << 0;
    constexpr std::uint32_t VirtualSlotRelationEventInRcx = 1u << 1;
    constexpr std::uint32_t VirtualSlotRelationEventType = 1u << 2;
    constexpr std::uint32_t VirtualSlotRelationEventArguments = 1u << 3;
    constexpr std::uint32_t VirtualSlotRelationEventEntity = 1u << 4;
    constexpr std::uint32_t VirtualReturnRelationUsed = 1u << 0;
    constexpr std::uint32_t VirtualReturnRelationCompare = 1u << 1;
    constexpr std::uint32_t VirtualReturnRelationDereference = 1u << 2;
    constexpr std::uint32_t VirtualReturnRelationMemoryStore = 1u << 3;
    constexpr std::uint32_t VirtualReturnRelationForward = 1u << 4;

    constexpr std::uint32_t ProvenanceGroup = 1u << 0;
    constexpr std::uint32_t ProvenanceEvent = 1u << 1;
    constexpr std::uint32_t ProvenanceVtable = 1u << 2;

    [[nodiscard]] std::array<std::uint32_t, 4> PackInstructionBytes(
        std::span<const std::byte> bytes,
        std::size_t instructionOffset,
        std::uint32_t instructionLength) noexcept
    {
        std::array<std::uint32_t, 4> packed{};
        const std::size_t length = std::min<std::size_t>(
            std::min<std::size_t>(instructionLength, 15u),
            bytes.size() > instructionOffset
                ? bytes.size() - instructionOffset
                : 0u);
        for (std::size_t index = 0; index < length; ++index)
        {
            packed[index / 4u] |=
                static_cast<std::uint32_t>(
                    std::to_integer<std::uint8_t>(
                        bytes[instructionOffset + index]))
                << ((index % 4u) * 8u);
        }
        return packed;
    }

    [[nodiscard]] std::uint32_t PackOperandInfo(
        const hde64s& instruction) noexcept
    {
        return static_cast<std::uint32_t>(instruction.modrm) |
            (static_cast<std::uint32_t>(instruction.sib) << 8u) |
            (static_cast<std::uint32_t>(instruction.rex) << 16u) |
            (static_cast<std::uint32_t>(instruction.len) << 24u);
    }

    [[nodiscard]] std::uint32_t InstructionDisplacementBits(
        const hde64s& instruction) noexcept
    {
        if ((instruction.flags & F_DISP32) != 0)
        {
            return instruction.disp.disp32;
        }
        if ((instruction.flags & F_DISP16) != 0)
        {
            return instruction.disp.disp16;
        }
        if ((instruction.flags & F_DISP8) != 0)
        {
            return instruction.disp.disp8;
        }
        return 0;
    }

    struct GuardMetadata final
    {
        std::uint32_t Flags{};
        std::uint32_t FunctionCount{};
        std::uint32_t LongJumpTargetCount{};
        std::uint32_t AddressTakenIatCount{};
    };

    [[nodiscard]] GuardMetadata ReadGuardMetadata(ImageView image) noexcept
    {
        GuardMetadata metadata{};
        if (image.Base == nullptr || image.Size < sizeof(IMAGE_DOS_HEADER))
        {
            return metadata;
        }
        const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(image.Base);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0)
        {
            return metadata;
        }
        const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(
            image.Base + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE)
        {
            return metadata;
        }
        const IMAGE_DATA_DIRECTORY directory =
            nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_LOAD_CONFIG];
        if (directory.VirtualAddress == 0 ||
            directory.VirtualAddress >= image.Size ||
            image.Size - directory.VirtualAddress <
                sizeof(IMAGE_LOAD_CONFIG_DIRECTORY64))
        {
            return metadata;
        }
        const auto* config =
            reinterpret_cast<const IMAGE_LOAD_CONFIG_DIRECTORY64*>(
                image.Base + directory.VirtualAddress);
        metadata.Flags = config->GuardFlags;
        metadata.FunctionCount = static_cast<std::uint32_t>(
            std::min<ULONGLONG>(config->GuardCFFunctionCount, UINT32_MAX));
        metadata.LongJumpTargetCount = static_cast<std::uint32_t>(
            std::min<ULONGLONG>(config->GuardLongJumpTargetCount, UINT32_MAX));
        metadata.AddressTakenIatCount = static_cast<std::uint32_t>(
            std::min<ULONGLONG>(config->GuardAddressTakenIatEntryCount, UINT32_MAX));
        return metadata;
    }

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

    [[nodiscard]] ImageView TailView(
        ImageView image,
        const std::byte* start) noexcept
    {
        if (image.Base == nullptr || start < image.Base ||
            start >= image.Base + image.Size)
        {
            return {};
        }
        return {
            start,
            static_cast<std::size_t>((image.Base + image.Size) - start)
        };
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

    [[nodiscard]] std::span<const std::byte> FunctionBytes(
        ImageView image,
        RuntimeFunctionView function) noexcept
    {
        if (!function || function.EndRva > image.Size)
        {
            return {};
        }
        return {
            image.Base + function.BeginRva,
            static_cast<std::size_t>(function.Size())
        };
    }

    [[nodiscard]] bool SameFunction(
        RuntimeFunctionView left,
        RuntimeFunctionView right) noexcept
    {
        return left && right &&
            left.BeginRva == right.BeginRva &&
            left.EndRva == right.EndRva;
    }

    [[nodiscard]] std::uint32_t RvaOf(
        ImageView image,
        const std::byte* address) noexcept
    {
        if (!Contains(image, address, 1))
        {
            return 0;
        }
        return static_cast<std::uint32_t>(address - image.Base);
    }

    [[nodiscard]] bool VisitDecodedInstructions(
        std::span<const std::byte> bytes,
        auto&& visitor)
    {
        constexpr std::size_t MaximumInstructionLength = 15;
        for (std::size_t offset = 0; offset < bytes.size();)
        {
            std::array<std::byte, MaximumInstructionLength> window{};
            const std::size_t available = std::min(
                MaximumInstructionLength,
                bytes.size() - offset);
            std::memcpy(
                window.data(),
                bytes.data() + offset,
                available);

            hde64s instruction{};
            const unsigned int length = hde64_disasm(
                window.data(),
                &instruction);
            if (length == 0 || length > available)
            {
                break;
            }

            if ((instruction.flags & F_ERROR) == 0 &&
                visitor(offset, instruction))
            {
                return true;
            }
            offset += length;
        }
        return false;
    }

    [[nodiscard]] std::optional<std::uint32_t> RelativeBranchTargetRva(
        ImageView image,
        std::uint32_t instructionRva,
        const hde64s& instruction) noexcept
    {
        if ((instruction.flags & F_RELATIVE) == 0)
        {
            return std::nullopt;
        }

        std::int64_t relative = 0;
        if ((instruction.flags & F_IMM8) != 0)
        {
            relative = static_cast<std::int8_t>(instruction.imm.imm8);
        }
        else if ((instruction.flags & F_IMM32) != 0)
        {
            relative = std::bit_cast<std::int32_t>(instruction.imm.imm32);
        }
        else
        {
            return std::nullopt;
        }

        const std::int64_t target =
            static_cast<std::int64_t>(instructionRva) +
            static_cast<std::int64_t>(instruction.len) +
            relative;
        if (target < 0 || target >= static_cast<std::int64_t>(image.Size))
        {
            return std::nullopt;
        }
        return static_cast<std::uint32_t>(target);
    }

    [[nodiscard]] RuntimeFunctionView RecoverLeafFunction(
        ImageView image,
        std::uint32_t beginRva) noexcept
    {
        constexpr std::uint32_t MaximumLeafBytes = 0x100u;
        constexpr std::size_t MaximumLeafInstructions = 128;
        constexpr std::size_t MaximumInstructionLength = 15;

        if (beginRva >= image.Size ||
            ResolveRuntimeFunction(image, image.Base + beginRva))
        {
            return {};
        }

        const std::uint32_t limit = static_cast<std::uint32_t>(
            std::min<std::size_t>(
                image.Size,
                static_cast<std::size_t>(beginRva) + MaximumLeafBytes));
        std::vector<std::uint32_t> pending{beginRva};
        std::unordered_set<std::uint32_t> visited;
        std::uint32_t maximumEnd = beginRva;
        std::size_t instructionCount = 0;
        bool hasExit = false;

        while (!pending.empty())
        {
            std::uint32_t current = pending.back();
            pending.pop_back();
            if (current < beginRva || current >= limit)
            {
                continue;
            }

            while (current < limit)
            {
                if (!visited.insert(current).second)
                {
                    break;
                }
                if (++instructionCount > MaximumLeafInstructions)
                {
                    return {};
                }
                if (current != beginRva &&
                    ResolveRuntimeFunction(image, image.Base + current))
                {
                    hasExit = true;
                    break;
                }

                std::array<std::byte, MaximumInstructionLength> window{};
                const std::size_t available = std::min<std::size_t>(
                    MaximumInstructionLength,
                    static_cast<std::size_t>(limit - current));
                std::memcpy(
                    window.data(),
                    image.Base + current,
                    available);

                hde64s instruction{};
                const unsigned int length = hde64_disasm(
                    window.data(),
                    &instruction);
                if (length == 0 ||
                    length > available ||
                    (instruction.flags & F_ERROR) != 0)
                {
                    return {};
                }

                const std::uint32_t next = current + length;
                maximumEnd = std::max(maximumEnd, next);

                if (instruction.opcode == 0xE8u ||
                    (instruction.opcode == 0xFFu &&
                     (instruction.flags & F_MODRM) != 0 &&
                     (instruction.modrm_reg == 2u ||
                      instruction.modrm_reg == 3u)))
                {
                    return {};
                }

                if (instruction.opcode == 0xC3u ||
                    instruction.opcode == 0xC2u ||
                    instruction.opcode == 0xCBu ||
                    instruction.opcode == 0xCAu)
                {
                    hasExit = true;
                    break;
                }

                const bool shortJump = instruction.opcode == 0xEBu;
                const bool nearJump = instruction.opcode == 0xE9u;
                if (shortJump || nearJump)
                {
                    const auto target = RelativeBranchTargetRva(
                        image,
                        current,
                        instruction);
                    if (!target.has_value() ||
                        *target < beginRva ||
                        *target >= limit)
                    {
                        hasExit = true;
                    }
                    else
                    {
                        pending.push_back(*target);
                    }
                    break;
                }

                if (instruction.opcode == 0xFFu &&
                    (instruction.flags & F_MODRM) != 0 &&
                    (instruction.modrm_reg == 4u ||
                     instruction.modrm_reg == 5u))
                {
                    hasExit = true;
                    break;
                }

                const bool shortConditional =
                    instruction.opcode >= 0x70u &&
                    instruction.opcode <= 0x7Fu;
                const bool nearConditional =
                    instruction.opcode == 0x0Fu &&
                    instruction.opcode2 >= 0x80u &&
                    instruction.opcode2 <= 0x8Fu;
                const bool loopConditional =
                    instruction.opcode >= 0xE0u &&
                    instruction.opcode <= 0xE3u;
                if (shortConditional || nearConditional || loopConditional)
                {
                    const auto target = RelativeBranchTargetRva(
                        image,
                        current,
                        instruction);
                    if (target.has_value() &&
                        *target >= beginRva &&
                        *target < limit)
                    {
                        pending.push_back(*target);
                    }
                    else
                    {
                        hasExit = true;
                    }
                }

                current = next;
            }
        }

        if (!hasExit || maximumEnd <= beginRva)
        {
            return {};
        }
        return {beginRva, maximumEnd};
    }

    [[nodiscard]] bool AnyDecodedInstruction(
        std::span<const std::byte> bytes,
        auto&& predicate)
    {
        return VisitDecodedInstructions(
            bytes,
            [&predicate](std::size_t, const hde64s& instruction)
            {
                return predicate(instruction);
            });
    }

    [[nodiscard]] std::optional<std::int32_t> MemoryDisplacement(
        const hde64s& instruction) noexcept
    {
        if ((instruction.flags & F_MODRM) == 0 || instruction.modrm_mod == 3)
        {
            return std::nullopt;
        }
        if ((instruction.flags & F_DISP32) != 0)
        {
            return static_cast<std::int32_t>(instruction.disp.disp32);
        }
        if ((instruction.flags & F_DISP16) != 0)
        {
            return static_cast<std::int16_t>(instruction.disp.disp16);
        }
        if ((instruction.flags & F_DISP8) != 0)
        {
            return static_cast<std::int8_t>(instruction.disp.disp8);
        }
        return 0;
    }

    [[nodiscard]] bool FunctionReferencesDisplacement(
        ImageView image,
        RuntimeFunctionView function,
        std::int32_t displacement) noexcept
    {
        return AnyDecodedInstruction(
            FunctionBytes(image, function),
            [displacement](const hde64s& instruction)
            {
                const auto value = MemoryDisplacement(instruction);
                return value.has_value() && *value == displacement;
            });
    }

    [[nodiscard]] bool FunctionReferencesIndexedStack(
        ImageView image,
        RuntimeFunctionView function,
        std::int32_t displacement) noexcept
    {
        return AnyDecodedInstruction(
            FunctionBytes(image, function),
            [displacement](const hde64s& instruction)
            {
                if ((instruction.flags & F_SIB) == 0 ||
                    instruction.sib_scale != 3)
                {
                    return false;
                }

                const auto value = MemoryDisplacement(instruction);
                return value.has_value() && *value == displacement;
            });
    }

    [[nodiscard]] bool FunctionCallsVirtualSlot(
        ImageView image,
        RuntimeFunctionView function,
        std::uint32_t slot) noexcept
    {
        if (slot > 0x7FFFFFFFu)
        {
            return false;
        }

        return AnyDecodedInstruction(
            FunctionBytes(image, function),
            [slot](const hde64s& instruction)
            {
                if (instruction.opcode != 0xFFu ||
                    (instruction.flags & F_MODRM) == 0 ||
                    instruction.modrm_reg != 2u ||
                    instruction.modrm_mod == 3u)
                {
                    return false;
                }

                const auto displacement = MemoryDisplacement(instruction);
                return displacement.has_value() &&
                    *displacement >= 0 &&
                    static_cast<std::uint32_t>(*displacement) == slot;
            });
    }

    struct EventObjectFlow final
    {
        bool Verified{};
        std::uint32_t StackLoadOffset{};
        std::uint32_t VTableLoadOffset{};
        std::uint32_t TypeCallOffset{};
    };

    [[nodiscard]] std::uint8_t ModRmRegRegister(
        const hde64s& instruction) noexcept
    {
        return static_cast<std::uint8_t>(
            instruction.modrm_reg |
            (static_cast<std::uint8_t>(instruction.rex_r) << 3));
    }

    [[nodiscard]] std::optional<std::uint8_t> MemoryBaseRegister(
        const hde64s& instruction) noexcept
    {
        if ((instruction.flags & F_MODRM) == 0 || instruction.modrm_mod == 3)
        {
            return std::nullopt;
        }

        if ((instruction.flags & F_SIB) != 0)
        {
            if (instruction.modrm_mod == 0 && instruction.sib_base == 5)
            {
                return std::nullopt;
            }
            return static_cast<std::uint8_t>(
                instruction.sib_base |
                (static_cast<std::uint8_t>(instruction.rex_b) << 3));
        }

        if (instruction.modrm_mod == 0 && instruction.modrm_rm == 5)
        {
            return std::nullopt;
        }

        return static_cast<std::uint8_t>(
            instruction.modrm_rm |
            (static_cast<std::uint8_t>(instruction.rex_b) << 3));
    }

    [[nodiscard]] std::optional<std::uint8_t> MemoryIndexRegister(
        const hde64s& instruction) noexcept
    {
        if ((instruction.flags & F_MODRM) == 0 ||
            instruction.modrm_mod == 3 ||
            (instruction.flags & F_SIB) == 0)
        {
            return std::nullopt;
        }

        if (instruction.sib_index == 4u && instruction.rex_x == 0)
        {
            return std::nullopt;
        }

        return static_cast<std::uint8_t>(
            instruction.sib_index |
            (static_cast<std::uint8_t>(instruction.rex_x) << 3));
    }

    [[nodiscard]] std::optional<std::uint8_t> RegisterSource(
        const hde64s& instruction) noexcept
    {
        if ((instruction.flags & F_MODRM) == 0 || instruction.modrm_mod != 3)
        {
            return std::nullopt;
        }
        return static_cast<std::uint8_t>(
            instruction.modrm_rm |
            (static_cast<std::uint8_t>(instruction.rex_b) << 3));
    }

    [[nodiscard]] std::uint32_t RegisterBit(std::uint8_t index) noexcept
    {
        return index < 16 ? 1u << index : 0u;
    }

    [[nodiscard]] EventObjectFlow AnalyzeEventObjectFlow(
        ImageView image,
        RuntimeFunctionView function,
        std::int32_t eventStackOffset,
        std::uint32_t eventTypeVFuncOffset) noexcept
    {
        EventObjectFlow result{};
        std::uint32_t eventRegisters = 0;
        std::uint32_t vtableRegisters = 0;

        static_cast<void>(VisitDecodedInstructions(
            FunctionBytes(image, function),
            [eventStackOffset,
             eventTypeVFuncOffset,
             &result,
             &eventRegisters,
             &vtableRegisters](
                std::size_t instructionOffset,
                const hde64s& instruction)
            {
                if (instruction.opcode == 0x8Bu && instruction.rex_w != 0 &&
                    (instruction.flags & F_MODRM) != 0)
                {
                    const std::uint8_t destination =
                        ModRmRegRegister(instruction);
                    const std::uint32_t destinationBit =
                        RegisterBit(destination);
                    eventRegisters &= ~destinationBit;
                    vtableRegisters &= ~destinationBit;

                    if (instruction.modrm_mod == 3)
                    {
                        if (const auto source = RegisterSource(instruction);
                            source.has_value())
                        {
                            const std::uint32_t sourceBit =
                                RegisterBit(*source);
                            if ((eventRegisters & sourceBit) != 0)
                            {
                                eventRegisters |= destinationBit;
                            }
                            if ((vtableRegisters & sourceBit) != 0)
                            {
                                vtableRegisters |= destinationBit;
                            }
                        }
                    }
                    else
                    {
                        const auto displacement =
                            MemoryDisplacement(instruction);
                        if ((instruction.flags & F_SIB) != 0 &&
                            instruction.sib_scale == 3 &&
                            displacement.has_value() &&
                            *displacement == eventStackOffset)
                        {
                            eventRegisters |= destinationBit;
                            result.StackLoadOffset =
                                static_cast<std::uint32_t>(instructionOffset);
                        }
                        else if (const auto base =
                                     MemoryBaseRegister(instruction);
                                 base.has_value() &&
                                 displacement.value_or(0) == 0 &&
                                 (eventRegisters &
                                  RegisterBit(*base)) != 0)
                        {
                            vtableRegisters |= destinationBit;
                            result.VTableLoadOffset =
                                static_cast<std::uint32_t>(instructionOffset);
                        }
                    }
                }
                else if (instruction.opcode == 0x89u &&
                         instruction.rex_w != 0 &&
                         (instruction.flags & F_MODRM) != 0 &&
                         instruction.modrm_mod == 3)
                {
                    const std::uint8_t source =
                        ModRmRegRegister(instruction);
                    const std::uint8_t destination =
                        static_cast<std::uint8_t>(
                            instruction.modrm_rm |
                            (static_cast<std::uint8_t>(instruction.rex_b) << 3));
                    const std::uint32_t sourceBit = RegisterBit(source);
                    const std::uint32_t destinationBit =
                        RegisterBit(destination);
                    eventRegisters &= ~destinationBit;
                    vtableRegisters &= ~destinationBit;
                    if ((eventRegisters & sourceBit) != 0)
                    {
                        eventRegisters |= destinationBit;
                    }
                    if ((vtableRegisters & sourceBit) != 0)
                    {
                        vtableRegisters |= destinationBit;
                    }
                }
                else if ((instruction.opcode == 0x8Du &&
                          (instruction.flags & F_MODRM) != 0) ||
                         (instruction.opcode >= 0xB8u &&
                          instruction.opcode <= 0xBFu))
                {
                    const std::uint8_t destination =
                        instruction.opcode == 0x8Du
                            ? ModRmRegRegister(instruction)
                            : static_cast<std::uint8_t>(
                                (instruction.opcode - 0xB8u) |
                                (static_cast<std::uint8_t>(instruction.rex_b)
                                 << 3));
                    const std::uint32_t destinationBit =
                        RegisterBit(destination);
                    eventRegisters &= ~destinationBit;
                    vtableRegisters &= ~destinationBit;
                }

                if (instruction.opcode != 0xFFu ||
                    (instruction.flags & F_MODRM) == 0 ||
                    instruction.modrm_reg != 2u ||
                    instruction.modrm_mod == 3u)
                {
                    return false;
                }

                const auto displacement = MemoryDisplacement(instruction);
                const auto base = MemoryBaseRegister(instruction);
                if (!displacement.has_value() ||
                    !base.has_value() ||
                    *displacement < 0 ||
                    static_cast<std::uint32_t>(*displacement) !=
                        eventTypeVFuncOffset ||
                    (vtableRegisters & RegisterBit(*base)) == 0)
                {
                    return false;
                }

                result.Verified = true;
                result.TypeCallOffset =
                    static_cast<std::uint32_t>(instructionOffset);
                return true;
            }));

        return result;
    }

    [[nodiscard]] std::optional<std::uint32_t> ConditionalBranchTargetOffset(
        const hde64s& instruction,
        std::size_t instructionOffset,
        std::size_t functionSize) noexcept
    {
        bool conditional = false;
        if (instruction.opcode >= 0x70u && instruction.opcode <= 0x7Fu)
        {
            conditional = true;
        }
        else if (instruction.opcode == 0x0Fu &&
                 instruction.opcode2 >= 0x80u &&
                 instruction.opcode2 <= 0x8Fu)
        {
            conditional = true;
        }

        if (!conditional || (instruction.flags & F_RELATIVE) == 0)
        {
            return std::nullopt;
        }

        std::int64_t relative = 0;
        if ((instruction.flags & F_IMM8) != 0)
        {
            relative = static_cast<std::int8_t>(instruction.imm.imm8);
        }
        else if ((instruction.flags & F_IMM32) != 0)
        {
            relative = std::bit_cast<std::int32_t>(
                instruction.imm.imm32);
        }
        else
        {
            return std::nullopt;
        }

        const std::int64_t target =
            static_cast<std::int64_t>(instructionOffset) +
            static_cast<std::int64_t>(instruction.len) +
            relative;
        if (target < 0 ||
            target >= static_cast<std::int64_t>(functionSize))
        {
            return std::nullopt;
        }
        return static_cast<std::uint32_t>(target);
    }

    [[nodiscard]] bool FunctionLoopsOverEventObjectFlow(
        ImageView image,
        RuntimeFunctionView function,
        const EventObjectFlow& flow) noexcept
    {
        if (!flow.Verified)
        {
            return false;
        }

        return VisitDecodedInstructions(
            FunctionBytes(image, function),
            [function, flow](
                std::size_t instructionOffset,
                const hde64s& instruction)
            {
                if (instructionOffset <= flow.TypeCallOffset)
                {
                    return false;
                }

                const auto target = ConditionalBranchTargetOffset(
                    instruction,
                    instructionOffset,
                    function.Size());
                return target.has_value() &&
                    *target <= flow.StackLoadOffset &&
                    *target < instructionOffset;
            });
    }

    [[nodiscard]] bool FunctionAddressTaken(
        ImageView image,
        RuntimeFunctionView function) noexcept
    {
        if (!function || image.Base == nullptr)
        {
            return false;
        }

        const std::uintptr_t address =
            reinterpret_cast<std::uintptr_t>(
                image.Base + function.BeginRva);
        const auto bytes =
            std::bit_cast<std::array<std::byte, sizeof(address)>>(address);
        const std::span<const std::byte> imageBytes{image.Base, image.Size};
        return std::ranges::search(imageBytes, bytes).begin() !=
            imageBytes.end();
    }

    [[nodiscard]] std::optional<std::uint32_t> DirectCallTargetRva(
        ImageView image,
        RuntimeFunctionView function,
        std::size_t instructionOffset,
        const hde64s& instruction) noexcept
    {
        if (instruction.opcode != 0xE8u ||
            (instruction.flags & (F_IMM32 | F_RELATIVE)) !=
                (F_IMM32 | F_RELATIVE))
        {
            return std::nullopt;
        }

        const std::int32_t relative =
            std::bit_cast<std::int32_t>(instruction.imm.imm32);
        const std::int64_t target =
            static_cast<std::int64_t>(function.BeginRva) +
            static_cast<std::int64_t>(instructionOffset) +
            static_cast<std::int64_t>(instruction.len) +
            relative;
        if (target < 0 ||
            target >= static_cast<std::int64_t>(image.Size))
        {
            return std::nullopt;
        }
        return static_cast<std::uint32_t>(target);
    }


    [[nodiscard]] std::optional<std::uint32_t> DirectTailTargetRva(
        ImageView image,
        RuntimeFunctionView function,
        std::size_t instructionOffset,
        const hde64s& instruction) noexcept
    {
        if (instruction.opcode != 0xE9u ||
            (instruction.flags & (F_IMM32 | F_RELATIVE)) !=
                (F_IMM32 | F_RELATIVE))
        {
            return std::nullopt;
        }

        const std::int32_t relative =
            std::bit_cast<std::int32_t>(instruction.imm.imm32);
        const std::int64_t target =
            static_cast<std::int64_t>(function.BeginRva) +
            static_cast<std::int64_t>(instructionOffset) +
            static_cast<std::int64_t>(instruction.len) +
            relative;
        if (target < 0 || target >= static_cast<std::int64_t>(image.Size))
        {
            return std::nullopt;
        }
        return static_cast<std::uint32_t>(target);
    }

    [[nodiscard]] bool IsReadableAddressRange(
        const std::byte* address,
        std::size_t size) noexcept
    {
        if (address == nullptr || size == 0)
        {
            return false;
        }

        MEMORY_BASIC_INFORMATION information{};
        if (VirtualQuery(
                address,
                &information,
                sizeof(information)) != sizeof(information) ||
            information.State != MEM_COMMIT ||
            (information.Protect & (PAGE_NOACCESS | PAGE_GUARD)) != 0)
        {
            return false;
        }

        const std::uintptr_t regionBegin =
            reinterpret_cast<std::uintptr_t>(information.BaseAddress);
        const std::uintptr_t regionEnd = regionBegin + information.RegionSize;
        const std::uintptr_t begin = reinterpret_cast<std::uintptr_t>(address);
        return begin >= regionBegin &&
            begin <= regionEnd &&
            size <= regionEnd - begin;
    }

    [[nodiscard]] bool IsExecutableAddress(const std::byte* address) noexcept
    {
        MEMORY_BASIC_INFORMATION information{};
        if (VirtualQuery(
                address,
                &information,
                sizeof(information)) != sizeof(information) ||
            information.State != MEM_COMMIT ||
            (information.Protect & PAGE_GUARD) != 0)
        {
            return false;
        }

        const DWORD protection = information.Protect & 0xFFu;
        return protection == PAGE_EXECUTE ||
            protection == PAGE_EXECUTE_READ ||
            protection == PAGE_EXECUTE_READWRITE ||
            protection == PAGE_EXECUTE_WRITECOPY;
    }

    struct TailSlotInspection final
    {
        std::uint32_t ImageSize{};
        std::uint32_t SlotRva{0xFFFFFFFFu};
        std::uint64_t SlotAddress{};
        std::uint32_t SlotProtection{};
        std::uint32_t SlotFlags{};
        std::uint64_t TargetAddress{};
        std::uint32_t TargetProtection{};
        std::uint32_t TargetFlags{};
        std::uint64_t TargetModuleBase{};
        std::uint32_t TargetModuleSize{};
        std::uint32_t TargetModuleRva{};
    };

    [[nodiscard]] std::uint32_t QueryProtection(
        const void* address) noexcept
    {
        MEMORY_BASIC_INFORMATION information{};
        if (address == nullptr ||
            VirtualQuery(address, &information, sizeof(information)) !=
                sizeof(information) ||
            information.State != MEM_COMMIT)
        {
            return 0;
        }
        return information.Protect;
    }

    [[nodiscard]] std::uint32_t ModuleImageSize(HMODULE module) noexcept
    {
        if (module == nullptr)
        {
            return 0;
        }
        const auto* base = reinterpret_cast<const std::byte*>(module);
        const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0)
        {
            return 0;
        }
        const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(
            base + dos->e_lfanew);
        return nt->Signature == IMAGE_NT_SIGNATURE
            ? nt->OptionalHeader.SizeOfImage
            : 0;
    }

    [[nodiscard]] bool RvaInIat(
        ImageView image,
        std::uint32_t rva) noexcept
    {
        if (image.Base == nullptr || image.Size < sizeof(IMAGE_DOS_HEADER))
        {
            return false;
        }
        const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(image.Base);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0)
        {
            return false;
        }
        const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(
            image.Base + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE)
        {
            return false;
        }
        const IMAGE_DATA_DIRECTORY directory =
            nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IAT];
        if (directory.VirtualAddress == 0 || directory.Size == 0)
        {
            return false;
        }
        return rva >= directory.VirtualAddress &&
            rva - directory.VirtualAddress < directory.Size;
    }

    [[nodiscard]] TailSlotInspection InspectTailSlot(
        ImageView image,
        RuntimeFunctionView function,
        std::size_t instructionOffset,
        const hde64s& instruction) noexcept
    {
        TailSlotInspection result{};
        result.ImageSize = static_cast<std::uint32_t>(
            std::min<std::size_t>(image.Size, UINT32_MAX));
        if (instruction.opcode != 0xFFu ||
            (instruction.flags & F_MODRM) == 0 ||
            instruction.modrm_reg != 4u ||
            instruction.modrm_mod != 0u ||
            instruction.modrm_rm != 5u ||
            (instruction.flags & F_SIB) != 0 ||
            (instruction.flags & F_DISP32) == 0 ||
            instruction.p_67 != 0)
        {
            return result;
        }
        result.SlotFlags |= TailSlotFlagRipRelative;
        const std::int64_t slotRelative =
            static_cast<std::int64_t>(function.BeginRva) +
            static_cast<std::int64_t>(instructionOffset) +
            static_cast<std::int64_t>(instruction.len) +
            static_cast<std::int32_t>(instruction.disp.disp32);
        const std::uintptr_t imageAddress =
            reinterpret_cast<std::uintptr_t>(image.Base);
        if (slotRelative < 0 ||
            static_cast<std::uint64_t>(slotRelative) >
                UINTPTR_MAX - imageAddress)
        {
            return result;
        }
        const std::uintptr_t slotAddress =
            imageAddress + static_cast<std::uintptr_t>(slotRelative);
        result.SlotAddress = static_cast<std::uint64_t>(slotAddress);
        result.SlotProtection = QueryProtection(
            reinterpret_cast<const void*>(slotAddress));
        if (slotAddress >= imageAddress &&
            slotAddress - imageAddress < image.Size)
        {
            result.SlotFlags |= TailSlotFlagInImage;
            result.SlotRva = static_cast<std::uint32_t>(
                slotAddress - imageAddress);
            if (RvaInIat(image, result.SlotRva))
            {
                result.SlotFlags |= TailSlotFlagInIat;
            }
        }
        const auto* slot = reinterpret_cast<const std::byte*>(slotAddress);
        if (!IsReadableAddressRange(slot, sizeof(std::uintptr_t)))
        {
            return result;
        }
        result.SlotFlags |= TailSlotFlagReadable;
        std::uintptr_t targetAddress{};
        std::memcpy(&targetAddress, slot, sizeof(targetAddress));
        result.TargetAddress = static_cast<std::uint64_t>(targetAddress);
        result.TargetProtection = QueryProtection(
            reinterpret_cast<const void*>(targetAddress));
        if (IsReadableAddressRange(
                reinterpret_cast<const std::byte*>(targetAddress),
                1u))
        {
            result.TargetFlags |= TailTargetFlagReadable;
        }
        if (IsExecutableAddress(
                reinterpret_cast<const std::byte*>(targetAddress)))
        {
            result.TargetFlags |= TailTargetFlagExecutable;
        }
        if (targetAddress >= imageAddress &&
            targetAddress - imageAddress < image.Size)
        {
            result.TargetFlags |= TailTargetFlagInMainImage;
        }
        HMODULE module{};
        if (targetAddress != 0 &&
            GetModuleHandleExW(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                    GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCWSTR>(targetAddress),
                &module) != FALSE &&
            module != nullptr)
        {
            result.TargetFlags |= TailTargetFlagModuleResolved;
            const std::uintptr_t moduleBase =
                reinterpret_cast<std::uintptr_t>(module);
            result.TargetModuleBase = static_cast<std::uint64_t>(moduleBase);
            result.TargetModuleSize = ModuleImageSize(module);
            if (targetAddress >= moduleBase &&
                targetAddress - moduleBase <= UINT32_MAX)
            {
                result.TargetModuleRva = static_cast<std::uint32_t>(
                    targetAddress - moduleBase);
            }
            if (moduleBase != reinterpret_cast<std::uintptr_t>(image.Base))
            {
                result.TargetFlags |= TailTargetFlagOtherModule;
            }
        }
        return result;
    }

    [[nodiscard]] std::optional<std::uint32_t> IndirectTailTargetRva(
        ImageView image,
        RuntimeFunctionView function,
        std::size_t instructionOffset,
        const hde64s& instruction) noexcept
    {
        if (instruction.opcode != 0xFFu ||
            (instruction.flags & F_MODRM) == 0 ||
            instruction.modrm_reg != 4u ||
            instruction.modrm_mod != 0u ||
            instruction.modrm_rm != 5u ||
            (instruction.flags & F_SIB) != 0 ||
            (instruction.flags & F_DISP32) == 0 ||
            instruction.p_67 != 0)
        {
            return std::nullopt;
        }

        const std::int64_t slot =
            static_cast<std::int64_t>(function.BeginRva) +
            static_cast<std::int64_t>(instructionOffset) +
            static_cast<std::int64_t>(instruction.len) +
            static_cast<std::int32_t>(instruction.disp.disp32);
        if (slot < 0 ||
            slot + static_cast<std::int64_t>(sizeof(std::uintptr_t)) >
                static_cast<std::int64_t>(image.Size))
        {
            return std::nullopt;
        }

        const auto* slotAddress =
            image.Base + static_cast<std::size_t>(slot);
        if (!IsReadableAddressRange(slotAddress, sizeof(std::uintptr_t)))
        {
            return std::nullopt;
        }

        std::uintptr_t targetAddress{};
        std::memcpy(
            &targetAddress,
            slotAddress,
            sizeof(targetAddress));

        const std::uintptr_t imageAddress =
            reinterpret_cast<std::uintptr_t>(image.Base);
        if (targetAddress < imageAddress ||
            targetAddress - imageAddress >= image.Size)
        {
            return std::nullopt;
        }

        const auto* target = reinterpret_cast<const std::byte*>(targetAddress);
        if (!IsExecutableAddress(target))
        {
            return std::nullopt;
        }

        return static_cast<std::uint32_t>(targetAddress - imageAddress);
    }

    [[nodiscard]] std::optional<std::uint8_t> ExplicitRegisterWrite(
        const hde64s& instruction) noexcept
    {
        if (instruction.opcode == 0x8Bu ||
            instruction.opcode == 0x8Du ||
            instruction.opcode == 0x63u ||
            instruction.opcode == 0x03u ||
            instruction.opcode == 0x0Bu ||
            instruction.opcode == 0x23u ||
            instruction.opcode == 0x2Bu ||
            instruction.opcode == 0x33u)
        {
            if ((instruction.flags & F_MODRM) != 0)
            {
                return ModRmRegRegister(instruction);
            }
        }

        if (instruction.opcode == 0x0Fu &&
            (instruction.opcode2 == 0xB6u ||
             instruction.opcode2 == 0xB7u ||
             instruction.opcode2 == 0xBEu ||
             instruction.opcode2 == 0xBFu) &&
            (instruction.flags & F_MODRM) != 0)
        {
            return ModRmRegRegister(instruction);
        }

        if (instruction.opcode == 0x89u &&
            (instruction.flags & F_MODRM) != 0 &&
            instruction.modrm_mod == 3u)
        {
            return static_cast<std::uint8_t>(
                instruction.modrm_rm |
                (static_cast<std::uint8_t>(instruction.rex_b) << 3));
        }

        if ((instruction.opcode == 0x01u ||
             instruction.opcode == 0x09u ||
             instruction.opcode == 0x21u ||
             instruction.opcode == 0x29u ||
             instruction.opcode == 0x31u ||
             instruction.opcode == 0x81u ||
             instruction.opcode == 0x83u ||
             instruction.opcode == 0xC7u) &&
            (instruction.flags & F_MODRM) != 0 &&
            instruction.modrm_mod == 3u)
        {
            return static_cast<std::uint8_t>(
                instruction.modrm_rm |
                (static_cast<std::uint8_t>(instruction.rex_b) << 3));
        }

        if (instruction.opcode >= 0x58u && instruction.opcode <= 0x5Fu)
        {
            return static_cast<std::uint8_t>(
                (instruction.opcode - 0x58u) |
                (static_cast<std::uint8_t>(instruction.rex_b) << 3));
        }

        if (instruction.opcode >= 0xB8u && instruction.opcode <= 0xBFu)
        {
            return static_cast<std::uint8_t>(
                (instruction.opcode - 0xB8u) |
                (static_cast<std::uint8_t>(instruction.rex_b) << 3));
        }

        return std::nullopt;
    }

    void ClearVolatileProvenance(
        std::uint32_t& eventRegisters,
        std::uint32_t& vtableRegisters) noexcept
    {
        constexpr std::array<std::uint8_t, 7> VolatileRegisters{
            0u, 1u, 2u, 8u, 9u, 10u, 11u
        };
        for (const std::uint8_t index : VolatileRegisters)
        {
            const std::uint32_t bit = RegisterBit(index);
            eventRegisters &= ~bit;
            vtableRegisters &= ~bit;
        }
    }

    constexpr std::array<std::uint8_t, 4> DispatchArgumentRegisters{
        1u, 2u, 8u, 9u
    };

    [[nodiscard]] std::uint32_t ArgumentMaskFromRegisters(
        std::uint32_t registers) noexcept
    {
        std::uint32_t mask = 0;
        for (std::size_t index = 0;
             index < DispatchArgumentRegisters.size();
             ++index)
        {
            if ((registers &
                 RegisterBit(DispatchArgumentRegisters[index])) != 0)
            {
                mask |= 1u << index;
            }
        }
        return mask;
    }

    [[nodiscard]] std::uint32_t RegistersFromArgumentMask(
        std::uint32_t mask) noexcept
    {
        std::uint32_t registers = 0;
        for (std::size_t index = 0;
             index < DispatchArgumentRegisters.size();
             ++index)
        {
            if ((mask & (1u << index)) != 0)
            {
                registers |= RegisterBit(
                    DispatchArgumentRegisters[index]);
            }
        }
        return registers;
    }

    [[nodiscard]] bool IsTrackedFrameBase(
        std::uint8_t index,
        std::uint32_t frameRegisters) noexcept
    {
        return (frameRegisters & RegisterBit(index)) != 0;
    }

    [[nodiscard]] std::uint32_t ReadStackProvenance(
        std::span<const StackProvenanceSlot> slots,
        std::uint8_t base,
        std::int32_t displacement) noexcept
    {
        const auto found = std::ranges::find_if(
            slots,
            [base, displacement](const StackProvenanceSlot& slot)
            {
                return slot.Base == base &&
                    slot.Displacement == displacement;
            });
        return found == slots.end() ? 0u : found->Provenance;
    }

    void WriteStackProvenance(
        std::vector<StackProvenanceSlot>& slots,
        std::uint8_t base,
        std::int32_t displacement,
        std::uint32_t provenance)
    {
        const auto found = std::ranges::find_if(
            slots,
            [base, displacement](const StackProvenanceSlot& slot)
            {
                return slot.Base == base &&
                    slot.Displacement == displacement;
            });

        if (provenance == 0)
        {
            if (found != slots.end())
            {
                slots.erase(found);
            }
            return;
        }

        if (found == slots.end())
        {
            slots.push_back({base, displacement, provenance});
            return;
        }

        found->Provenance = provenance;
    }

    [[nodiscard]] std::uint32_t ProvenanceForRegister(
        std::uint8_t index,
        std::uint32_t groupRegisters,
        std::uint32_t eventRegisters,
        std::uint32_t vtableRegisters) noexcept
    {
        const std::uint32_t bit = RegisterBit(index);
        std::uint32_t provenance = 0;
        if ((groupRegisters & bit) != 0)
        {
            provenance |= ProvenanceGroup;
        }
        if ((eventRegisters & bit) != 0)
        {
            provenance |= ProvenanceEvent;
        }
        if ((vtableRegisters & bit) != 0)
        {
            provenance |= ProvenanceVtable;
        }
        return provenance;
    }

    void ApplyProvenance(
        std::uint32_t provenance,
        std::uint32_t destinationBit,
        std::uint32_t& groupRegisters,
        std::uint32_t& eventRegisters,
        std::uint32_t& vtableRegisters) noexcept
    {
        if ((provenance & ProvenanceGroup) != 0)
        {
            groupRegisters |= destinationBit;
        }
        if ((provenance & ProvenanceEvent) != 0)
        {
            eventRegisters |= destinationBit;
        }
        if ((provenance & ProvenanceVtable) != 0)
        {
            vtableRegisters |= destinationBit;
        }
    }

    [[nodiscard]] DispatchFunctionAnalysis AnalyzeDispatchFunction(
        ImageView image,
        RuntimeFunctionView function,
        std::uint32_t initialGroupRegisters,
        std::uint32_t initialEventRegisters,
        std::uint32_t eventTypeVFuncOffset,
        std::uint32_t eventArgumentsVFuncOffset,
        std::uint32_t eventEntityVFuncOffset)
    {
        DispatchFunctionAnalysis analysis{};
        std::uint32_t groupRegisters = initialGroupRegisters;
        std::uint32_t eventRegisters = initialEventRegisters;
        std::uint32_t vtableRegisters = 0;
        std::uint32_t frameRegisters = RegisterBit(4u);
        std::vector<StackProvenanceSlot> stackSlots;

        const auto clearVolatile = [
            &groupRegisters,
            &eventRegisters,
            &vtableRegisters]()
        {
            constexpr std::array<std::uint8_t, 7> VolatileRegisters{
                0u, 1u, 2u, 8u, 9u, 10u, 11u
            };
            for (const std::uint8_t index : VolatileRegisters)
            {
                const std::uint32_t bit = RegisterBit(index);
                groupRegisters &= ~bit;
                eventRegisters &= ~bit;
                vtableRegisters &= ~bit;
            }
        };

        static_cast<void>(VisitDecodedInstructions(
            FunctionBytes(image, function),
            [image,
             function,
             eventTypeVFuncOffset,
             eventArgumentsVFuncOffset,
             eventEntityVFuncOffset,
             &analysis,
             &groupRegisters,
             &eventRegisters,
             &vtableRegisters,
             &frameRegisters,
             &stackSlots,
             &clearVolatile](
                std::size_t instructionOffset,
                const hde64s& instruction)
            {
                if (instruction.opcode == 0xE8u)
                {
                    const std::uint32_t eventArgumentMask =
                        ArgumentMaskFromRegisters(eventRegisters);
                    const std::uint32_t groupArgumentMask =
                        ArgumentMaskFromRegisters(groupRegisters);
                    if (eventArgumentMask != 0)
                    {
                        analysis.Relations |=
                            DispatchRelationForwardsEvent;
                    }
                    if (groupArgumentMask != 0)
                    {
                        analysis.Relations |=
                            DispatchRelationForwardsGroup;
                    }

                    if (eventArgumentMask != 0)
                    {
                        if (const auto targetRva = DirectCallTargetRva(
                                image,
                                function,
                                instructionOffset,
                                instruction);
                            targetRva.has_value())
                        {
                            const RuntimeFunctionView target =
                                ResolveRuntimeFunction(
                                    image,
                                    image.Base + *targetRva);
                            const std::uint32_t normalizedRva =
                                target ? target.BeginRva : *targetRva;
                            if (normalizedRva != function.BeginRva)
                            {
                                analysis.ForwardCallsites.push_back({
                                    function.BeginRva,
                                    static_cast<std::uint32_t>(
                                        function.BeginRva +
                                        instructionOffset),
                                    normalizedRva,
                                    eventArgumentMask,
                                    groupArgumentMask
                                });
                            }
                        }
                    }

                    clearVolatile();
                    return false;
                }

                if (instruction.opcode == 0xFFu &&
                    (instruction.flags & F_MODRM) != 0 &&
                    instruction.modrm_reg == 2u)
                {
                    const std::uint32_t eventArgumentMask =
                        ArgumentMaskFromRegisters(eventRegisters);
                    const std::uint32_t groupArgumentMask =
                        ArgumentMaskFromRegisters(groupRegisters);
                    if (eventArgumentMask != 0)
                    {
                        analysis.Relations |=
                            DispatchRelationForwardsEvent;
                    }
                    if (groupArgumentMask != 0)
                    {
                        analysis.Relations |=
                            DispatchRelationForwardsGroup;
                    }

                    if (instruction.modrm_mod != 3u)
                    {
                        const auto displacement =
                            MemoryDisplacement(instruction);
                        const auto base =
                            MemoryBaseRegister(instruction);
                        if (displacement.has_value() &&
                            base.has_value() &&
                            *displacement >= 0 &&
                            (vtableRegisters &
                             RegisterBit(*base)) != 0)
                        {
                            const std::uint32_t slot =
                                static_cast<std::uint32_t>(
                                    *displacement);
                            if (slot == eventTypeVFuncOffset)
                            {
                                analysis.Relations |=
                                    DispatchRelationCallsEventTypeVirtual;
                            }
                            if (slot == eventArgumentsVFuncOffset)
                            {
                                analysis.Relations |=
                                    DispatchRelationCallsEventArgumentsVirtual;
                            }
                            if (slot == eventEntityVFuncOffset)
                            {
                                analysis.Relations |=
                                    DispatchRelationCallsEventEntityVirtual;
                            }
                        }
                    }

                    clearVolatile();
                    return false;
                }

                if (instruction.opcode == 0x89u &&
                    instruction.rex_w != 0 &&
                    (instruction.flags & F_MODRM) != 0 &&
                    instruction.modrm_mod != 3u)
                {
                    const auto base =
                        MemoryBaseRegister(instruction);
                    const auto displacement =
                        MemoryDisplacement(instruction);
                    if (base.has_value() &&
                        displacement.has_value() &&
                        IsTrackedFrameBase(*base, frameRegisters))
                    {
                        const std::uint8_t source =
                            ModRmRegRegister(instruction);
                        const std::uint32_t provenance =
                            ProvenanceForRegister(
                                source,
                                groupRegisters,
                                eventRegisters,
                                vtableRegisters);
                        WriteStackProvenance(
                            stackSlots,
                            *base,
                            *displacement,
                            provenance);
                        if ((provenance & ProvenanceEvent) != 0)
                        {
                            analysis.Relations |=
                                DispatchRelationEventSpilledToStack;
                        }
                        if ((provenance & ProvenanceGroup) != 0)
                        {
                            analysis.Relations |=
                                DispatchRelationGroupSpilledToStack;
                        }
                    }
                    return false;
                }

                const auto destination =
                    ExplicitRegisterWrite(instruction);
                if (!destination.has_value())
                {
                    return false;
                }

                const std::uint32_t destinationBit =
                    RegisterBit(*destination);
                groupRegisters &= ~destinationBit;
                eventRegisters &= ~destinationBit;
                vtableRegisters &= ~destinationBit;
                frameRegisters &= ~destinationBit;

                if (instruction.opcode == 0x8Bu &&
                    instruction.rex_w != 0 &&
                    (instruction.flags & F_MODRM) != 0)
                {
                    if (instruction.modrm_mod == 3u)
                    {
                        if (const auto source =
                                RegisterSource(instruction);
                            source.has_value())
                        {
                            ApplyProvenance(
                                ProvenanceForRegister(
                                    *source,
                                    groupRegisters,
                                    eventRegisters,
                                    vtableRegisters),
                                destinationBit,
                                groupRegisters,
                                eventRegisters,
                                vtableRegisters);
                            if ((frameRegisters &
                                 RegisterBit(*source)) != 0)
                            {
                                frameRegisters |= destinationBit;
                            }
                        }
                    }
                    else if (const auto base =
                                 MemoryBaseRegister(instruction);
                             base.has_value())
                    {
                        const std::int32_t displacement =
                            MemoryDisplacement(instruction).value_or(0);
                        if (IsTrackedFrameBase(*base, frameRegisters))
                        {
                            const std::uint32_t provenance =
                                ReadStackProvenance(
                                    stackSlots,
                                    *base,
                                    displacement);
                            ApplyProvenance(
                                provenance,
                                destinationBit,
                                groupRegisters,
                                eventRegisters,
                                vtableRegisters);
                            if ((provenance & ProvenanceEvent) != 0)
                            {
                                analysis.Relations |=
                                    DispatchRelationEventReloadedFromStack;
                            }
                            if ((provenance & ProvenanceGroup) != 0)
                            {
                                analysis.Relations |=
                                    DispatchRelationGroupReloadedFromStack;
                            }
                        }
                        else
                        {
                            const std::uint32_t baseBit =
                                RegisterBit(*base);
                            if ((groupRegisters & baseBit) != 0)
                            {
                                analysis.Relations |=
                                    DispatchRelationIncomingGroupRead;
                            }
                            if ((eventRegisters & baseBit) != 0)
                            {
                                analysis.Relations |=
                                    DispatchRelationIncomingEventRead;
                                if (displacement == 0)
                                {
                                    vtableRegisters |= destinationBit;
                                    analysis.Relations |=
                                        DispatchRelationEventVtableLoaded;
                                }
                            }
                        }
                    }
                }
                else if (instruction.opcode == 0x89u &&
                         instruction.rex_w != 0 &&
                         (instruction.flags & F_MODRM) != 0 &&
                         instruction.modrm_mod == 3u)
                {
                    const std::uint8_t source =
                        ModRmRegRegister(instruction);
                    ApplyProvenance(
                        ProvenanceForRegister(
                            source,
                            groupRegisters,
                            eventRegisters,
                            vtableRegisters),
                        destinationBit,
                        groupRegisters,
                        eventRegisters,
                        vtableRegisters);
                    if ((frameRegisters &
                         RegisterBit(source)) != 0)
                    {
                        frameRegisters |= destinationBit;
                    }
                }
                else if (instruction.opcode == 0x8Du &&
                         (instruction.flags & F_MODRM) != 0)
                {
                    if (const auto base =
                            MemoryBaseRegister(instruction);
                        base.has_value())
                    {
                        ApplyProvenance(
                            ProvenanceForRegister(
                                *base,
                                groupRegisters,
                                eventRegisters,
                                vtableRegisters),
                            destinationBit,
                            groupRegisters,
                            eventRegisters,
                            vtableRegisters);
                        if ((frameRegisters &
                             RegisterBit(*base)) != 0)
                        {
                            frameRegisters |= destinationBit;
                        }
                    }
                }

                return false;
            }));

        return analysis;
    }

    [[nodiscard]] std::uint8_t ModRmRmRegister(
        const hde64s& instruction) noexcept
    {
        return static_cast<std::uint8_t>(
            instruction.modrm_rm |
            (static_cast<std::uint8_t>(instruction.rex_b) << 3));
    }

    [[nodiscard]] bool InstructionComparesEvent(
        const hde64s& instruction,
        std::uint32_t eventRegisters) noexcept
    {
        if ((instruction.flags & F_MODRM) == 0)
        {
            return false;
        }

        const std::uint32_t regBit = RegisterBit(ModRmRegRegister(instruction));
        if (instruction.modrm_mod == 3u)
        {
            const std::uint32_t rmBit = RegisterBit(ModRmRmRegister(instruction));
            if (instruction.opcode == 0x39u ||
                instruction.opcode == 0x3Bu ||
                instruction.opcode == 0x85u)
            {
                return ((regBit | rmBit) & eventRegisters) != 0;
            }
            if ((instruction.opcode == 0x81u || instruction.opcode == 0x83u) &&
                instruction.modrm_reg == 7u)
            {
                return (rmBit & eventRegisters) != 0;
            }
            if (instruction.opcode == 0xF7u && instruction.modrm_reg == 0u)
            {
                return (rmBit & eventRegisters) != 0;
            }
            return false;
        }

        const auto base = MemoryBaseRegister(instruction);
        const bool eventBase = base.has_value() &&
            (eventRegisters & RegisterBit(*base)) != 0;
        if (instruction.opcode == 0x39u ||
            instruction.opcode == 0x3Bu ||
            instruction.opcode == 0x85u)
        {
            return eventBase || (regBit & eventRegisters) != 0;
        }
        if ((instruction.opcode == 0x81u || instruction.opcode == 0x83u) &&
            instruction.modrm_reg == 7u)
        {
            return eventBase;
        }
        if (instruction.opcode == 0xF7u && instruction.modrm_reg == 0u)
        {
            return eventBase;
        }
        return false;
    }

    [[nodiscard]] std::optional<std::uint32_t> MemoryIndirectTailTargetRva(
        ImageView image,
        RuntimeFunctionView function,
        std::size_t tailInstructionOffset,
        const hde64s& tailInstruction) noexcept
    {
        if (tailInstruction.opcode != 0xFFu ||
            (tailInstruction.flags & F_MODRM) == 0 ||
            tailInstruction.modrm_reg != 4u ||
            tailInstruction.modrm_mod == 3u ||
            tailInstruction.p_67 != 0)
        {
            return std::nullopt;
        }

        std::array<std::optional<std::uintptr_t>, 16> values{};

        const auto setValue = [&values](
            std::uint8_t destination,
            std::optional<std::uintptr_t> value)
        {
            if (destination < values.size())
            {
                values[destination] = value;
            }
        };

        static_cast<void>(VisitDecodedInstructions(
            FunctionBytes(image, function),
            [image,
             function,
             tailInstructionOffset,
             &values,
             &setValue](
                std::size_t instructionOffset,
                const hde64s& instruction)
            {
                if (instructionOffset >= tailInstructionOffset)
                {
                    return true;
                }

                if (instruction.opcode >= 0xB8u &&
                    instruction.opcode <= 0xBFu &&
                    instruction.rex_w != 0 &&
                    (instruction.flags & F_IMM64) != 0)
                {
                    const std::uint8_t destination = static_cast<std::uint8_t>(
                        (instruction.opcode - 0xB8u) |
                        (static_cast<std::uint8_t>(instruction.rex_b) << 3));
                    setValue(
                        destination,
                        static_cast<std::uintptr_t>(instruction.imm.imm64));
                    return false;
                }

                if ((instruction.opcode == 0x8Du ||
                     instruction.opcode == 0x8Bu) &&
                    instruction.rex_w != 0 &&
                    (instruction.flags & F_MODRM) != 0 &&
                    instruction.modrm_mod == 0u &&
                    instruction.modrm_rm == 5u &&
                    (instruction.flags & F_SIB) == 0 &&
                    (instruction.flags & F_DISP32) != 0 &&
                    instruction.p_67 == 0)
                {
                    const std::uint8_t destination =
                        ModRmRegRegister(instruction);
                    const std::int64_t relativeAddress =
                        static_cast<std::int64_t>(function.BeginRva) +
                        static_cast<std::int64_t>(instructionOffset) +
                        static_cast<std::int64_t>(instruction.len) +
                        static_cast<std::int32_t>(instruction.disp.disp32);
                    if (relativeAddress < 0 ||
                        relativeAddress >= static_cast<std::int64_t>(image.Size))
                    {
                        setValue(destination, std::nullopt);
                        return false;
                    }

                    const std::uintptr_t address =
                        reinterpret_cast<std::uintptr_t>(image.Base) +
                        static_cast<std::uintptr_t>(relativeAddress);
                    if (instruction.opcode == 0x8Du)
                    {
                        setValue(destination, address);
                        return false;
                    }

                    const auto* slotAddress =
                        reinterpret_cast<const std::byte*>(address);
                    if (!IsReadableAddressRange(
                            slotAddress,
                            sizeof(std::uintptr_t)))
                    {
                        setValue(destination, std::nullopt);
                        return false;
                    }

                    std::uintptr_t loaded{};
                    std::memcpy(
                        &loaded,
                        slotAddress,
                        sizeof(loaded));
                    setValue(destination, loaded);
                    return false;
                }

                if ((instruction.opcode == 0x8Bu ||
                     instruction.opcode == 0x89u) &&
                    instruction.rex_w != 0 &&
                    (instruction.flags & F_MODRM) != 0 &&
                    instruction.modrm_mod == 3u)
                {
                    const std::uint8_t destination =
                        instruction.opcode == 0x8Bu
                            ? ModRmRegRegister(instruction)
                            : ModRmRmRegister(instruction);
                    const std::uint8_t source =
                        instruction.opcode == 0x8Bu
                            ? ModRmRmRegister(instruction)
                            : ModRmRegRegister(instruction);
                    setValue(
                        destination,
                        source < values.size()
                            ? values[source]
                            : std::optional<std::uintptr_t>{});
                    return false;
                }

                if (instruction.opcode == 0x8Du &&
                    instruction.rex_w != 0 &&
                    (instruction.flags & F_MODRM) != 0 &&
                    instruction.modrm_mod != 3u)
                {
                    const auto base = MemoryBaseRegister(instruction);
                    const auto index = MemoryIndexRegister(instruction);
                    const auto displacement = MemoryDisplacement(instruction);
                    if (!displacement.has_value() ||
                        (base.has_value() &&
                         (*base >= values.size() || !values[*base].has_value())) ||
                        (index.has_value() &&
                         (*index >= values.size() || !values[*index].has_value())))
                    {
                        setValue(
                            ModRmRegRegister(instruction),
                            std::nullopt);
                        return false;
                    }

                    std::uintptr_t address = 0;
                    if (base.has_value())
                    {
                        address = *values[*base];
                    }
                    if (index.has_value())
                    {
                        const std::uintptr_t scale =
                            std::uintptr_t{1} << instruction.sib_scale;
                        const std::uintptr_t indexValue = *values[*index];
                        if (indexValue > UINTPTR_MAX / scale)
                        {
                            setValue(
                                ModRmRegRegister(instruction),
                                std::nullopt);
                            return false;
                        }
                        const std::uintptr_t scaled = indexValue * scale;
                        if (address > UINTPTR_MAX - scaled)
                        {
                            setValue(
                                ModRmRegRegister(instruction),
                                std::nullopt);
                            return false;
                        }
                        address += scaled;
                    }

                    const std::int64_t signedAddress =
                        static_cast<std::int64_t>(address) +
                        static_cast<std::int64_t>(*displacement);
                    if (signedAddress < 0)
                    {
                        setValue(
                            ModRmRegRegister(instruction),
                            std::nullopt);
                        return false;
                    }

                    setValue(
                        ModRmRegRegister(instruction),
                        static_cast<std::uintptr_t>(signedAddress));
                    return false;
                }

                if ((instruction.opcode == 0x81u ||
                     instruction.opcode == 0x83u) &&
                    instruction.rex_w != 0 &&
                    (instruction.flags & F_MODRM) != 0 &&
                    instruction.modrm_mod == 3u &&
                    (instruction.modrm_reg == 0u ||
                     instruction.modrm_reg == 5u))
                {
                    const std::uint8_t destination =
                        ModRmRmRegister(instruction);
                    if (destination >= values.size() ||
                        !values[destination].has_value())
                    {
                        setValue(destination, std::nullopt);
                        return false;
                    }

                    std::int64_t immediate{};
                    if ((instruction.flags & F_IMM8) != 0)
                    {
                        immediate =
                            static_cast<std::int8_t>(instruction.imm.imm8);
                    }
                    else if ((instruction.flags & F_IMM32) != 0)
                    {
                        immediate =
                            static_cast<std::int32_t>(instruction.imm.imm32);
                    }
                    else
                    {
                        setValue(destination, std::nullopt);
                        return false;
                    }

                    const std::int64_t delta =
                        instruction.modrm_reg == 0u
                            ? immediate
                            : -immediate;
                    const std::int64_t updated =
                        static_cast<std::int64_t>(*values[destination]) +
                        delta;
                    setValue(
                        destination,
                        updated >= 0
                            ? std::optional<std::uintptr_t>{
                                static_cast<std::uintptr_t>(updated)}
                            : std::optional<std::uintptr_t>{});
                    return false;
                }

                if (const auto destination = ExplicitRegisterWrite(instruction);
                    destination.has_value() && *destination < values.size())
                {
                    values[*destination].reset();
                }
                return false;
            }));

        const auto base = MemoryBaseRegister(tailInstruction);
        const auto index = MemoryIndexRegister(tailInstruction);
        const auto displacement = MemoryDisplacement(tailInstruction);
        if (!displacement.has_value() ||
            !base.has_value() ||
            *base >= values.size() ||
            !values[*base].has_value() ||
            (index.has_value() &&
             (*index >= values.size() || !values[*index].has_value())))
        {
            return std::nullopt;
        }

        std::uintptr_t slotAddress = *values[*base];
        if (index.has_value())
        {
            const std::uintptr_t scale =
                std::uintptr_t{1} << tailInstruction.sib_scale;
            const std::uintptr_t indexValue = *values[*index];
            if (indexValue > UINTPTR_MAX / scale)
            {
                return std::nullopt;
            }
            const std::uintptr_t scaled = indexValue * scale;
            if (slotAddress > UINTPTR_MAX - scaled)
            {
                return std::nullopt;
            }
            slotAddress += scaled;
        }

        const std::int64_t signedSlot =
            static_cast<std::int64_t>(slotAddress) +
            static_cast<std::int64_t>(*displacement);
        if (signedSlot < 0)
        {
            return std::nullopt;
        }
        slotAddress = static_cast<std::uintptr_t>(signedSlot);

        const std::uintptr_t imageAddress =
            reinterpret_cast<std::uintptr_t>(image.Base);
        if (slotAddress < imageAddress ||
            slotAddress - imageAddress >
                image.Size - std::min<std::size_t>(
                    image.Size,
                    sizeof(std::uintptr_t)))
        {
            return std::nullopt;
        }

        const auto* slot =
            reinterpret_cast<const std::byte*>(slotAddress);
        if (!IsReadableAddressRange(slot, sizeof(std::uintptr_t)))
        {
            return std::nullopt;
        }

        std::uintptr_t targetAddress{};
        std::memcpy(
            &targetAddress,
            slot,
            sizeof(targetAddress));
        if (targetAddress < imageAddress ||
            targetAddress - imageAddress >= image.Size)
        {
            return std::nullopt;
        }

        const auto* target =
            reinterpret_cast<const std::byte*>(targetAddress);
        if (!IsExecutableAddress(target))
        {
            return std::nullopt;
        }

        return static_cast<std::uint32_t>(targetAddress - imageAddress);
    }

    [[nodiscard]] std::optional<std::uint32_t> RegisterIndirectTailTargetRva(
        ImageView image,
        RuntimeFunctionView function,
        std::size_t tailInstructionOffset,
        const hde64s& tailInstruction) noexcept
    {
        if (tailInstruction.opcode != 0xFFu ||
            (tailInstruction.flags & F_MODRM) == 0 ||
            tailInstruction.modrm_reg != 4u ||
            tailInstruction.modrm_mod != 3u)
        {
            return std::nullopt;
        }

        const std::uint8_t targetRegister = ModRmRmRegister(tailInstruction);
        std::array<std::optional<std::uint32_t>, 16> targets{};

        static_cast<void>(VisitDecodedInstructions(
            FunctionBytes(image, function),
            [image,
             function,
             tailInstructionOffset,
             targetRegister,
             &targets](
                std::size_t instructionOffset,
                const hde64s& instruction)
            {
                if (instructionOffset >= tailInstructionOffset)
                {
                    return true;
                }

                auto setExecutableTarget =
                    [image, &targets](std::uint8_t destination, std::uintptr_t address)
                    {
                        if (destination >= targets.size())
                        {
                            return;
                        }
                        const std::uintptr_t imageAddress =
                            reinterpret_cast<std::uintptr_t>(image.Base);
                        if (address < imageAddress ||
                            address - imageAddress >= image.Size)
                        {
                            targets[destination].reset();
                            return;
                        }
                        const auto* target =
                            reinterpret_cast<const std::byte*>(address);
                        if (!IsExecutableAddress(target))
                        {
                            targets[destination].reset();
                            return;
                        }
                        targets[destination] = static_cast<std::uint32_t>(
                            address - imageAddress);
                    };

                if (instruction.opcode >= 0xB8u &&
                    instruction.opcode <= 0xBFu &&
                    instruction.rex_w != 0 &&
                    (instruction.flags & F_IMM64) != 0)
                {
                    const std::uint8_t destination = static_cast<std::uint8_t>(
                        (instruction.opcode - 0xB8u) |
                        (static_cast<std::uint8_t>(instruction.rex_b) << 3));
                    setExecutableTarget(
                        destination,
                        static_cast<std::uintptr_t>(instruction.imm.imm64));
                    return false;
                }

                if ((instruction.opcode == 0x8Du ||
                     instruction.opcode == 0x8Bu) &&
                    instruction.rex_w != 0 &&
                    (instruction.flags & F_MODRM) != 0 &&
                    instruction.modrm_mod == 0u &&
                    instruction.modrm_rm == 5u &&
                    (instruction.flags & F_SIB) == 0 &&
                    (instruction.flags & F_DISP32) != 0 &&
                    instruction.p_67 == 0)
                {
                    const std::uint8_t destination =
                        ModRmRegRegister(instruction);
                    const std::int64_t relativeAddress =
                        static_cast<std::int64_t>(function.BeginRva) +
                        static_cast<std::int64_t>(instructionOffset) +
                        static_cast<std::int64_t>(instruction.len) +
                        static_cast<std::int32_t>(instruction.disp.disp32);
                    if (relativeAddress < 0 ||
                        relativeAddress >= static_cast<std::int64_t>(image.Size))
                    {
                        targets[destination].reset();
                        return false;
                    }

                    if (instruction.opcode == 0x8Du)
                    {
                        setExecutableTarget(
                            destination,
                            reinterpret_cast<std::uintptr_t>(image.Base) +
                                static_cast<std::uintptr_t>(relativeAddress));
                        return false;
                    }

                    const auto* slotAddress =
                        image.Base + static_cast<std::size_t>(relativeAddress);
                    if (!IsReadableAddressRange(
                            slotAddress,
                            sizeof(std::uintptr_t)))
                    {
                        targets[destination].reset();
                        return false;
                    }
                    std::uintptr_t targetAddress{};
                    std::memcpy(
                        &targetAddress,
                        slotAddress,
                        sizeof(targetAddress));
                    setExecutableTarget(destination, targetAddress);
                    return false;
                }

                if ((instruction.opcode == 0x8Bu ||
                     instruction.opcode == 0x89u) &&
                    instruction.rex_w != 0 &&
                    (instruction.flags & F_MODRM) != 0 &&
                    instruction.modrm_mod == 3u)
                {
                    const std::uint8_t destination =
                        instruction.opcode == 0x8Bu
                            ? ModRmRegRegister(instruction)
                            : ModRmRmRegister(instruction);
                    const std::uint8_t source =
                        instruction.opcode == 0x8Bu
                            ? ModRmRmRegister(instruction)
                            : ModRmRegRegister(instruction);
                    if (destination < targets.size() && source < targets.size())
                    {
                        targets[destination] = targets[source];
                    }
                    return false;
                }

                if (const auto destination = ExplicitRegisterWrite(instruction);
                    destination.has_value() && *destination < targets.size())
                {
                    targets[*destination].reset();
                }
                return false;
            }));

        if (targetRegister >= targets.size())
        {
            return std::nullopt;
        }
        return targets[targetRegister];
    }

    [[nodiscard]] EventConsumerFunctionAnalysis AnalyzeEventConsumerFunction(
        ImageView image,
        RuntimeFunctionView function,
        std::uint32_t initialEventRegisters)
    {
        EventConsumerFunctionAnalysis analysis{};
        std::uint32_t eventRegisters = initialEventRegisters;
        std::uint32_t vtableRegisters = 0;
        std::uint32_t preparedEventArgumentMask = 0;
        std::uint32_t frameRegisters = RegisterBit(4u);
        std::vector<StackProvenanceSlot> stackSlots;

        const auto markUse = [&analysis, function](
            std::size_t instructionOffset,
            std::uint32_t relation,
            std::uint32_t& count)
        {
            analysis.Relations |= relation;
            ++count;
            if (analysis.FirstUseRva == 0)
            {
                analysis.FirstUseRva = static_cast<std::uint32_t>(
                    function.BeginRva + instructionOffset);
            }
        };

        const auto markClobber = [&analysis, function](
            std::size_t instructionOffset)
        {
            if (analysis.FirstClobberRva == 0)
            {
                analysis.FirstClobberRva = static_cast<std::uint32_t>(
                    function.BeginRva + instructionOffset);
            }
        };

        static_cast<void>(VisitDecodedInstructions(
            FunctionBytes(image, function),
            [image,
             function,
             &analysis,
             &eventRegisters,
             &vtableRegisters,
             &preparedEventArgumentMask,
             &frameRegisters,
             &stackSlots,
             &markUse,
             &markClobber](
                std::size_t instructionOffset,
                const hde64s& instruction)
            {
                const std::uint32_t eventArgumentMask =
                    ArgumentMaskFromRegisters(eventRegisters);

                if (instruction.opcode == 0xE8u)
                {
                    const std::uint32_t forwardedMask =
                        eventArgumentMask & preparedEventArgumentMask;
                    if (forwardedMask != 0)
                    {
                        markUse(
                            instructionOffset,
                            EventConsumerRelationForward,
                            analysis.ForwardCount);
                    }
                    if ((eventRegisters & 0x00000F07u) != 0)
                    {
                        markClobber(instructionOffset);
                    }
                    ClearVolatileProvenance(eventRegisters, vtableRegisters);
                    preparedEventArgumentMask = 0;
                    return false;
                }

                if (instruction.opcode == 0xE9u)
                {
                    const std::uint32_t forwardedMask = eventArgumentMask;
                    if (forwardedMask != 0)
                    {
                        markUse(
                            instructionOffset,
                            EventConsumerRelationTailForward,
                            analysis.TailForwardCount);
                        if (const auto target = DirectTailTargetRva(
                                image,
                                function,
                                instructionOffset,
                                instruction);
                            target.has_value())
                        {
                            analysis.TailTargetRva = *target;
                            analysis.TailEventArgumentMask = forwardedMask;
                        }
                    }
                    return false;
                }

                if (instruction.opcode == 0xFFu &&
                    (instruction.flags & F_MODRM) != 0 &&
                    (instruction.modrm_reg == 2u ||
                     instruction.modrm_reg == 4u))
                {
                    const std::uint32_t forwardedMask =
                        instruction.modrm_reg == 4u
                            ? eventArgumentMask
                            : eventArgumentMask & preparedEventArgumentMask;
                    if (forwardedMask != 0)
                    {
                        if (instruction.modrm_reg == 2u)
                        {
                            markUse(
                                instructionOffset,
                                EventConsumerRelationForward,
                                analysis.ForwardCount);
                        }
                        else
                        {
                            markUse(
                                instructionOffset,
                                EventConsumerRelationTailForward,
                                analysis.TailForwardCount);
                            analysis.TailInstructionRva = static_cast<std::uint32_t>(
                                function.BeginRva + instructionOffset);
                            analysis.TailInstructionLength = instruction.len;
                            analysis.TailEncoding = PackInstructionBytes(
                                FunctionBytes(image, function),
                                instructionOffset,
                                instruction.len);
                            analysis.TailOperandInfo = PackOperandInfo(instruction);
                            analysis.TailDisplacement =
                                InstructionDisplacementBits(instruction);
                            if (const auto base = MemoryBaseRegister(instruction);
                                base.has_value())
                            {
                                analysis.TailBaseRegister = *base;
                            }
                            if (const auto index = MemoryIndexRegister(instruction);
                                index.has_value())
                            {
                                analysis.TailIndexRegister = *index;
                                analysis.TailScale =
                                    1u << instruction.sib_scale;
                            }
                            const TailSlotInspection slotInspection =
                                InspectTailSlot(
                                    image,
                                    function,
                                    instructionOffset,
                                    instruction);
                            analysis.TailImageSize = slotInspection.ImageSize;
                            analysis.TailSlotRva = slotInspection.SlotRva;
                            analysis.TailSlotAddress = slotInspection.SlotAddress;
                            analysis.TailSlotProtection = slotInspection.SlotProtection;
                            analysis.TailSlotFlags = slotInspection.SlotFlags;
                            analysis.TailTargetAddress = slotInspection.TargetAddress;
                            analysis.TailTargetProtection = slotInspection.TargetProtection;
                            analysis.TailTargetFlags = slotInspection.TargetFlags;
                            analysis.TailTargetModuleBase = slotInspection.TargetModuleBase;
                            analysis.TailTargetModuleSize = slotInspection.TargetModuleSize;
                            analysis.TailTargetModuleRva = slotInspection.TargetModuleRva;
                            analysis.Relations |=
                                instruction.modrm_mod == 3u
                                    ? EventConsumerRelationIndirectTailRegister
                                    : EventConsumerRelationIndirectTailMemory;
                            if (instruction.modrm_mod != 3u)
                            {
                                const auto tailBase =
                                    MemoryBaseRegister(instruction);
                                const auto tailIndex =
                                    MemoryIndexRegister(instruction);
                                if ((tailBase.has_value() &&
                                     (eventRegisters &
                                      RegisterBit(*tailBase)) != 0) ||
                                    (tailIndex.has_value() &&
                                     (eventRegisters &
                                      RegisterBit(*tailIndex)) != 0))
                                {
                                    analysis.Relations |=
                                        EventConsumerRelationMemoryTailEventOperand;
                                }
                                if ((tailBase.has_value() &&
                                     (vtableRegisters &
                                      RegisterBit(*tailBase)) != 0) ||
                                    (tailIndex.has_value() &&
                                     (vtableRegisters &
                                      RegisterBit(*tailIndex)) != 0))
                                {
                                    analysis.Relations |=
                                        EventConsumerRelationMemoryTailVtableOperand;
                                }
                                if ((instruction.flags & F_SIB) != 0)
                                {
                                    analysis.Relations |=
                                        EventConsumerRelationMemoryTailIndexed;
                                }
                            }
                            if (const auto target = IndirectTailTargetRva(
                                    image,
                                    function,
                                    instructionOffset,
                                    instruction);
                                target.has_value())
                            {
                                analysis.TailTargetRva = *target;
                                analysis.TailEventArgumentMask = forwardedMask;
                                analysis.Relations |=
                                    EventConsumerRelationIndirectTailResolved;
                            }
                            else if (const auto target =
                                         RegisterIndirectTailTargetRva(
                                             image,
                                             function,
                                             instructionOffset,
                                             instruction);
                                     target.has_value())
                            {
                                analysis.TailTargetRva = *target;
                                analysis.TailEventArgumentMask = forwardedMask;
                                analysis.Relations |=
                                    EventConsumerRelationRegisterTailResolved;
                            }
                            else if (const auto target =
                                         MemoryIndirectTailTargetRva(
                                             image,
                                             function,
                                             instructionOffset,
                                             instruction);
                                     target.has_value())
                            {
                                analysis.TailTargetRva = *target;
                                analysis.TailEventArgumentMask = forwardedMask;
                                analysis.Relations |=
                                    EventConsumerRelationMemoryTailResolved;
                            }
                        }
                    }
                    if (instruction.modrm_reg == 2u)
                    {
                        if ((eventRegisters & 0x00000F07u) != 0)
                        {
                            markClobber(instructionOffset);
                        }
                        ClearVolatileProvenance(
                            eventRegisters,
                            vtableRegisters);
                        preparedEventArgumentMask = 0;
                    }
                    return false;
                }

                if (InstructionComparesEvent(instruction, eventRegisters))
                {
                    markUse(
                        instructionOffset,
                        EventConsumerRelationCompare,
                        analysis.CompareCount);
                }

                if (instruction.opcode == 0x89u &&
                    instruction.rex_w != 0 &&
                    (instruction.flags & F_MODRM) != 0 &&
                    instruction.modrm_mod != 3u)
                {
                    const auto base = MemoryBaseRegister(instruction);
                    const auto displacement = MemoryDisplacement(instruction);
                    const std::uint8_t source = ModRmRegRegister(instruction);
                    const std::uint32_t sourceBit = RegisterBit(source);
                    if ((eventRegisters & sourceBit) != 0)
                    {
                        if (base.has_value() &&
                            displacement.has_value() &&
                            IsTrackedFrameBase(*base, frameRegisters))
                        {
                            WriteStackProvenance(
                                stackSlots,
                                *base,
                                *displacement,
                                ProvenanceEvent);
                            analysis.Relations |=
                                EventConsumerRelationSpilledToStack;
                        }
                        else
                        {
                            markUse(
                                instructionOffset,
                                EventConsumerRelationMemoryStore,
                                analysis.MemoryStoreCount);
                        }
                    }
                    if (base.has_value() &&
                        (eventRegisters & RegisterBit(*base)) != 0)
                    {
                        markUse(
                            instructionOffset,
                            EventConsumerRelationRead,
                            analysis.ReadCount);
                    }
                    return false;
                }

                const auto destination = ExplicitRegisterWrite(instruction);
                if (!destination.has_value())
                {
                    return false;
                }

                const std::uint32_t destinationBit =
                    RegisterBit(*destination);
                const bool destinationHadEvent =
                    (eventRegisters & destinationBit) != 0;
                bool destinationKeepsEvent = false;
                bool destinationGetsVtable = false;

                if (instruction.opcode == 0x8Bu &&
                    instruction.rex_w != 0 &&
                    (instruction.flags & F_MODRM) != 0)
                {
                    if (instruction.modrm_mod == 3u)
                    {
                        if (const auto source = RegisterSource(instruction);
                            source.has_value())
                        {
                            destinationKeepsEvent =
                                (eventRegisters & RegisterBit(*source)) != 0;
                            destinationGetsVtable =
                                (vtableRegisters & RegisterBit(*source)) != 0;
                        }
                    }
                    else if (const auto base = MemoryBaseRegister(instruction);
                             base.has_value())
                    {
                        const std::int32_t displacement =
                            MemoryDisplacement(instruction).value_or(0);
                        if (IsTrackedFrameBase(*base, frameRegisters))
                        {
                            const std::uint32_t provenance =
                                ReadStackProvenance(
                                    stackSlots,
                                    *base,
                                    displacement);
                            destinationKeepsEvent =
                                (provenance & ProvenanceEvent) != 0;
                            if (destinationKeepsEvent)
                            {
                                analysis.Relations |=
                                    EventConsumerRelationReloadedFromStack;
                            }
                        }
                        else if ((eventRegisters & RegisterBit(*base)) != 0)
                        {
                            markUse(
                                instructionOffset,
                                EventConsumerRelationRead,
                                analysis.ReadCount);
                            if (displacement == 0)
                            {
                                destinationGetsVtable = true;
                                analysis.Relations |=
                                    EventConsumerRelationVtableLoaded;
                                ++analysis.VtableLoadCount;
                            }
                        }
                    }
                }
                else if (instruction.opcode == 0x89u &&
                         instruction.rex_w != 0 &&
                         (instruction.flags & F_MODRM) != 0 &&
                         instruction.modrm_mod == 3u)
                {
                    const std::uint8_t source = ModRmRegRegister(instruction);
                    destinationKeepsEvent =
                        (eventRegisters & RegisterBit(source)) != 0;
                    destinationGetsVtable =
                        (vtableRegisters & RegisterBit(source)) != 0;
                }
                else if (instruction.opcode == 0x8Du &&
                         (instruction.flags & F_MODRM) != 0)
                {
                    if (const auto base = MemoryBaseRegister(instruction);
                        base.has_value())
                    {
                        destinationKeepsEvent =
                            (eventRegisters & RegisterBit(*base)) != 0;
                    }
                }

                if (destinationHadEvent && !destinationKeepsEvent)
                {
                    markClobber(instructionOffset);
                }

                eventRegisters &= ~destinationBit;
                vtableRegisters &= ~destinationBit;
                preparedEventArgumentMask &=
                    ~ArgumentMaskFromRegisters(destinationBit);
                if (destinationKeepsEvent)
                {
                    eventRegisters |= destinationBit;
                    preparedEventArgumentMask |=
                        ArgumentMaskFromRegisters(destinationBit);
                }
                if (destinationGetsVtable)
                {
                    vtableRegisters |= destinationBit;
                }
                if ((frameRegisters & destinationBit) != 0)
                {
                    frameRegisters &= ~destinationBit;
                }
                if ((instruction.opcode == 0x8Bu ||
                     instruction.opcode == 0x89u) &&
                    instruction.rex_w != 0 &&
                    (instruction.flags & F_MODRM) != 0 &&
                    instruction.modrm_mod == 3u)
                {
                    const auto source = instruction.opcode == 0x8Bu
                        ? RegisterSource(instruction)
                        : std::optional<std::uint8_t>{
                            ModRmRegRegister(instruction)};
                    if (source.has_value() &&
                        (frameRegisters & RegisterBit(*source)) != 0)
                    {
                        frameRegisters |= destinationBit;
                    }
                }
                if (instruction.opcode == 0x8Du &&
                    (instruction.flags & F_MODRM) != 0)
                {
                    if (const auto base = MemoryBaseRegister(instruction);
                        base.has_value() &&
                        (frameRegisters & RegisterBit(*base)) != 0)
                    {
                        frameRegisters |= destinationBit;
                    }
                }
                return false;
            }));

        if (analysis.FirstUseRva != 0 &&
            (analysis.FirstClobberRva == 0 ||
             analysis.FirstUseRva < analysis.FirstClobberRva))
        {
            analysis.Relations |= EventConsumerRelationUseBeforeClobber;
        }
        return analysis;
    }

    void AnalyzeEventConsumerEvidence(
        ImageView image,
        EventConsumerEvidence& candidate)
    {
        constexpr std::uint32_t MaximumTailDepth = 8;
        std::unordered_set<std::uint32_t> visited;
        std::uint32_t currentRva = candidate.Rva;
        std::uint32_t currentEventArgumentMask = candidate.EventArgumentMask;
        std::uint32_t accumulatedRelations = 0;
        std::uint32_t accumulatedReadCount = 0;
        std::uint32_t accumulatedMemoryStoreCount = 0;
        std::uint32_t accumulatedCompareCount = 0;
        std::uint32_t accumulatedVtableLoadCount = 0;
        std::uint32_t accumulatedForwardCount = 0;
        std::uint32_t accumulatedTailForwardCount = 0;

        for (std::uint32_t depth = 0; depth <= MaximumTailDepth; ++depth)
        {
            if (!visited.insert(currentRva).second)
            {
                candidate.Rva = currentRva;
                candidate.Relations |= accumulatedRelations |
                    EventConsumerRelationTailChainCycle;
                candidate.TailForwardCount = accumulatedTailForwardCount;
                return;
            }

            RuntimeFunctionView function = ResolveRuntimeFunction(
                image,
                image.Base + currentRva);
            bool recoveredLeaf = false;
            if (function && function.BeginRva != currentRva)
            {
                candidate.Rva = currentRva;
                candidate.Size = 0;
                candidate.Relations |= accumulatedRelations |
                    EventConsumerRelationTailChainUnresolved;
                candidate.TailForwardCount = accumulatedTailForwardCount;
                return;
            }
            if (!function)
            {
                function = RecoverLeafFunction(image, currentRva);
                recoveredLeaf = static_cast<bool>(function);
            }
            if (!function)
            {
                candidate.Rva = currentRva;
                candidate.Size = 0;
                candidate.EventArgumentMask = currentEventArgumentMask;
                candidate.Relations |= accumulatedRelations |
                    EventConsumerRelationTailChainUnresolved;
                candidate.TailForwardCount = accumulatedTailForwardCount;
                return;
            }

            const EventConsumerFunctionAnalysis functionAnalysis =
                AnalyzeEventConsumerFunction(
                    image,
                    function,
                    RegistersFromArgumentMask(currentEventArgumentMask));
            accumulatedRelations |= functionAnalysis.Relations;
            if (recoveredLeaf)
            {
                accumulatedRelations |= EventConsumerRelationRecoveredLeaf;
            }
            accumulatedReadCount += functionAnalysis.ReadCount;
            accumulatedMemoryStoreCount += functionAnalysis.MemoryStoreCount;
            accumulatedCompareCount += functionAnalysis.CompareCount;
            accumulatedVtableLoadCount += functionAnalysis.VtableLoadCount;
            accumulatedForwardCount += functionAnalysis.ForwardCount;
            accumulatedTailForwardCount += functionAnalysis.TailForwardCount;

            candidate.Rva = currentRva;
            candidate.Size = function.Size();
            candidate.EventArgumentMask = currentEventArgumentMask;
            candidate.Relations |= accumulatedRelations;
            candidate.FirstUseRva = functionAnalysis.FirstUseRva;
            candidate.FirstClobberRva = functionAnalysis.FirstClobberRva;
            candidate.ReadCount = accumulatedReadCount;
            candidate.MemoryStoreCount = accumulatedMemoryStoreCount;
            candidate.CompareCount = accumulatedCompareCount;
            candidate.VtableLoadCount = accumulatedVtableLoadCount;
            candidate.ForwardCount = accumulatedForwardCount;
            candidate.TailForwardCount = accumulatedTailForwardCount;
            candidate.TailInstructionRva = functionAnalysis.TailInstructionRva;
            candidate.TailInstructionLength =
                functionAnalysis.TailInstructionLength;
            candidate.TailEncoding = functionAnalysis.TailEncoding;
            candidate.TailOperandInfo = functionAnalysis.TailOperandInfo;
            candidate.TailDisplacement = functionAnalysis.TailDisplacement;
            candidate.TailBaseRegister = functionAnalysis.TailBaseRegister;
            candidate.TailIndexRegister = functionAnalysis.TailIndexRegister;
            candidate.TailScale = functionAnalysis.TailScale;
            candidate.TailImageSize = functionAnalysis.TailImageSize;
            candidate.TailSlotRva = functionAnalysis.TailSlotRva;
            candidate.TailSlotAddress = functionAnalysis.TailSlotAddress;
            candidate.TailSlotProtection = functionAnalysis.TailSlotProtection;
            candidate.TailSlotFlags = functionAnalysis.TailSlotFlags;
            candidate.TailTargetAddress = functionAnalysis.TailTargetAddress;
            candidate.TailTargetProtection = functionAnalysis.TailTargetProtection;
            candidate.TailTargetFlags = functionAnalysis.TailTargetFlags;
            candidate.TailTargetModuleBase = functionAnalysis.TailTargetModuleBase;
            candidate.TailTargetModuleSize = functionAnalysis.TailTargetModuleSize;
            candidate.TailTargetModuleRva = functionAnalysis.TailTargetModuleRva;

            if ((candidate.TailSlotFlags & TailSlotFlagInIat) != 0 &&
                (candidate.TailTargetFlags & TailTargetFlagOtherModule) != 0)
            {
                candidate.Relations |= EventConsumerRelationSystemImportThunk;
            }
            else if (candidate.Rva < image.Size)
            {
                candidate.Relations |= EventConsumerRelationGameLocalConsumer;
            }

            if (functionAnalysis.TailForwardCount == 0)
            {
                return;
            }
            if (functionAnalysis.TailTargetRva == 0 ||
                functionAnalysis.TailEventArgumentMask == 0)
            {
                candidate.Relations |=
                    EventConsumerRelationTailChainUnresolved;
                return;
            }
            if (depth == MaximumTailDepth)
            {
                candidate.Relations |=
                    EventConsumerRelationTailChainDepthLimit;
                return;
            }

            const std::uint32_t nextRva = functionAnalysis.TailTargetRva;
            if (nextRva >= image.Size)
            {
                candidate.Relations |=
                    EventConsumerRelationTailChainUnresolved;
                return;
            }
            candidate.Relations |= EventConsumerRelationTailChainResolved;
            currentRva = nextRva;
            currentEventArgumentMask =
                functionAnalysis.TailEventArgumentMask;
        }
    }


    [[nodiscard]] VirtualReturnUse AnalyzeVirtualReturnUse(
        ImageView image,
        RuntimeFunctionView function,
        std::size_t startOffset) noexcept
    {
        VirtualReturnUse result{};
        const std::span<const std::byte> bytes = FunctionBytes(image, function);
        if (startOffset >= bytes.size())
        {
            return result;
        }

        std::uint32_t returnRegisters = RegisterBit(0u);
        std::size_t instructionCount = 0;
        const auto markUse =
            [&result, function, startOffset](std::size_t offset, std::uint32_t relation)
            {
                result.Relations |= VirtualReturnRelationUsed | relation;
                if (result.FirstUseRva == 0)
                {
                    result.FirstUseRva = static_cast<std::uint32_t>(
                        function.BeginRva + startOffset + offset);
                }
            };
        const auto markClobber =
            [&result, function, startOffset](std::size_t offset)
            {
                if (result.FirstClobberRva == 0)
                {
                    result.FirstClobberRva = static_cast<std::uint32_t>(
                        function.BeginRva + startOffset + offset);
                }
            };

        static_cast<void>(VisitDecodedInstructions(
            bytes.subspan(startOffset),
            [&result,
             &returnRegisters,
             &instructionCount,
             &markUse,
             &markClobber](
                std::size_t instructionOffset,
                const hde64s& instruction)
            {
                if (++instructionCount > 16)
                {
                    return true;
                }

                if (InstructionComparesEvent(instruction, returnRegisters))
                {
                    markUse(
                        instructionOffset,
                        VirtualReturnRelationCompare);
                }

                if (instruction.opcode == 0x89u &&
                    instruction.rex_w != 0 &&
                    (instruction.flags & F_MODRM) != 0)
                {
                    const std::uint8_t source =
                        ModRmRegRegister(instruction);
                    const std::uint32_t sourceBit = RegisterBit(source);
                    if (instruction.modrm_mod == 3u)
                    {
                        const std::uint8_t destination =
                            ModRmRmRegister(instruction);
                        const std::uint32_t destinationBit =
                            RegisterBit(destination);
                        const bool carriesReturn =
                            (returnRegisters & sourceBit) != 0;
                        if ((returnRegisters & destinationBit) != 0 &&
                            !carriesReturn)
                        {
                            markClobber(instructionOffset);
                        }
                        returnRegisters &= ~destinationBit;
                        if (carriesReturn)
                        {
                            returnRegisters |= destinationBit;
                        }
                    }
                    else
                    {
                        const auto base =
                            MemoryBaseRegister(instruction);
                        if ((returnRegisters & sourceBit) != 0)
                        {
                            markUse(
                                instructionOffset,
                                VirtualReturnRelationMemoryStore);
                        }
                        if (base.has_value() &&
                            (returnRegisters & RegisterBit(*base)) != 0)
                        {
                            markUse(
                                instructionOffset,
                                VirtualReturnRelationDereference |
                                    VirtualReturnRelationMemoryStore);
                        }
                    }
                }
                else if (instruction.opcode == 0x8Bu &&
                         instruction.rex_w != 0 &&
                         (instruction.flags & F_MODRM) != 0)
                {
                    const std::uint8_t destination =
                        ModRmRegRegister(instruction);
                    const std::uint32_t destinationBit =
                        RegisterBit(destination);
                    bool carriesReturn = false;
                    if (instruction.modrm_mod == 3u)
                    {
                        if (const auto source =
                                RegisterSource(instruction);
                            source.has_value())
                        {
                            carriesReturn =
                                (returnRegisters &
                                 RegisterBit(*source)) != 0;
                        }
                    }
                    else if (const auto base =
                                 MemoryBaseRegister(instruction);
                             base.has_value() &&
                             (returnRegisters &
                              RegisterBit(*base)) != 0)
                    {
                        carriesReturn = true;
                        markUse(
                            instructionOffset,
                            VirtualReturnRelationDereference);
                    }
                    if ((returnRegisters & destinationBit) != 0 &&
                        !carriesReturn)
                    {
                        markClobber(instructionOffset);
                    }
                    returnRegisters &= ~destinationBit;
                    if (carriesReturn)
                    {
                        returnRegisters |= destinationBit;
                    }
                }
                else if (instruction.opcode == 0x8Du &&
                         instruction.rex_w != 0 &&
                         (instruction.flags & F_MODRM) != 0 &&
                         instruction.modrm_mod != 3u)
                {
                    const std::uint8_t destination =
                        ModRmRegRegister(instruction);
                    const std::uint32_t destinationBit =
                        RegisterBit(destination);
                    const auto base =
                        MemoryBaseRegister(instruction);
                    const bool carriesReturn =
                        base.has_value() &&
                        (returnRegisters &
                         RegisterBit(*base)) != 0;
                    if ((returnRegisters & destinationBit) != 0 &&
                        !carriesReturn)
                    {
                        markClobber(instructionOffset);
                    }
                    returnRegisters &= ~destinationBit;
                    if (carriesReturn)
                    {
                        returnRegisters |= destinationBit;
                    }
                }

                const bool directCall =
                    instruction.opcode == 0xE8u;
                const bool indirectCall =
                    instruction.opcode == 0xFFu &&
                    (instruction.flags & F_MODRM) != 0 &&
                    instruction.modrm_reg == 2u;
                if (directCall || indirectCall)
                {
                    if (ArgumentMaskFromRegisters(returnRegisters) != 0)
                    {
                        markUse(
                            instructionOffset,
                            VirtualReturnRelationForward);
                    }
                    if ((returnRegisters & 0x00000F07u) != 0)
                    {
                        markClobber(instructionOffset);
                    }
                    returnRegisters &= ~0x00000F07u;
                    return returnRegisters == 0;
                }

                if (instruction.opcode != 0x89u &&
                    instruction.opcode != 0x8Bu &&
                    instruction.opcode != 0x8Du)
                {
                    if (const auto destination =
                            ExplicitRegisterWrite(instruction);
                        destination.has_value())
                    {
                        const std::uint32_t destinationBit =
                            RegisterBit(*destination);
                        if ((returnRegisters & destinationBit) != 0)
                        {
                            markClobber(instructionOffset);
                            returnRegisters &= ~destinationBit;
                        }
                    }
                }

                if (instruction.opcode == 0xC2u ||
                    instruction.opcode == 0xC3u ||
                    instruction.opcode == 0xE9u ||
                    instruction.opcode == 0xEBu ||
                    (instruction.opcode >= 0x70u &&
                     instruction.opcode <= 0x7Fu) ||
                    (instruction.opcode == 0x0Fu &&
                     instruction.opcode2 >= 0x80u &&
                     instruction.opcode2 <= 0x8Fu))
                {
                    return true;
                }

                return returnRegisters == 0;
            }));

        return result;
    }

    [[nodiscard]] VirtualInputUse AnalyzeVirtualInputUse(
        ImageView image,
        RuntimeFunctionView function,
        std::size_t callsiteOffset) noexcept
    {
        VirtualInputUse result{};
        std::uint32_t preparedRegisters = 0;
        std::uint32_t memoryRegisters = 0;
        std::uint32_t addressRegisters = 0;
        std::uint32_t immediateRegisters = 0;
        std::uint32_t zeroRegisters = 0;
        std::array<VirtualArgumentSource, 16> sources{};
        const std::span<const std::byte> bytes = FunctionBytes(image, function);

        const auto clearRegister =
            [&preparedRegisters,
             &memoryRegisters,
             &addressRegisters,
             &immediateRegisters,
             &zeroRegisters,
             &sources](std::uint8_t destination)
            {
                const std::uint32_t bit = RegisterBit(destination);
                preparedRegisters &= ~bit;
                memoryRegisters &= ~bit;
                addressRegisters &= ~bit;
                immediateRegisters &= ~bit;
                zeroRegisters &= ~bit;
                if (destination < sources.size())
                {
                    sources[destination] = {};
                }
            };
        const auto copyRegister =
            [&preparedRegisters,
             &memoryRegisters,
             &addressRegisters,
             &immediateRegisters,
             &zeroRegisters,
             &sources](
                std::uint8_t source,
                std::uint8_t destination)
            {
                const std::uint32_t sourceBit = RegisterBit(source);
                const std::uint32_t destinationBit = RegisterBit(destination);
                preparedRegisters |= destinationBit;
                memoryRegisters =
                    (memoryRegisters & ~destinationBit) |
                    ((memoryRegisters & sourceBit) != 0 ? destinationBit : 0u);
                addressRegisters =
                    (addressRegisters & ~destinationBit) |
                    ((addressRegisters & sourceBit) != 0 ? destinationBit : 0u);
                immediateRegisters =
                    (immediateRegisters & ~destinationBit) |
                    ((immediateRegisters & sourceBit) != 0 ? destinationBit : 0u);
                zeroRegisters =
                    (zeroRegisters & ~destinationBit) |
                    ((zeroRegisters & sourceBit) != 0 ? destinationBit : 0u);
                if (source < sources.size() && destination < sources.size())
                {
                    sources[destination] = sources[source];
                }
            };
        const auto setKind =
            [&preparedRegisters,
             &memoryRegisters,
             &addressRegisters,
             &immediateRegisters,
             &zeroRegisters,
             &sources,
             bytes,
             function](
                std::uint8_t destination,
                std::uint32_t kind,
                bool zero,
                std::size_t instructionOffset,
                const hde64s& instruction,
                std::uint32_t value)
            {
                const std::uint32_t bit = RegisterBit(destination);
                preparedRegisters |= bit;
                memoryRegisters &= ~bit;
                addressRegisters &= ~bit;
                immediateRegisters &= ~bit;
                zeroRegisters &= ~bit;
                if (kind == 1u)
                {
                    memoryRegisters |= bit;
                }
                else if (kind == 2u)
                {
                    addressRegisters |= bit;
                }
                else if (kind == 3u)
                {
                    immediateRegisters |= bit;
                }
                if (zero)
                {
                    zeroRegisters |= bit;
                }
                if (destination < sources.size())
                {
                    const auto encoding = PackInstructionBytes(
                        bytes,
                        instructionOffset,
                        instruction.len);
                    sources[destination] = {
                        static_cast<std::uint32_t>(
                            function.BeginRva + instructionOffset),
                        kind,
                        encoding[0],
                        PackOperandInfo(instruction),
                        InstructionDisplacementBits(instruction),
                        value
                    };
                }
            };
        const auto clearVolatile =
            [&preparedRegisters,
             &memoryRegisters,
             &addressRegisters,
             &immediateRegisters,
             &zeroRegisters,
             &sources]()
            {
                constexpr std::array<std::uint8_t, 7> volatileRegisters{
                    0u, 1u, 2u, 8u, 9u, 10u, 11u
                };
                for (const std::uint8_t index : volatileRegisters)
                {
                    const std::uint32_t bit = RegisterBit(index);
                    preparedRegisters &= ~bit;
                    memoryRegisters &= ~bit;
                    addressRegisters &= ~bit;
                    immediateRegisters &= ~bit;
                    zeroRegisters &= ~bit;
                    sources[index] = {};
                }
            };

        static_cast<void>(VisitDecodedInstructions(
            bytes,
            [callsiteOffset,
             &preparedRegisters,
             &memoryRegisters,
             &addressRegisters,
             &immediateRegisters,
             &zeroRegisters,
             &sources,
             &clearRegister,
             &copyRegister,
             &setKind,
             &clearVolatile,
             bytes,
             function](
                std::size_t instructionOffset,
                const hde64s& instruction)
            {
                if (instructionOffset >= callsiteOffset)
                {
                    return true;
                }

                const bool directCall = instruction.opcode == 0xE8u;
                const bool indirectCall =
                    instruction.opcode == 0xFFu &&
                    (instruction.flags & F_MODRM) != 0 &&
                    instruction.modrm_reg == 2u;
                if (directCall || indirectCall)
                {
                    clearVolatile();
                    return false;
                }

                if (instruction.opcode == 0x8Bu &&
                    (instruction.flags & F_MODRM) != 0)
                {
                    const std::uint8_t destination =
                        ModRmRegRegister(instruction);
                    if (instruction.modrm_mod == 3u)
                    {
                        if (const auto source = RegisterSource(instruction);
                            source.has_value())
                        {
                            copyRegister(*source, destination);
                        }
                        else
                        {
                            clearRegister(destination);
                        }
                    }
                    else
                    {
                        setKind(
                            destination,
                            1u,
                            false,
                            instructionOffset,
                            instruction,
                            0u);
                    }
                    return false;
                }

                if (instruction.opcode == 0x89u &&
                    (instruction.flags & F_MODRM) != 0 &&
                    instruction.modrm_mod == 3u)
                {
                    copyRegister(
                        ModRmRegRegister(instruction),
                        ModRmRmRegister(instruction));
                    return false;
                }

                if (instruction.opcode == 0x8Du &&
                    (instruction.flags & F_MODRM) != 0 &&
                    instruction.modrm_mod != 3u)
                {
                    setKind(
                        ModRmRegRegister(instruction),
                        2u,
                        false,
                        instructionOffset,
                        instruction,
                        0u);
                    return false;
                }

                if (instruction.opcode >= 0xB8u &&
                    instruction.opcode <= 0xBFu)
                {
                    const std::uint8_t destination = static_cast<std::uint8_t>(
                        (instruction.opcode - 0xB8u) |
                        (static_cast<std::uint8_t>(instruction.rex_b) << 3));
                    std::uint32_t value = 0;
                    bool zero = false;
                    if ((instruction.flags & F_IMM64) != 0)
                    {
                        value = static_cast<std::uint32_t>(
                            instruction.imm.imm64);
                        zero = instruction.imm.imm64 == 0;
                    }
                    else if ((instruction.flags & F_IMM32) != 0)
                    {
                        value = instruction.imm.imm32;
                        zero = value == 0;
                    }
                    else if ((instruction.flags & F_IMM16) != 0)
                    {
                        value = instruction.imm.imm16;
                        zero = value == 0;
                    }
                    else if ((instruction.flags & F_IMM8) != 0)
                    {
                        value = instruction.imm.imm8;
                        zero = value == 0;
                    }
                    setKind(
                        destination,
                        3u,
                        zero,
                        instructionOffset,
                        instruction,
                        value);
                    return false;
                }

                if (instruction.opcode == 0xC7u &&
                    (instruction.flags & F_MODRM) != 0 &&
                    instruction.modrm_mod == 3u &&
                    instruction.modrm_reg == 0u &&
                    (instruction.flags & F_IMM32) != 0)
                {
                    setKind(
                        ModRmRmRegister(instruction),
                        3u,
                        instruction.imm.imm32 == 0,
                        instructionOffset,
                        instruction,
                        instruction.imm.imm32);
                    return false;
                }

                if ((instruction.opcode == 0x31u ||
                     instruction.opcode == 0x33u) &&
                    (instruction.flags & F_MODRM) != 0 &&
                    instruction.modrm_mod == 3u)
                {
                    const std::uint8_t destination =
                        instruction.opcode == 0x31u
                            ? ModRmRmRegister(instruction)
                            : ModRmRegRegister(instruction);
                    const std::uint8_t source =
                        instruction.opcode == 0x31u
                            ? ModRmRegRegister(instruction)
                            : ModRmRmRegister(instruction);
                    if (destination == source)
                    {
                        setKind(
                            destination,
                            3u,
                            true,
                            instructionOffset,
                            instruction,
                            0u);
                        return false;
                    }
                }

                if (const auto destination =
                        ExplicitRegisterWrite(instruction);
                    destination.has_value())
                {
                    clearRegister(*destination);
                    preparedRegisters |= RegisterBit(*destination);
                    if (*destination < sources.size())
                    {
                        const auto encoding = PackInstructionBytes(
                            bytes,
                            instructionOffset,
                            instruction.len);
                        sources[*destination] = {
                            static_cast<std::uint32_t>(
                                function.BeginRva + instructionOffset),
                            4u,
                            encoding[0],
                            PackOperandInfo(instruction),
                            InstructionDisplacementBits(instruction),
                            0u
                        };
                    }
                }
                return false;
            }));

        result.PreparedArgumentMask =
            ArgumentMaskFromRegisters(preparedRegisters);
        result.MemoryArgumentMask =
            ArgumentMaskFromRegisters(memoryRegisters);
        result.AddressArgumentMask =
            ArgumentMaskFromRegisters(addressRegisters);
        result.ImmediateArgumentMask =
            ArgumentMaskFromRegisters(immediateRegisters);
        result.ZeroArgumentMask =
            ArgumentMaskFromRegisters(zeroRegisters);
        result.Rdx = sources[2u];
        result.R8 = sources[8u];
        result.R9 = sources[9u];
        return result;
    }

    [[nodiscard]] bool IsEventRttiName(
        ImageView image,
        std::size_t offset) noexcept
    {
        constexpr std::array<char, 10> classPrefix{
            '.', '?', 'A', 'V', 'C', 'E', 'v', 'e', 'n', 't'
        };
        constexpr std::array<char, 10> structPrefix{
            '.', '?', 'A', 'U', 'C', 'E', 'v', 'e', 'n', 't'
        };
        if (offset > image.Size ||
            classPrefix.size() > image.Size - offset)
        {
            return false;
        }
        const char* text = reinterpret_cast<const char*>(image.Base + offset);
        return std::memcmp(text, classPrefix.data(), classPrefix.size()) == 0 ||
            std::memcmp(text, structPrefix.data(), structPrefix.size()) == 0;
    }

    [[nodiscard]] std::uint32_t VftableTargetRva(
        ImageView image,
        std::uint32_t vftableRva,
        std::uint32_t slot) noexcept
    {
        if (vftableRva > image.Size ||
            slot > image.Size - vftableRva ||
            sizeof(std::uintptr_t) > image.Size - vftableRva - slot)
        {
            return 0xFFFFFFFFu;
        }
        std::uintptr_t target{};
        std::memcpy(
            &target,
            image.Base + vftableRva + slot,
            sizeof(target));
        const std::uintptr_t base =
            reinterpret_cast<std::uintptr_t>(image.Base);
        if (target < base ||
            target - base >= image.Size ||
            !IsExecutableAddress(
                reinterpret_cast<const std::byte*>(target)))
        {
            return 0xFFFFFFFFu;
        }
        return static_cast<std::uint32_t>(target - base);
    }

    [[nodiscard]] std::uint32_t ImmediateReturnValue(
        ImageView image,
        std::uint32_t targetRva) noexcept
    {
        if (targetRva == 0xFFFFFFFFu ||
            targetRva >= image.Size)
        {
            return 0xFFFFFFFFu;
        }
        const std::byte* code = image.Base + targetRva;
        if (!Contains(image, code, 30))
        {
            return 0xFFFFFFFFu;
        }

        hde64s first{};
        const unsigned int firstLength = hde64_disasm(code, &first);
        if (firstLength == 0 ||
            (first.flags & F_ERROR) != 0 ||
            firstLength > 15)
        {
            return 0xFFFFFFFFu;
        }

        std::uint32_t value = 0xFFFFFFFFu;
        bool known = false;
        if (first.opcode == 0xB8u &&
            first.rex_b == 0)
        {
            if ((first.flags & F_IMM32) != 0)
            {
                value = first.imm.imm32;
                known = true;
            }
            else if ((first.flags & F_IMM64) != 0 &&
                     first.imm.imm64 <= UINT32_MAX)
            {
                value = static_cast<std::uint32_t>(first.imm.imm64);
                known = true;
            }
        }
        else if ((first.opcode == 0x31u ||
                  first.opcode == 0x33u) &&
                 (first.flags & F_MODRM) != 0 &&
                 first.modrm_mod == 3u &&
                 ModRmRegRegister(first) == 0u &&
                 ModRmRmRegister(first) == 0u)
        {
            value = 0;
            known = true;
        }
        if (!known ||
            !Contains(image, code + firstLength, 1))
        {
            return 0xFFFFFFFFu;
        }

        hde64s second{};
        const unsigned int secondLength =
            hde64_disasm(code + firstLength, &second);
        if (secondLength == 0 ||
            (second.flags & F_ERROR) != 0 ||
            (second.opcode != 0xC3u &&
             second.opcode != 0xC2u))
        {
            return 0xFFFFFFFFu;
        }
        return value;
    }

    [[nodiscard]] RttiEventTypeAnalysis AnalyzeEventRttiTypes(
        ImageView image)
    {
        RttiEventTypeAnalysis analysis;
        if (image.Base == nullptr || image.Size < 32)
        {
            return analysis;
        }

        std::vector<RttiEventTypeEvidence> candidates;
        candidates.reserve(CEventGenerator::EnhancedRttiEventTypeDiagnosticCapacity);
        for (std::size_t offset = 16;
             offset + 10 <= image.Size;
             ++offset)
        {
            if (!IsEventRttiName(image, offset))
            {
                continue;
            }
            ++analysis.CandidateCount;
            if (candidates.size() <
                CEventGenerator::EnhancedRttiEventTypeDiagnosticCapacity)
            {
                candidates.push_back({
                    static_cast<std::uint32_t>(offset),
                    static_cast<std::uint32_t>(offset - 16)
                });
            }
            const char* text =
                reinterpret_cast<const char*>(image.Base + offset);
            std::size_t advance = 0;
            while (offset + advance < image.Size &&
                   advance < 192 &&
                   text[advance] != '\0')
            {
                ++advance;
            }
            offset += advance;
        }

        if (candidates.empty())
        {
            return analysis;
        }

        std::unordered_map<std::uint32_t, std::size_t> descriptorIndex;
        descriptorIndex.reserve(candidates.size());
        for (std::size_t index = 0; index < candidates.size(); ++index)
        {
            descriptorIndex.emplace(
                candidates[index].TypeDescriptorRva,
                index);
        }

        for (std::size_t offset = 0;
             offset + 24 <= image.Size;
             offset += 4)
        {
            std::uint32_t signature{};
            std::uint32_t typeDescriptorRva{};
            std::uint32_t selfRva{};
            std::memcpy(
                &signature,
                image.Base + offset,
                sizeof(signature));
            if (signature != 1u)
            {
                continue;
            }
            std::memcpy(
                &typeDescriptorRva,
                image.Base + offset + 12,
                sizeof(typeDescriptorRva));
            const auto found = descriptorIndex.find(typeDescriptorRva);
            if (found == descriptorIndex.end())
            {
                continue;
            }
            std::memcpy(
                &selfRva,
                image.Base + offset + 20,
                sizeof(selfRva));
            if (selfRva != offset ||
                candidates[found->second].CompleteObjectLocatorRva != 0)
            {
                continue;
            }
            candidates[found->second].CompleteObjectLocatorRva =
                static_cast<std::uint32_t>(offset);
        }

        std::unordered_map<std::uintptr_t, std::size_t> locatorIndex;
        locatorIndex.reserve(candidates.size());
        const std::uintptr_t imageBase =
            reinterpret_cast<std::uintptr_t>(image.Base);
        for (std::size_t index = 0; index < candidates.size(); ++index)
        {
            const std::uint32_t locatorRva =
                candidates[index].CompleteObjectLocatorRva;
            if (locatorRva != 0)
            {
                locatorIndex.emplace(
                    imageBase + locatorRva,
                    index);
            }
        }

        for (std::size_t offset = 0;
             offset + sizeof(std::uintptr_t) <= image.Size;
             offset += sizeof(std::uintptr_t))
        {
            std::uintptr_t value{};
            std::memcpy(
                &value,
                image.Base + offset,
                sizeof(value));
            const auto found = locatorIndex.find(value);
            if (found == locatorIndex.end())
            {
                continue;
            }
            RttiEventTypeEvidence& candidate =
                candidates[found->second];
            if (candidate.VftableRva != 0)
            {
                continue;
            }
            const std::size_t vftableOffset =
                offset + sizeof(std::uintptr_t);
            if (vftableOffset >= image.Size)
            {
                continue;
            }
            const std::uint32_t vftableRva =
                static_cast<std::uint32_t>(vftableOffset);
            if (VftableTargetRva(image, vftableRva, 0) ==
                0xFFFFFFFFu)
            {
                continue;
            }
            candidate.VftableRva = vftableRva;
            candidate.Slot30TargetRva =
                VftableTargetRva(image, vftableRva, 0x30u);
            candidate.Slot48TargetRva =
                VftableTargetRva(image, vftableRva, 0x48u);
            candidate.Slot50TargetRva =
                VftableTargetRva(image, vftableRva, 0x50u);
            candidate.Slot60TargetRva =
                VftableTargetRva(image, vftableRva, 0x60u);
            candidate.Slot68TargetRva =
                VftableTargetRva(image, vftableRva, 0x68u);
            candidate.SlotB0TargetRva =
                VftableTargetRva(image, vftableRva, 0xB0u);
            candidate.SlotF8TargetRva =
                VftableTargetRva(image, vftableRva, 0xF8u);
            candidate.EventTypeValue =
                ImmediateReturnValue(
                    image,
                    candidate.Slot48TargetRva);
        }

        analysis.Types = std::move(candidates);
        return analysis;
    }

    [[nodiscard]] VirtualSlotAnalysis AnalyzeEventVirtualSlots(
        ImageView image,
        std::span<const RuntimeFunctionView> walkers,
        std::int32_t eventStackOffset,
        std::uint32_t eventTypeVFuncOffset,
        std::uint32_t eventArgumentsVFuncOffset,
        std::uint32_t eventEntityVFuncOffset)
    {
        VirtualSlotAnalysis analysis;
        for (RuntimeFunctionView walker : walkers)
        {
            std::uint32_t eventRegisters = 0;
            std::uint32_t vtableRegisters = 0;
            std::vector<std::uint32_t> seenSlots;
            static_cast<void>(VisitDecodedInstructions(
                FunctionBytes(image, walker),
                [image,
                 walker,
                 eventStackOffset,
                 eventTypeVFuncOffset,
                 eventArgumentsVFuncOffset,
                 eventEntityVFuncOffset,
                 &analysis,
                 &eventRegisters,
                 &vtableRegisters,
                 &seenSlots](
                    std::size_t instructionOffset,
                    const hde64s& instruction)
                {
                    if (instruction.opcode == 0x8Bu &&
                        instruction.rex_w != 0 &&
                        (instruction.flags & F_MODRM) != 0)
                    {
                        const std::uint8_t destination =
                            ModRmRegRegister(instruction);
                        const std::uint32_t destinationBit =
                            RegisterBit(destination);
                        eventRegisters &= ~destinationBit;
                        vtableRegisters &= ~destinationBit;
                        if (instruction.modrm_mod == 3u)
                        {
                            if (const auto source = RegisterSource(instruction);
                                source.has_value())
                            {
                                const std::uint32_t sourceBit =
                                    RegisterBit(*source);
                                if ((eventRegisters & sourceBit) != 0)
                                {
                                    eventRegisters |= destinationBit;
                                }
                                if ((vtableRegisters & sourceBit) != 0)
                                {
                                    vtableRegisters |= destinationBit;
                                }
                            }
                        }
                        else
                        {
                            const auto displacement =
                                MemoryDisplacement(instruction);
                            if ((instruction.flags & F_SIB) != 0 &&
                                instruction.sib_scale == 3u &&
                                displacement.has_value() &&
                                *displacement == eventStackOffset)
                            {
                                eventRegisters |= destinationBit;
                            }
                            else if (const auto base =
                                         MemoryBaseRegister(instruction);
                                     base.has_value() &&
                                     displacement.value_or(0) == 0 &&
                                     (eventRegisters & RegisterBit(*base)) != 0)
                            {
                                vtableRegisters |= destinationBit;
                            }
                        }
                    }
                    else if (instruction.opcode == 0x89u &&
                             instruction.rex_w != 0 &&
                             (instruction.flags & F_MODRM) != 0 &&
                             instruction.modrm_mod == 3u)
                    {
                        const std::uint8_t source =
                            ModRmRegRegister(instruction);
                        const std::uint8_t destination =
                            ModRmRmRegister(instruction);
                        const std::uint32_t sourceBit = RegisterBit(source);
                        const std::uint32_t destinationBit =
                            RegisterBit(destination);
                        eventRegisters &= ~destinationBit;
                        vtableRegisters &= ~destinationBit;
                        if ((eventRegisters & sourceBit) != 0)
                        {
                            eventRegisters |= destinationBit;
                        }
                        if ((vtableRegisters & sourceBit) != 0)
                        {
                            vtableRegisters |= destinationBit;
                        }
                    }
                    else if (const auto destination =
                                 ExplicitRegisterWrite(instruction);
                             destination.has_value())
                    {
                        const std::uint32_t destinationBit =
                            RegisterBit(*destination);
                        eventRegisters &= ~destinationBit;
                        vtableRegisters &= ~destinationBit;
                    }

                    if (instruction.opcode != 0xFFu ||
                        (instruction.flags & F_MODRM) == 0 ||
                        instruction.modrm_reg != 2u ||
                        instruction.modrm_mod == 3u)
                    {
                        return false;
                    }
                    const auto base = MemoryBaseRegister(instruction);
                    const auto displacement = MemoryDisplacement(instruction);
                    if (!base.has_value() || !displacement.has_value() ||
                        *displacement < 0 ||
                        *displacement > 0x800 ||
                        (*displacement % static_cast<std::int32_t>(sizeof(void*))) != 0 ||
                        (vtableRegisters & RegisterBit(*base)) == 0)
                    {
                        ClearVolatileProvenance(
                            eventRegisters,
                            vtableRegisters);
                        return false;
                    }

                    const std::uint32_t slot =
                        static_cast<std::uint32_t>(*displacement);
                    const std::uint32_t eventArgumentMask =
                        ArgumentMaskFromRegisters(eventRegisters);
                    const VirtualReturnUse returnUse =
                        AnalyzeVirtualReturnUse(
                            image,
                            walker,
                            instructionOffset + instruction.len);
                    const VirtualInputUse inputUse =
                        AnalyzeVirtualInputUse(
                            image,
                            walker,
                            instructionOffset);
                    std::uint32_t relations =
                        VirtualSlotRelationVtableProvenance;
                    if ((eventRegisters & RegisterBit(1u)) != 0)
                    {
                        relations |= VirtualSlotRelationEventInRcx;
                    }
                    if (slot == eventTypeVFuncOffset)
                    {
                        relations |= VirtualSlotRelationEventType;
                    }
                    if (slot == eventArgumentsVFuncOffset)
                    {
                        relations |= VirtualSlotRelationEventArguments;
                    }
                    if (slot == eventEntityVFuncOffset)
                    {
                        relations |= VirtualSlotRelationEventEntity;
                    }
                    analysis.Callsites.push_back({
                        walker.BeginRva,
                        static_cast<std::uint32_t>(
                            walker.BeginRva + instructionOffset),
                        slot,
                        relations,
                        eventArgumentMask,
                        returnUse.Relations,
                        returnUse.FirstUseRva,
                        returnUse.FirstClobberRva,
                        inputUse.PreparedArgumentMask,
                        inputUse.MemoryArgumentMask,
                        inputUse.AddressArgumentMask,
                        inputUse.ImmediateArgumentMask,
                        inputUse.ZeroArgumentMask,
                        inputUse.Rdx,
                        inputUse.R8,
                        inputUse.R9
                    });
                    auto existing = std::ranges::find_if(
                        analysis.Slots,
                        [slot](const VirtualSlotEvidence& candidate)
                        {
                            return candidate.SlotOffset == slot;
                        });
                    if (existing == analysis.Slots.end())
                    {
                        analysis.Slots.push_back({
                            slot,
                            relations,
                            1u,
                            0u,
                            0u,
                            eventArgumentMask,
                            returnUse.Relations,
                            returnUse.Relations != 0 ? 1u : 0u
                        });
                        existing = std::prev(analysis.Slots.end());
                    }
                    else
                    {
                        existing->Relations |= relations;
                        existing->EventArgumentMask |= eventArgumentMask;
                        existing->ReturnRelations |= returnUse.Relations;
                        if (returnUse.Relations != 0)
                        {
                            ++existing->ReturnUseCount;
                        }
                        ++existing->CallsiteCount;
                    }
                    if ((relations & VirtualSlotRelationEventInRcx) != 0)
                    {
                        ++existing->EventRcxCount;
                    }
                    if (!std::ranges::contains(seenSlots, slot))
                    {
                        seenSlots.push_back(slot);
                        ++existing->WalkerCount;
                    }
                    ClearVolatileProvenance(
                        eventRegisters,
                        vtableRegisters);
                    return false;
                }));
        }
        std::ranges::sort(
            analysis.Slots,
            [](const VirtualSlotEvidence& left,
               const VirtualSlotEvidence& right)
            {
                return left.SlotOffset < right.SlotOffset;
            });
        std::ranges::sort(
            analysis.Callsites,
            [](const VirtualSlotCallsiteEvidence& left,
               const VirtualSlotCallsiteEvidence& right)
            {
                return left.CallsiteRva < right.CallsiteRva;
            });
        return analysis;
    }

    [[nodiscard]] EventConsumerAnalysis AnalyzeEventConsumers(
        ImageView image,
        std::span<const RuntimeFunctionView> walkers,
        RuntimeFunctionView countFunction,
        RuntimeFunctionView eventTypeFunction,
        std::int32_t eventStackOffset)
    {
        EventConsumerAnalysis analysis;

        for (RuntimeFunctionView walker : walkers)
        {
            std::uint32_t eventRegisters = 0;
            std::uint32_t vtableRegisters = 0;
            std::vector<std::uint32_t> seenCallees;

            static_cast<void>(VisitDecodedInstructions(
                FunctionBytes(image, walker),
                [image,
                 walker,
                 countFunction,
                 eventTypeFunction,
                 eventStackOffset,
                 &analysis,
                 &eventRegisters,
                 &vtableRegisters,
                 &seenCallees](
                    std::size_t instructionOffset,
                    const hde64s& instruction)
                {
                    const bool directCall = instruction.opcode == 0xE8u;
                    const bool tailCall = instruction.opcode == 0xE9u;
                    if (directCall || tailCall)
                    {
                        const std::uint32_t eventArgumentMask =
                            ArgumentMaskFromRegisters(eventRegisters);
                        const auto targetRva = directCall
                            ? DirectCallTargetRva(
                                image,
                                walker,
                                instructionOffset,
                                instruction)
                            : DirectTailTargetRva(
                                image,
                                walker,
                                instructionOffset,
                                instruction);
                        if (eventArgumentMask != 0 && targetRva.has_value())
                        {
                            const RuntimeFunctionView target =
                                ResolveRuntimeFunction(
                                    image,
                                    image.Base + *targetRva);
                            const std::uint32_t normalizedRva =
                                target ? target.BeginRva : *targetRva;
                            const bool excluded =
                                normalizedRva == walker.BeginRva ||
                                (countFunction &&
                                 normalizedRva == countFunction.BeginRva) ||
                                (eventTypeFunction &&
                                 normalizedRva == eventTypeFunction.BeginRva);
                            if (!excluded)
                            {
                                analysis.Callsites.push_back({
                                    walker.BeginRva,
                                    static_cast<std::uint32_t>(
                                        walker.BeginRva + instructionOffset),
                                    normalizedRva,
                                    eventArgumentMask,
                                    directCall
                                        ? EventConsumerCallsiteDirect
                                        : EventConsumerCallsiteTail,
                                    instruction.len,
                                    PackInstructionBytes(
                                        FunctionBytes(image, walker),
                                        instructionOffset,
                                        instruction.len),
                                    PackOperandInfo(instruction),
                                    InstructionDisplacementBits(instruction)
                                });

                                auto existing = std::ranges::find_if(
                                    analysis.Consumers,
                                    [normalizedRva](
                                        const EventConsumerEvidence& candidate)
                                    {
                                        return candidate.Rva == normalizedRva;
                                    });
                                if (existing == analysis.Consumers.end())
                                {
                                    analysis.Consumers.push_back({
                                        normalizedRva,
                                        target ? target.Size() : 0u,
                                        0u,
                                        eventArgumentMask,
                                        0u,
                                        0u,
                                        1u,
                                        0u,
                                        0u,
                                        0u,
                                        0u,
                                        0u,
                                        0u,
                                        0u
                                    });
                                    existing = std::prev(
                                        analysis.Consumers.end());
                                }
                                else
                                {
                                    existing->EventArgumentMask |=
                                        eventArgumentMask;
                                    ++existing->CallsiteCount;
                                }
                                if (!std::ranges::contains(
                                        seenCallees,
                                        normalizedRva))
                                {
                                    seenCallees.push_back(normalizedRva);
                                    ++existing->WalkerCount;
                                }
                            }
                        }

                        if (directCall)
                        {
                            ClearVolatileProvenance(
                                eventRegisters,
                                vtableRegisters);
                        }
                        return false;
                    }

                    if (instruction.opcode == 0xFFu &&
                        (instruction.flags & F_MODRM) != 0 &&
                        (instruction.modrm_reg == 2u ||
                         instruction.modrm_reg == 4u))
                    {
                        const std::uint32_t eventArgumentMask =
                            ArgumentMaskFromRegisters(eventRegisters);
                        if (eventArgumentMask != 0)
                        {
                            std::uint32_t relations =
                                EventConsumerCallsiteIndirect |
                                EventConsumerCallsiteUnresolved;
                            relations |= instruction.modrm_reg == 4u
                                ? EventConsumerCallsiteTail
                                : EventConsumerCallsiteDirect;
                            relations |= instruction.modrm_mod == 3u
                                ? EventConsumerCallsiteRegister
                                : EventConsumerCallsiteMemory;
                            analysis.Callsites.push_back({
                                walker.BeginRva,
                                static_cast<std::uint32_t>(
                                    walker.BeginRva + instructionOffset),
                                0u,
                                eventArgumentMask,
                                relations,
                                instruction.len,
                                PackInstructionBytes(
                                    FunctionBytes(image, walker),
                                    instructionOffset,
                                    instruction.len),
                                PackOperandInfo(instruction),
                                InstructionDisplacementBits(instruction)
                            });
                        }
                        if (instruction.modrm_reg == 2u)
                        {
                            ClearVolatileProvenance(
                                eventRegisters,
                                vtableRegisters);
                        }
                        return false;
                    }

                    const auto destination = ExplicitRegisterWrite(instruction);
                    if (!destination.has_value())
                    {
                        return false;
                    }
                    const std::uint32_t destinationBit =
                        RegisterBit(*destination);
                    eventRegisters &= ~destinationBit;
                    vtableRegisters &= ~destinationBit;

                    if (instruction.opcode == 0x8Bu &&
                        instruction.rex_w != 0 &&
                        (instruction.flags & F_MODRM) != 0)
                    {
                        if (instruction.modrm_mod == 3u)
                        {
                            if (const auto source = RegisterSource(instruction);
                                source.has_value())
                            {
                                const std::uint32_t sourceBit =
                                    RegisterBit(*source);
                                if ((eventRegisters & sourceBit) != 0)
                                {
                                    eventRegisters |= destinationBit;
                                }
                                if ((vtableRegisters & sourceBit) != 0)
                                {
                                    vtableRegisters |= destinationBit;
                                }
                            }
                        }
                        else
                        {
                            const auto displacement =
                                MemoryDisplacement(instruction);
                            if ((instruction.flags & F_SIB) != 0 &&
                                instruction.sib_scale == 3u &&
                                displacement.has_value() &&
                                *displacement == eventStackOffset)
                            {
                                eventRegisters |= destinationBit;
                            }
                            else if (const auto base =
                                         MemoryBaseRegister(instruction);
                                     base.has_value() &&
                                     displacement.value_or(0) == 0 &&
                                     (eventRegisters &
                                      RegisterBit(*base)) != 0)
                            {
                                vtableRegisters |= destinationBit;
                            }
                        }
                    }
                    else if (instruction.opcode == 0x89u &&
                             instruction.rex_w != 0 &&
                             (instruction.flags & F_MODRM) != 0 &&
                             instruction.modrm_mod == 3u)
                    {
                        const std::uint8_t source =
                            ModRmRegRegister(instruction);
                        const std::uint32_t sourceBit = RegisterBit(source);
                        if ((eventRegisters & sourceBit) != 0)
                        {
                            eventRegisters |= destinationBit;
                        }
                        if ((vtableRegisters & sourceBit) != 0)
                        {
                            vtableRegisters |= destinationBit;
                        }
                    }
                    return false;
                }));
        }

        for (EventConsumerEvidence& candidate : analysis.Consumers)
        {
            AnalyzeEventConsumerEvidence(image, candidate);
            if (candidate.CallsiteCount > 1)
            {
                candidate.Relations |=
                    EventConsumerRelationMultipleCallsites;
            }
            if (candidate.WalkerCount > 1)
            {
                candidate.Relations |=
                    EventConsumerRelationMultipleWalkers;
            }
        }

        std::ranges::sort(
            analysis.Consumers,
            [](const EventConsumerEvidence& left,
               const EventConsumerEvidence& right)
            {
                const bool leftUsed = left.FirstUseRva != 0;
                const bool rightUsed = right.FirstUseRva != 0;
                if (leftUsed != rightUsed)
                {
                    return leftUsed > rightUsed;
                }
                if (left.WalkerCount != right.WalkerCount)
                {
                    return left.WalkerCount > right.WalkerCount;
                }
                if (left.CallsiteCount != right.CallsiteCount)
                {
                    return left.CallsiteCount > right.CallsiteCount;
                }
                return left.Rva < right.Rva;
            });
        std::ranges::sort(
            analysis.Callsites,
            [](const EventConsumerCallsiteEvidence& left,
               const EventConsumerCallsiteEvidence& right)
            {
                return left.WalkerRva != right.WalkerRva
                    ? left.WalkerRva < right.WalkerRva
                    : left.CallsiteRva < right.CallsiteRva;
            });
        return analysis;
    }

    [[nodiscard]] DispatchCalleeAnalysis AnalyzeDispatchCallees(
        ImageView image,
        std::span<const RuntimeFunctionView> walkers,
        RuntimeFunctionView countFunction,
        RuntimeFunctionView eventTypeFunction,
        std::int32_t eventStackOffset,
        std::uint32_t eventTypeVFuncOffset,
        std::uint32_t eventArgumentsVFuncOffset,
        std::uint32_t eventEntityVFuncOffset)
    {
        DispatchCalleeAnalysis analysis;

        for (RuntimeFunctionView walker : walkers)
        {
            std::uint32_t eventRegisters = 0;
            std::uint32_t vtableRegisters = 0;
            bool firstArgumentPrepared = false;
            bool eventTypeCallSeen = false;
            std::vector<std::uint32_t> seenCallees;

            static_cast<void>(VisitDecodedInstructions(
                FunctionBytes(image, walker),
                [image,
                 walker,
                 countFunction,
                 eventTypeFunction,
                 eventStackOffset,
                 eventTypeVFuncOffset,
                 &analysis,
                 &eventRegisters,
                 &vtableRegisters,
                 &firstArgumentPrepared,
                 &eventTypeCallSeen,
                 &seenCallees](
                    std::size_t instructionOffset,
                    const hde64s& instruction)
                {
                    constexpr std::uint8_t RcxIndex = 1u;
                    constexpr std::uint8_t RdxIndex = 2u;
                    const std::uint32_t rcxBit = RegisterBit(RcxIndex);
                    const std::uint32_t rdxBit = RegisterBit(RdxIndex);

                    if (instruction.opcode == 0xE8u)
                    {
                        const auto targetRva = DirectCallTargetRva(
                            image,
                            walker,
                            instructionOffset,
                            instruction);
                        if (targetRva.has_value() &&
                            (eventRegisters & rdxBit) != 0)
                        {
                            const RuntimeFunctionView targetFunction =
                                ResolveRuntimeFunction(
                                    image,
                                    image.Base + *targetRva);
                            const std::uint32_t normalizedRva =
                                targetFunction
                                    ? targetFunction.BeginRva
                                    : *targetRva;
                            const bool excluded =
                                normalizedRva == walker.BeginRva ||
                                (countFunction &&
                                 normalizedRva == countFunction.BeginRva) ||
                                (eventTypeFunction &&
                                 normalizedRva == eventTypeFunction.BeginRva);
                            if (!excluded)
                            {
                                std::uint32_t relations =
                                    DispatchRelationEventArgumentRdx;
                                if (firstArgumentPrepared &&
                                    (eventRegisters & rcxBit) == 0 &&
                                    (vtableRegisters & rcxBit) == 0)
                                {
                                    relations |=
                                        DispatchRelationFirstArgumentPrepared;
                                }
                                if (eventTypeCallSeen)
                                {
                                    relations |=
                                        DispatchRelationAfterEventTypeCall;
                                }

                                ++analysis.CallsiteCount;
                                analysis.Callsites.push_back({
                                    walker.BeginRva,
                                    static_cast<std::uint32_t>(
                                        walker.BeginRva + instructionOffset),
                                    normalizedRva,
                                    relations
                                });
                                auto existing = std::ranges::find_if(
                                    analysis.Callees,
                                    [normalizedRva](
                                        const DispatchCalleeEvidence& evidence)
                                    {
                                        return evidence.Rva == normalizedRva;
                                    });
                                if (existing == analysis.Callees.end())
                                {
                                    analysis.Callees.push_back({
                                        normalizedRva,
                                        targetFunction
                                            ? targetFunction.Size()
                                            : 0u,
                                        relations,
                                        1u,
                                        0u
                                    });
                                    existing = std::prev(
                                        analysis.Callees.end());
                                }
                                else
                                {
                                    existing->Relations |= relations;
                                    ++existing->CallsiteCount;
                                }

                                if (!std::ranges::contains(
                                        seenCallees,
                                        normalizedRva))
                                {
                                    seenCallees.push_back(normalizedRva);
                                    ++existing->WalkerCount;
                                }
                            }
                        }

                        ClearVolatileProvenance(
                            eventRegisters,
                            vtableRegisters);
                        firstArgumentPrepared = false;
                        return false;
                    }

                    if (instruction.opcode == 0xFFu &&
                        (instruction.flags & F_MODRM) != 0 &&
                        instruction.modrm_reg == 2u)
                    {
                        if (instruction.modrm_mod != 3u)
                        {
                            const auto displacement =
                                MemoryDisplacement(instruction);
                            const auto base =
                                MemoryBaseRegister(instruction);
                            if (displacement.has_value() &&
                                base.has_value() &&
                                *displacement >= 0 &&
                                static_cast<std::uint32_t>(*displacement) ==
                                    eventTypeVFuncOffset &&
                                (vtableRegisters &
                                 RegisterBit(*base)) != 0)
                            {
                                eventTypeCallSeen = true;
                            }
                        }

                        ClearVolatileProvenance(
                            eventRegisters,
                            vtableRegisters);
                        firstArgumentPrepared = false;
                        return false;
                    }

                    const auto destination =
                        ExplicitRegisterWrite(instruction);
                    if (!destination.has_value())
                    {
                        return false;
                    }

                    const std::uint32_t destinationBit =
                        RegisterBit(*destination);
                    eventRegisters &= ~destinationBit;
                    vtableRegisters &= ~destinationBit;

                    if (instruction.opcode == 0x8Bu &&
                        instruction.rex_w != 0 &&
                        (instruction.flags & F_MODRM) != 0)
                    {
                        if (instruction.modrm_mod == 3u)
                        {
                            if (const auto source =
                                    RegisterSource(instruction);
                                source.has_value())
                            {
                                const std::uint32_t sourceBit =
                                    RegisterBit(*source);
                                if ((eventRegisters & sourceBit) != 0)
                                {
                                    eventRegisters |= destinationBit;
                                }
                                if ((vtableRegisters & sourceBit) != 0)
                                {
                                    vtableRegisters |= destinationBit;
                                }
                            }
                        }
                        else
                        {
                            const auto displacement =
                                MemoryDisplacement(instruction);
                            if ((instruction.flags & F_SIB) != 0 &&
                                instruction.sib_scale == 3u &&
                                displacement.has_value() &&
                                *displacement == eventStackOffset)
                            {
                                eventRegisters |= destinationBit;
                            }
                            else if (const auto base =
                                         MemoryBaseRegister(instruction);
                                     base.has_value() &&
                                     displacement.value_or(0) == 0 &&
                                     (eventRegisters &
                                      RegisterBit(*base)) != 0)
                            {
                                vtableRegisters |= destinationBit;
                            }
                        }
                    }
                    else if (instruction.opcode == 0x89u &&
                             instruction.rex_w != 0 &&
                             (instruction.flags & F_MODRM) != 0 &&
                             instruction.modrm_mod == 3u)
                    {
                        const std::uint8_t source =
                            ModRmRegRegister(instruction);
                        const std::uint32_t sourceBit =
                            RegisterBit(source);
                        if ((eventRegisters & sourceBit) != 0)
                        {
                            eventRegisters |= destinationBit;
                        }
                        if ((vtableRegisters & sourceBit) != 0)
                        {
                            vtableRegisters |= destinationBit;
                        }
                    }

                    if (*destination == RcxIndex)
                    {
                        firstArgumentPrepared = true;
                    }
                    return false;
                }));
        }

        for (DispatchCalleeEvidence& evidence : analysis.Callees)
        {
            const RuntimeFunctionView function =
                ResolveRuntimeFunction(
                    image,
                    image.Base + evidence.Rva);
            if (function)
            {
                DispatchFunctionAnalysis functionAnalysis =
                    AnalyzeDispatchFunction(
                        image,
                        function,
                        RegisterBit(1u),
                        RegisterBit(2u),
                        eventTypeVFuncOffset,
                        eventArgumentsVFuncOffset,
                        eventEntityVFuncOffset);
                evidence.Relations |= functionAnalysis.Relations;
                analysis.ForwardCallsites.insert(
                    analysis.ForwardCallsites.end(),
                    functionAnalysis.ForwardCallsites.begin(),
                    functionAnalysis.ForwardCallsites.end());
            }

            if (evidence.CallsiteCount > 1)
            {
                evidence.Relations |=
                    DispatchRelationMultipleCallsites;
            }
            if (evidence.WalkerCount > 1)
            {
                evidence.Relations |=
                    DispatchRelationMultipleWalkers;
            }
        }

        for (const DispatchForwardCallsiteEvidence& callsite :
             analysis.ForwardCallsites)
        {
            auto existing = std::ranges::find_if(
                analysis.ForwardCallees,
                [&callsite](
                    const DispatchForwardCalleeEvidence& evidence)
                {
                    return evidence.Rva == callsite.CalleeRva;
                });
            if (existing == analysis.ForwardCallees.end())
            {
                const RuntimeFunctionView function =
                    ResolveRuntimeFunction(
                        image,
                        image.Base + callsite.CalleeRva);
                analysis.ForwardCallees.push_back({
                    callsite.CalleeRva,
                    function ? function.Size() : 0u,
                    0u,
                    0u,
                    0u,
                    0u,
                    0u
                });
                existing = std::prev(
                    analysis.ForwardCallees.end());
            }

            ++existing->CallsiteCount;
            existing->EventArgumentMask |=
                callsite.EventArgumentMask;
            existing->GroupArgumentMask |=
                callsite.GroupArgumentMask;
        }

        for (DispatchForwardCalleeEvidence& evidence :
             analysis.ForwardCallees)
        {
            std::vector<std::uint32_t> parentRvas;
            for (const DispatchForwardCallsiteEvidence& callsite :
                 analysis.ForwardCallsites)
            {
                if (callsite.CalleeRva != evidence.Rva)
                {
                    continue;
                }
                if (!std::ranges::contains(
                        parentRvas,
                        callsite.ParentRva))
                {
                    parentRvas.push_back(callsite.ParentRva);
                }
            }
            evidence.ParentCount =
                static_cast<std::uint32_t>(parentRvas.size());

            const RuntimeFunctionView function =
                ResolveRuntimeFunction(
                    image,
                    image.Base + evidence.Rva);
            if (function)
            {
                const DispatchFunctionAnalysis nested =
                    AnalyzeDispatchFunction(
                        image,
                        function,
                        RegistersFromArgumentMask(
                            evidence.GroupArgumentMask),
                        RegistersFromArgumentMask(
                            evidence.EventArgumentMask),
                        eventTypeVFuncOffset,
                        eventArgumentsVFuncOffset,
                        eventEntityVFuncOffset);
                evidence.Relations |= nested.Relations;
            }
        }

        std::ranges::sort(
            analysis.Callees,
            [](const DispatchCalleeEvidence& left,
               const DispatchCalleeEvidence& right)
            {
                if (left.WalkerCount != right.WalkerCount)
                {
                    return left.WalkerCount > right.WalkerCount;
                }
                if (left.CallsiteCount != right.CallsiteCount)
                {
                    return left.CallsiteCount > right.CallsiteCount;
                }
                const int leftRelations = std::popcount(left.Relations);
                const int rightRelations = std::popcount(right.Relations);
                if (leftRelations != rightRelations)
                {
                    return leftRelations > rightRelations;
                }
                return left.Rva < right.Rva;
            });

        std::ranges::sort(
            analysis.ForwardCallsites,
            [](const DispatchForwardCallsiteEvidence& left,
               const DispatchForwardCallsiteEvidence& right)
            {
                return left.ParentRva != right.ParentRva
                    ? left.ParentRva < right.ParentRva
                    : left.CallsiteRva < right.CallsiteRva;
            });

        std::ranges::sort(
            analysis.ForwardCallees,
            [](const DispatchForwardCalleeEvidence& left,
               const DispatchForwardCalleeEvidence& right)
            {
                if (left.ParentCount != right.ParentCount)
                {
                    return left.ParentCount > right.ParentCount;
                }
                if (left.CallsiteCount != right.CallsiteCount)
                {
                    return left.CallsiteCount > right.CallsiteCount;
                }
                const int leftRelations = std::popcount(left.Relations);
                const int rightRelations = std::popcount(right.Relations);
                if (leftRelations != rightRelations)
                {
                    return leftRelations > rightRelations;
                }
                return left.Rva < right.Rva;
            });

        return analysis;
    }

    [[nodiscard]] bool FunctionDirectlyCalls(
        ImageView image,
        RuntimeFunctionView caller,
        RuntimeFunctionView callee) noexcept
    {
        if (!caller || !callee)
        {
            return false;
        }

        return VisitDecodedInstructions(
            FunctionBytes(image, caller),
            [image, caller, callee](
                std::size_t instructionOffset,
                const hde64s& instruction)
            {
                const auto targetRva = DirectCallTargetRva(
                    image,
                    caller,
                    instructionOffset,
                    instruction);
                if (!targetRva.has_value())
                {
                    return false;
                }

                return SameFunction(
                    callee,
                    ResolveRuntimeFunction(
                        image,
                        image.Base + *targetRva));
            });
    }

    [[nodiscard]] std::uint32_t FunctionDirectCallRelations(
        ImageView image,
        RuntimeFunctionView caller,
        RuntimeFunctionView countFunction,
        RuntimeFunctionView eventTypeFunction) noexcept
    {
        if (!caller)
        {
            return 0;
        }

        std::uint32_t relations = 0;
        static_cast<void>(VisitDecodedInstructions(
            FunctionBytes(image, caller),
            [image, caller, countFunction, eventTypeFunction, &relations](
                std::size_t instructionOffset,
                const hde64s& instruction)
            {
                const auto targetRva = DirectCallTargetRva(
                    image,
                    caller,
                    instructionOffset,
                    instruction);
                if (!targetRva.has_value())
                {
                    return false;
                }

                const RuntimeFunctionView target = ResolveRuntimeFunction(
                    image,
                    image.Base + *targetRva);
                if (SameFunction(target, countFunction))
                {
                    relations |= RelationCallsCount;
                }
                if (SameFunction(target, eventTypeFunction))
                {
                    relations |= RelationCallsEventType;
                }
                return (relations &
                    (RelationCallsCount | RelationCallsEventType)) ==
                    (RelationCallsCount | RelationCallsEventType);
            }));
        return relations;
    }

    [[nodiscard]] KnownCallerSets FindDirectCallers(
        ImageView image,
        RuntimeFunctionView countFunction,
        RuntimeFunctionView eventTypeFunction)
    {
        KnownCallerSets callers;
        if (image.Base == nullptr)
        {
            return callers;
        }

        std::unordered_set<std::uint32_t> visited;
        const std::span<const std::byte> imageBytes{image.Base, image.Size};
        for (std::size_t offset = 0; offset < imageBytes.size(); ++offset)
        {
            if (std::to_integer<std::uint8_t>(imageBytes[offset]) != 0xE8u)
            {
                continue;
            }

            const RuntimeFunctionView caller =
                ResolveRuntimeFunction(image, image.Base + offset);
            if (!caller || !visited.insert(caller.BeginRva).second)
            {
                continue;
            }

            const std::uint32_t relations = FunctionDirectCallRelations(
                image,
                caller,
                countFunction,
                eventTypeFunction);
            if ((relations & RelationCallsCount) != 0)
            {
                callers.Count.push_back(caller);
            }
            if ((relations & RelationCallsEventType) != 0)
            {
                callers.EventType.push_back(caller);
            }
        }
        return callers;
    }

    [[nodiscard]] bool AnyCallerAlsoCalls(
        ImageView image,
        std::span<const RuntimeFunctionView> callers,
        RuntimeFunctionView candidate) noexcept
    {
        return std::ranges::any_of(
            callers,
            [image, candidate](RuntimeFunctionView caller)
            {
                return !SameFunction(caller, candidate) &&
                    FunctionDirectlyCalls(image, caller, candidate);
            });
    }

    [[nodiscard]] std::uint32_t CandidateRelations(
        ImageView image,
        RuntimeFunctionView candidate,
        RuntimeFunctionView countFunction,
        RuntimeFunctionView eventTypeFunction,
        std::span<const RuntimeFunctionView> countCallers,
        std::span<const RuntimeFunctionView> eventTypeCallers,
        std::int32_t eventStackOffset,
        std::uint32_t eventTypeVFuncOffset) noexcept
    {
        std::uint32_t relations = 0;

        if (SameFunction(candidate, countFunction))
        {
            relations |= RelationIsCountFunction;
        }
        if (SameFunction(candidate, eventTypeFunction))
        {
            relations |= RelationIsEventTypeFunction;
        }
        relations |= FunctionDirectCallRelations(
            image,
            candidate,
            countFunction,
            eventTypeFunction);
        if (FunctionDirectlyCalls(image, countFunction, candidate))
        {
            relations |= RelationCalledByCount;
        }
        if (FunctionDirectlyCalls(image, eventTypeFunction, candidate))
        {
            relations |= RelationCalledByEventType;
        }
        if (AnyCallerAlsoCalls(image, countCallers, candidate))
        {
            relations |= RelationSharesCallerWithCount;
        }
        if (AnyCallerAlsoCalls(image, eventTypeCallers, candidate))
        {
            relations |= RelationSharesCallerWithEventType;
        }

        const EventObjectFlow flow = AnalyzeEventObjectFlow(
            image,
            candidate,
            eventStackOffset,
            eventTypeVFuncOffset);
        if (flow.Verified)
        {
            relations |= RelationEventObjectFlow;
            if (FunctionLoopsOverEventObjectFlow(image, candidate, flow))
            {
                relations |= RelationIterationLoop;
            }
        }
        if (FunctionAddressTaken(image, candidate))
        {
            relations |= RelationAddressTaken;
        }

        return relations;
    }

    [[nodiscard]] std::vector<RuntimeFunctionView> FindFunctionsReferencingOffsets(
        ImageView image,
        std::int32_t eventCountOffset,
        std::int32_t eventStackOffset)
    {
        std::vector<RuntimeFunctionView> functions;
        if (image.Base == nullptr || image.Size < sizeof(eventCountOffset))
        {
            return functions;
        }

        const auto countBytes =
            std::bit_cast<std::array<std::byte, sizeof(eventCountOffset)>>(
                eventCountOffset);
        const std::span<const std::byte> imageBytes{image.Base, image.Size};
        for (std::size_t offset = 0;
             offset + countBytes.size() <= imageBytes.size();
             ++offset)
        {
            if (!std::ranges::equal(
                    imageBytes.subspan(offset, countBytes.size()),
                    countBytes))
            {
                continue;
            }

            const RuntimeFunctionView function =
                ResolveRuntimeFunction(image, image.Base + offset);
            if (!function ||
                !FunctionReferencesDisplacement(
                    image,
                    function,
                    eventCountOffset) ||
                !FunctionReferencesDisplacement(
                    image,
                    function,
                    eventStackOffset))
            {
                continue;
            }

            const bool exists = std::ranges::any_of(
                functions,
                [function](RuntimeFunctionView candidate)
                {
                    return SameFunction(candidate, function);
                });
            if (!exists)
            {
                functions.push_back(function);
            }
        }
        return functions;
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

        const GuardMetadata guardMetadata = ReadGuardMetadata(image);
        result.Diagnostics.GuardFlags = guardMetadata.Flags;
        result.Diagnostics.GuardCFFunctionCount = guardMetadata.FunctionCount;
        result.Diagnostics.GuardLongJumpTargetCount =
            guardMetadata.LongJumpTargetCount;
        result.Diagnostics.GuardAddressTakenIatCount =
            guardMetadata.AddressTakenIatCount;

        const auto countAnchors = FindAll(image, EnhancedCEventCountPattern, 4);
        result.Diagnostics.CEventCountAnchorCount =
            static_cast<std::uint32_t>(countAnchors.size());
        if (countAnchors.empty())
        {
            result.Diagnostics.Failure =
                LocatorFailure::EnhancedCEventCountAnchor;
            return result;
        }

        const RuntimeFunctionView countFunction =
            ResolveRuntimeFunction(image, countAnchors.front());
        if (countFunction)
        {
            result.Diagnostics.CEventCountFunctionRva = countFunction.BeginRva;
            result.Diagnostics.CEventCountFunctionSize = countFunction.Size();
        }

        const auto stackAnchors = FindAll(
            TailView(image, countAnchors.front()),
            EnhancedCEventStackPattern,
            4);
        result.Diagnostics.CEventStackAnchorCount =
            static_cast<std::uint32_t>(stackAnchors.size());
        if (stackAnchors.empty())
        {
            result.Diagnostics.Failure =
                LocatorFailure::EnhancedCEventStackAnchor;
            return result;
        }

        const auto eventTypeAnchors = FindAll(
            image,
            EnhancedEventTypePattern,
            4);
        result.Diagnostics.EventTypeAnchorCount =
            static_cast<std::uint32_t>(eventTypeAnchors.size());
        if (eventTypeAnchors.empty())
        {
            result.Diagnostics.Failure =
                LocatorFailure::EnhancedEventTypeAnchor;
            return result;
        }

        const RuntimeFunctionView eventTypeFunction =
            ResolveRuntimeFunction(image, eventTypeAnchors.front());
        if (eventTypeFunction)
        {
            result.Diagnostics.EventTypeFunctionRva = eventTypeFunction.BeginRva;
            result.Diagnostics.EventTypeFunctionSize = eventTypeFunction.Size();
        }

        const std::byte* stackAnchor = stackAnchors.front();
        if (countFunction)
        {
            std::vector<const std::byte*> sameFunctionAnchors;
            for (const std::byte* candidate : stackAnchors)
            {
                if (SameFunction(
                        countFunction,
                        ResolveRuntimeFunction(image, candidate)))
                {
                    sameFunctionAnchors.push_back(candidate);
                }
            }

            result.Diagnostics.CEventStackFunctionMatchCount =
                static_cast<std::uint32_t>(sameFunctionAnchors.size());
            if (!sameFunctionAnchors.empty())
            {
                stackAnchor = sameFunctionAnchors.front();
            }
        }

        result.Diagnostics.CEventStackAnchorRva = RvaOf(image, stackAnchor);

        if (!Contains(image, countAnchors.front() + 2, sizeof(std::int32_t)) ||
            !Contains(image, stackAnchor + 4, sizeof(std::int32_t)) ||
            !Contains(image, eventTypeAnchors.front() + 20, sizeof(std::uint8_t)))
        {
            result.Diagnostics.Failure =
                LocatorFailure::EnhancedStructureInvalid;
            return result;
        }

        const std::int32_t eventCountOffset = ReadInt32(
            countAnchors.front() + 2);
        const std::int32_t eventStackOffset = ReadInt32(stackAnchor + 4);
        const std::uint32_t eventTypeVFuncOffset =
            std::to_integer<std::uint8_t>(eventTypeAnchors.front()[20]);

        constexpr std::uint32_t EventArgumentsVFuncDelta =
            static_cast<std::uint32_t>(3u * sizeof(void*));
        constexpr std::uint32_t EventEntityVFuncDelta =
            static_cast<std::uint32_t>(22u * sizeof(void*));
        const std::uint32_t eventArgumentsVFuncOffset =
            eventTypeVFuncOffset + EventArgumentsVFuncDelta;
        const std::uint32_t eventEntityVFuncOffset =
            eventTypeVFuncOffset + EventEntityVFuncDelta;

        if (eventCountOffset <= 0 || eventStackOffset <= 0 ||
            eventTypeVFuncOffset == 0 || eventCountOffset > 0x10000 ||
            eventStackOffset > 0x10000 ||
            eventTypeVFuncOffset > 0x800 ||
            eventTypeVFuncOffset % sizeof(void*) != 0 ||
            eventArgumentsVFuncOffset > 0x800 ||
            eventEntityVFuncOffset > 0x800)
        {
            result.Diagnostics.Failure =
                LocatorFailure::EnhancedStructureInvalid;
            return result;
        }

        result.Diagnostics.CEventCountOffset =
            static_cast<std::uint32_t>(eventCountOffset);
        result.Diagnostics.CEventStackOffset =
            static_cast<std::uint32_t>(eventStackOffset);
        result.Diagnostics.EventTypeVFuncOffset = eventTypeVFuncOffset;
        result.Targets.EventIdVFuncOffset = eventTypeVFuncOffset;
        result.Targets.EventArgumentsVFuncOffset = eventArgumentsVFuncOffset;
        result.Targets.EventEntityVFuncOffset = eventEntityVFuncOffset;

        const auto walkerCandidates = FindFunctionsReferencingOffsets(
            image,
            eventCountOffset,
            eventStackOffset);
        result.Diagnostics.EventWalkerCandidateCount =
            static_cast<std::uint32_t>(walkerCandidates.size());
        if (!walkerCandidates.empty())
        {
            result.Diagnostics.EventWalkerCandidateRva =
                walkerCandidates.front().BeginRva;
            result.Diagnostics.EventWalkerCandidateSize =
                walkerCandidates.front().Size();
        }

        std::vector<RuntimeFunctionView> typedWalkerCandidates;
        for (RuntimeFunctionView candidate : walkerCandidates)
        {
            if (FunctionReferencesIndexedStack(
                    image,
                    candidate,
                    eventStackOffset) &&
                FunctionCallsVirtualSlot(
                    image,
                    candidate,
                    eventTypeVFuncOffset))
            {
                typedWalkerCandidates.push_back(candidate);
            }
        }

        result.Diagnostics.TypedEventWalkerCandidateCount =
            static_cast<std::uint32_t>(typedWalkerCandidates.size());
        if (!typedWalkerCandidates.empty())
        {
            result.Diagnostics.TypedEventWalkerCandidateRva =
                typedWalkerCandidates.front().BeginRva;
            result.Diagnostics.TypedEventWalkerCandidateSize =
                typedWalkerCandidates.front().Size();
        }

        const KnownCallerSets knownCallers = FindDirectCallers(
            image,
            countFunction,
            eventTypeFunction);
        result.Diagnostics.DirectCountCallerCount =
            static_cast<std::uint32_t>(knownCallers.Count.size());
        result.Diagnostics.DirectEventTypeCallerCount =
            static_cast<std::uint32_t>(knownCallers.EventType.size());

        std::vector<RelatedCandidate> relatedCandidates;
        relatedCandidates.reserve(typedWalkerCandidates.size());
        for (RuntimeFunctionView candidate : typedWalkerCandidates)
        {
            const std::uint32_t relations = CandidateRelations(
                image,
                candidate,
                countFunction,
                eventTypeFunction,
                knownCallers.Count,
                knownCallers.EventType,
                eventStackOffset,
                eventTypeVFuncOffset);
            constexpr std::uint32_t EvidenceRelations =
                RelationCallsCount |
                RelationCallsEventType |
                RelationCalledByCount |
                RelationCalledByEventType |
                RelationSharesCallerWithCount |
                RelationSharesCallerWithEventType |
                RelationEventObjectFlow |
                RelationIterationLoop |
                RelationAddressTaken;
            if ((relations & EvidenceRelations) != 0)
            {
                relatedCandidates.push_back({candidate, relations});
            }
        }

        std::ranges::sort(
            relatedCandidates,
            [](const RelatedCandidate& left, const RelatedCandidate& right)
            {
                const auto score = [](std::uint32_t relations)
                {
                    int value = std::popcount(relations & 0xFFu);
                    value += (relations & RelationEventObjectFlow) != 0 ? 4 : 0;
                    value += (relations & RelationIterationLoop) != 0 ? 4 : 0;
                    value += (relations & RelationAddressTaken) != 0 ? 2 : 0;
                    return value;
                };
                const int leftScore = score(left.Relations);
                const int rightScore = score(right.Relations);
                return leftScore != rightScore
                    ? leftScore > rightScore
                    : left.Function.BeginRva < right.Function.BeginRva;
            });

        result.Diagnostics.RelatedEventWalkerCandidateCount =
            static_cast<std::uint32_t>(relatedCandidates.size());
        const std::size_t diagnosticCount = std::min(
            relatedCandidates.size(),
            EnhancedDiagnosticCandidateCapacity);
        result.Diagnostics.EnhancedCandidateDiagnosticCount =
            static_cast<std::uint32_t>(diagnosticCount);
        for (std::size_t index = 0; index < diagnosticCount; ++index)
        {
            const RelatedCandidate& candidate = relatedCandidates[index];
            result.Diagnostics.EnhancedCandidates[index] = {
                candidate.Function.BeginRva,
                candidate.Function.Size(),
                candidate.Relations
            };
        }

        std::vector<RuntimeFunctionView> dispatchWalkers;
        dispatchWalkers.reserve(relatedCandidates.size());
        for (const RelatedCandidate& candidate : relatedCandidates)
        {
            dispatchWalkers.push_back(candidate.Function);
        }

        const DispatchCalleeAnalysis dispatchAnalysis =
            AnalyzeDispatchCallees(
                image,
                dispatchWalkers,
                countFunction,
                eventTypeFunction,
                eventStackOffset,
                eventTypeVFuncOffset,
                eventArgumentsVFuncOffset,
                eventEntityVFuncOffset);
        result.Diagnostics.DispatchCallsiteCandidateCount =
            dispatchAnalysis.CallsiteCount;
        const std::size_t dispatchCallsiteDiagnosticCount = std::min(
            dispatchAnalysis.Callsites.size(),
            EnhancedDispatchCallsiteDiagnosticCapacity);
        result.Diagnostics.DispatchCallsiteDiagnosticCount =
            static_cast<std::uint32_t>(
                dispatchCallsiteDiagnosticCount);
        for (std::size_t index = 0;
             index < dispatchCallsiteDiagnosticCount;
             ++index)
        {
            const DispatchCallsiteEvidence& callsite =
                dispatchAnalysis.Callsites[index];
            result.Diagnostics.DispatchCallsites[index] = {
                callsite.WalkerRva,
                callsite.CallsiteRva,
                callsite.CalleeRva,
                callsite.Relations
            };
        }
        result.Diagnostics.DispatchCalleeCandidateCount =
            static_cast<std::uint32_t>(
                dispatchAnalysis.Callees.size());
        const std::size_t dispatchDiagnosticCount = std::min(
            dispatchAnalysis.Callees.size(),
            EnhancedDispatchCalleeDiagnosticCapacity);
        result.Diagnostics.DispatchCalleeDiagnosticCount =
            static_cast<std::uint32_t>(
                dispatchDiagnosticCount);
        for (std::size_t index = 0;
             index < dispatchDiagnosticCount;
             ++index)
        {
            const DispatchCalleeEvidence& candidate =
                dispatchAnalysis.Callees[index];
            result.Diagnostics.DispatchCallees[index] = {
                candidate.Rva,
                candidate.Size,
                candidate.Relations,
                candidate.CallsiteCount,
                candidate.WalkerCount
            };
        }

        result.Diagnostics.DispatchForwardCallsiteCandidateCount =
            static_cast<std::uint32_t>(
                dispatchAnalysis.ForwardCallsites.size());
        const std::size_t dispatchForwardCallsiteDiagnosticCount = std::min(
            dispatchAnalysis.ForwardCallsites.size(),
            EnhancedDispatchForwardCallsiteDiagnosticCapacity);
        result.Diagnostics.DispatchForwardCallsiteDiagnosticCount =
            static_cast<std::uint32_t>(
                dispatchForwardCallsiteDiagnosticCount);
        for (std::size_t index = 0;
             index < dispatchForwardCallsiteDiagnosticCount;
             ++index)
        {
            const DispatchForwardCallsiteEvidence& callsite =
                dispatchAnalysis.ForwardCallsites[index];
            result.Diagnostics.DispatchForwardCallsites[index] = {
                callsite.ParentRva,
                callsite.CallsiteRva,
                callsite.CalleeRva,
                callsite.EventArgumentMask,
                callsite.GroupArgumentMask
            };
        }

        result.Diagnostics.DispatchForwardCalleeCandidateCount =
            static_cast<std::uint32_t>(
                dispatchAnalysis.ForwardCallees.size());
        const std::size_t dispatchForwardCalleeDiagnosticCount = std::min(
            dispatchAnalysis.ForwardCallees.size(),
            EnhancedDispatchForwardCalleeDiagnosticCapacity);
        result.Diagnostics.DispatchForwardCalleeDiagnosticCount =
            static_cast<std::uint32_t>(
                dispatchForwardCalleeDiagnosticCount);
        for (std::size_t index = 0;
             index < dispatchForwardCalleeDiagnosticCount;
             ++index)
        {
            const DispatchForwardCalleeEvidence& candidate =
                dispatchAnalysis.ForwardCallees[index];
            result.Diagnostics.DispatchForwardCallees[index] = {
                candidate.Rva,
                candidate.Size,
                candidate.Relations,
                candidate.CallsiteCount,
                candidate.ParentCount,
                candidate.EventArgumentMask,
                candidate.GroupArgumentMask
            };
        }

        const EventConsumerAnalysis consumerAnalysis =
            AnalyzeEventConsumers(
                image,
                typedWalkerCandidates,
                countFunction,
                eventTypeFunction,
                eventStackOffset);
        result.Diagnostics.EventConsumerCallsiteCandidateCount =
            static_cast<std::uint32_t>(consumerAnalysis.Callsites.size());
        const std::size_t consumerCallsiteDiagnosticCount = std::min(
            consumerAnalysis.Callsites.size(),
            EnhancedEventConsumerCallsiteDiagnosticCapacity);
        result.Diagnostics.EventConsumerCallsiteDiagnosticCount =
            static_cast<std::uint32_t>(consumerCallsiteDiagnosticCount);
        for (std::size_t index = 0;
             index < consumerCallsiteDiagnosticCount;
             ++index)
        {
            const EventConsumerCallsiteEvidence& callsite =
                consumerAnalysis.Callsites[index];
            result.Diagnostics.EventConsumerCallsites[index] = {
                callsite.WalkerRva,
                callsite.CallsiteRva,
                callsite.CalleeRva,
                callsite.EventArgumentMask,
                callsite.Relations,
                callsite.InstructionLength,
                callsite.Encoding[0],
                callsite.Encoding[1],
                callsite.Encoding[2],
                callsite.Encoding[3],
                callsite.OperandInfo,
                callsite.Displacement
            };
        }

        result.Diagnostics.EventConsumerCandidateCount =
            static_cast<std::uint32_t>(consumerAnalysis.Consumers.size());
        const std::size_t consumerDiagnosticCount = std::min(
            consumerAnalysis.Consumers.size(),
            EnhancedEventConsumerDiagnosticCapacity);
        result.Diagnostics.EventConsumerDiagnosticCount =
            static_cast<std::uint32_t>(consumerDiagnosticCount);
        for (std::size_t index = 0;
             index < consumerDiagnosticCount;
             ++index)
        {
            const EventConsumerEvidence& consumer =
                consumerAnalysis.Consumers[index];
            result.Diagnostics.EventConsumers[index] = {
                consumer.Rva,
                consumer.Size,
                consumer.Relations,
                consumer.EventArgumentMask,
                consumer.FirstUseRva,
                consumer.FirstClobberRva,
                consumer.CallsiteCount,
                consumer.WalkerCount,
                consumer.ReadCount,
                consumer.MemoryStoreCount,
                consumer.CompareCount,
                consumer.VtableLoadCount,
                consumer.ForwardCount,
                consumer.TailForwardCount,
                consumer.TailInstructionRva,
                consumer.TailInstructionLength,
                consumer.TailEncoding[0],
                consumer.TailEncoding[1],
                consumer.TailEncoding[2],
                consumer.TailEncoding[3],
                consumer.TailOperandInfo,
                consumer.TailDisplacement,
                consumer.TailBaseRegister,
                consumer.TailIndexRegister,
                consumer.TailScale,
                consumer.TailImageSize,
                consumer.TailSlotRva,
                consumer.TailSlotAddress,
                consumer.TailSlotProtection,
                consumer.TailSlotFlags,
                consumer.TailTargetAddress,
                consumer.TailTargetProtection,
                consumer.TailTargetFlags,
                consumer.TailTargetModuleBase,
                consumer.TailTargetModuleSize,
                consumer.TailTargetModuleRva
            };
        }

        const VirtualSlotAnalysis virtualSlotAnalysis =
            AnalyzeEventVirtualSlots(
                image,
                typedWalkerCandidates,
                eventStackOffset,
                eventTypeVFuncOffset,
                eventArgumentsVFuncOffset,
                eventEntityVFuncOffset);
        result.Diagnostics.VirtualSlotCandidateCount =
            static_cast<std::uint32_t>(virtualSlotAnalysis.Slots.size());
        const std::size_t virtualSlotDiagnosticCount = std::min(
            virtualSlotAnalysis.Slots.size(),
            EnhancedVirtualSlotDiagnosticCapacity);
        result.Diagnostics.VirtualSlotDiagnosticCount =
            static_cast<std::uint32_t>(virtualSlotDiagnosticCount);
        for (std::size_t index = 0;
             index < virtualSlotDiagnosticCount;
             ++index)
        {
            const VirtualSlotEvidence& slot = virtualSlotAnalysis.Slots[index];
            result.Diagnostics.VirtualSlots[index] = {
                slot.SlotOffset,
                slot.Relations,
                slot.CallsiteCount,
                slot.WalkerCount,
                slot.EventRcxCount,
                slot.EventArgumentMask,
                slot.ReturnRelations,
                slot.ReturnUseCount
            };
        }
        result.Diagnostics.VirtualSlotCallsiteCandidateCount =
            static_cast<std::uint32_t>(virtualSlotAnalysis.Callsites.size());
        const std::size_t virtualSlotCallsiteDiagnosticCount = std::min(
            virtualSlotAnalysis.Callsites.size(),
            EnhancedVirtualSlotCallsiteDiagnosticCapacity);
        result.Diagnostics.VirtualSlotCallsiteDiagnosticCount =
            static_cast<std::uint32_t>(virtualSlotCallsiteDiagnosticCount);
        for (std::size_t index = 0;
             index < virtualSlotCallsiteDiagnosticCount;
             ++index)
        {
            const VirtualSlotCallsiteEvidence& callsite =
                virtualSlotAnalysis.Callsites[index];
            result.Diagnostics.VirtualSlotCallsites[index] = {
                callsite.WalkerRva,
                callsite.CallsiteRva,
                callsite.SlotOffset,
                callsite.Relations,
                callsite.EventArgumentMask,
                callsite.ReturnRelations,
                callsite.ReturnFirstUseRva,
                callsite.ReturnFirstClobberRva,
                callsite.PreparedArgumentMask,
                callsite.MemoryArgumentMask,
                callsite.AddressArgumentMask,
                callsite.ImmediateArgumentMask,
                callsite.ZeroArgumentMask,
                callsite.Rdx.Rva,
                callsite.Rdx.Kind,
                callsite.Rdx.Encoding,
                callsite.Rdx.OperandInfo,
                callsite.Rdx.Displacement,
                callsite.Rdx.Value,
                callsite.R8.Rva,
                callsite.R8.Kind,
                callsite.R8.Encoding,
                callsite.R8.OperandInfo,
                callsite.R8.Displacement,
                callsite.R8.Value,
                callsite.R9.Rva,
                callsite.R9.Kind,
                callsite.R9.Encoding,
                callsite.R9.OperandInfo,
                callsite.R9.Displacement,
                callsite.R9.Value
            };
        }

        const RttiEventTypeAnalysis rttiAnalysis =
            AnalyzeEventRttiTypes(image);
        result.Diagnostics.RttiEventTypeCandidateCount =
            rttiAnalysis.CandidateCount;
        const std::size_t rttiDiagnosticCount = std::min(
            rttiAnalysis.Types.size(),
            EnhancedRttiEventTypeDiagnosticCapacity);
        result.Diagnostics.RttiEventTypeDiagnosticCount =
            static_cast<std::uint32_t>(rttiDiagnosticCount);
        for (std::size_t index = 0;
             index < rttiDiagnosticCount;
             ++index)
        {
            const RttiEventTypeEvidence& type =
                rttiAnalysis.Types[index];
            result.Diagnostics.RttiEventTypes[index] = {
                type.TypeNameRva,
                type.TypeDescriptorRva,
                type.CompleteObjectLocatorRva,
                type.VftableRva,
                type.EventTypeValue,
                type.Slot30TargetRva,
                type.Slot48TargetRva,
                type.Slot50TargetRva,
                type.Slot60TargetRva,
                type.Slot68TargetRva,
                type.SlotB0TargetRva,
                type.SlotF8TargetRva
            };
        }

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