#include "CoreCLRHostLoader.hpp"

#include <rometadata.h>
#include <rometadataapi.h>

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cwctype>
#include <exception>
#include <limits>
#include <map>
#include <span>
#include <utility>

#pragma comment(lib, "Rometadata.lib")

namespace CoreCLRHostLoader
{
    namespace
    {
        constexpr std::wstring_view AssemblyMetadataAttributeName =
            L"System.Reflection.AssemblyMetadataAttribute";

        constexpr std::wstring_view RoleKey = L"CCHL.Role";
        constexpr std::wstring_view ContractIdKey = L"CCHL.ContractId";
        constexpr std::wstring_view AbiMajorKey = L"CCHL.AbiMajor";
        constexpr std::wstring_view AbiMinorKey = L"CCHL.AbiMinor";
        constexpr std::wstring_view EntryTypeKey = L"CCHL.EntryType";
        constexpr std::wstring_view EntryMethodKey =
            L"CCHL.EntryMethod";
        constexpr std::wstring_view RuntimeTfmKey = L"CCHL.RuntimeTfm";
        constexpr std::wstring_view RuntimeFrameworkKey =
            L"CCHL.RuntimeFramework";
        constexpr std::wstring_view RuntimeVersionKey =
            L"CCHL.RuntimeVersion";

        template <typename T>
        class ComOwner final
        {
        public:
            explicit ComOwner(T* value = nullptr) noexcept
                : m_value(value)
            {
            }

            ~ComOwner()
            {
                if (m_value != nullptr)
                {
                    m_value->Release();
                }
            }

            ComOwner(const ComOwner&) = delete;
            ComOwner& operator=(const ComOwner&) = delete;
            ComOwner(ComOwner&&) = delete;
            ComOwner& operator=(ComOwner&&) = delete;

            [[nodiscard]]
            T* Get() const noexcept
            {
                return m_value;
            }

            [[nodiscard]]
            T* operator->() const noexcept
            {
                return m_value;
            }

        private:
            T* m_value;
        };

        class MetadataEnumeration final
        {
        public:
            explicit MetadataEnumeration(IMetaDataImport* metadata) noexcept
                : m_metadata(metadata)
            {
            }

            ~MetadataEnumeration()
            {
                if (m_metadata != nullptr && m_handle != nullptr)
                {
                    m_metadata->CloseEnum(m_handle);
                }
            }

            MetadataEnumeration(const MetadataEnumeration&) = delete;
            MetadataEnumeration& operator=(const MetadataEnumeration&) = delete;
            MetadataEnumeration(MetadataEnumeration&&) = delete;
            MetadataEnumeration& operator=(MetadataEnumeration&&) = delete;

            [[nodiscard]]
            HCORENUM* Address() noexcept
            {
                return &m_handle;
            }

        private:
            IMetaDataImport* m_metadata;
            HCORENUM m_handle = nullptr;
        };

        class AttributeBlobReader final
        {
        public:
            explicit AttributeBlobReader(
                std::span<const std::uint8_t> bytes) noexcept
                : m_bytes(bytes)
            {
            }

            [[nodiscard]]
            bool ReadUInt16(std::uint16_t& value) noexcept
            {
                if (m_offset > m_bytes.size() ||
                    m_bytes.size() - m_offset < sizeof(std::uint16_t))
                {
                    return false;
                }

                value = static_cast<std::uint16_t>(m_bytes[m_offset]) |
                    static_cast<std::uint16_t>(
                        static_cast<std::uint16_t>(
                            m_bytes[m_offset + 1]) << 8);
                m_offset += sizeof(std::uint16_t);
                return true;
            }

            [[nodiscard]]
            bool ReadString(std::wstring& value)
            {
                if (m_offset >= m_bytes.size() || m_bytes[m_offset] == 0xFF)
                {
                    return false;
                }

                std::uint32_t length = 0;
                if (!ReadCompressedUInt(length) ||
                    m_offset > m_bytes.size() ||
                    length > m_bytes.size() - m_offset ||
                    length > static_cast<std::uint32_t>(
                        (std::numeric_limits<int>::max)()))
                {
                    return false;
                }

                if (length == 0)
                {
                    value.clear();
                    return true;
                }

                const int required = MultiByteToWideChar(
                    CP_UTF8,
                    MB_ERR_INVALID_CHARS,
                    reinterpret_cast<const char*>(
                        m_bytes.data() + m_offset),
                    static_cast<int>(length),
                    nullptr,
                    0);
                if (required <= 0)
                {
                    return false;
                }

                value.assign(static_cast<std::size_t>(required), L'\0');
                if (MultiByteToWideChar(
                        CP_UTF8,
                        MB_ERR_INVALID_CHARS,
                        reinterpret_cast<const char*>(
                            m_bytes.data() + m_offset),
                        static_cast<int>(length),
                        value.data(),
                        required) != required)
                {
                    return false;
                }

                m_offset += length;
                return true;
            }

            [[nodiscard]]
            bool AtEnd() const noexcept
            {
                return m_offset == m_bytes.size();
            }

        private:
            [[nodiscard]]
            bool ReadCompressedUInt(std::uint32_t& value) noexcept
            {
                if (m_offset >= m_bytes.size())
                {
                    return false;
                }

                const std::uint8_t first = m_bytes[m_offset++];
                if ((first & 0x80u) == 0)
                {
                    value = first;
                    return true;
                }

                if ((first & 0xC0u) == 0x80u)
                {
                    if (m_offset >= m_bytes.size())
                    {
                        return false;
                    }
                    value =
                        (static_cast<std::uint32_t>(first & 0x3Fu) << 8) |
                        m_bytes[m_offset++];
                    return true;
                }

                if ((first & 0xE0u) == 0xC0u)
                {
                    if (m_offset > m_bytes.size() ||
                        m_bytes.size() - m_offset < 3)
                    {
                        return false;
                    }
                    value =
                        (static_cast<std::uint32_t>(first & 0x1Fu) << 24) |
                        (static_cast<std::uint32_t>(
                            m_bytes[m_offset]) << 16) |
                        (static_cast<std::uint32_t>(
                            m_bytes[m_offset + 1]) << 8) |
                        static_cast<std::uint32_t>(m_bytes[m_offset + 2]);
                    m_offset += 3;
                    return true;
                }

                return false;
            }

            std::span<const std::uint8_t> m_bytes;
            std::size_t m_offset = 0;
        };

        using MetadataValues =
            std::map<std::wstring, std::wstring, std::less<>>;

        [[nodiscard]]
        bool IsDllPath(const std::filesystem::path& path) noexcept
        {
            std::wstring extension = path.extension().wstring();
            std::ranges::transform(
                extension,
                extension.begin(),
                [](wchar_t character)
                {
                    return std::towlower(character);
                });
            return extension == L".dll";
        }

        [[nodiscard]]
        bool IsSupportedText(std::wstring_view value) noexcept
        {
            return !value.empty() && value.size() <= 1024 &&
                std::ranges::none_of(value, [](wchar_t character)
                {
                    return character < 0x20;
                });
        }

        [[nodiscard]]
        bool IsAssemblyMetadataConstructor(
            IMetaDataImport* metadata,
            mdToken constructorToken)
        {
            if (metadata == nullptr ||
                TypeFromToken(constructorToken) != mdtMemberRef)
            {
                return false;
            }

            mdToken parent = mdTokenNil;
            std::array<wchar_t, 32> memberName{};
            ULONG memberNameLength = 0;
            PCCOR_SIGNATURE signature = nullptr;
            ULONG signatureSize = 0;
            HRESULT status = metadata->GetMemberRefProps(
                static_cast<mdMemberRef>(constructorToken),
                &parent,
                memberName.data(),
                static_cast<ULONG>(memberName.size()),
                &memberNameLength,
                &signature,
                &signatureSize);
            if (FAILED(status) ||
                std::wstring_view(memberName.data()) != L".ctor" ||
                TypeFromToken(parent) != mdtTypeRef)
            {
                return false;
            }

            mdToken resolutionScope = mdTokenNil;
            std::array<wchar_t, 128> typeName{};
            ULONG typeNameLength = 0;
            status = metadata->GetTypeRefProps(
                static_cast<mdTypeRef>(parent),
                &resolutionScope,
                typeName.data(),
                static_cast<ULONG>(typeName.size()),
                &typeNameLength);
            return SUCCEEDED(status) &&
                std::wstring_view(typeName.data()) ==
                    AssemblyMetadataAttributeName;
        }

        [[nodiscard]]
        HostResult<MetadataValues> ReadAssemblyMetadata(
            IMetaDataImport* metadata,
            mdAssembly assemblyToken,
            const std::filesystem::path& path)
        {
            MetadataValues values;
            MetadataEnumeration enumeration(metadata);
            std::array<mdCustomAttribute, 16> attributes{};

            for (;;)
            {
                ULONG count = 0;
                const HRESULT status = metadata->EnumCustomAttributes(
                    enumeration.Address(),
                    assemblyToken,
                    0,
                    attributes.data(),
                    static_cast<ULONG>(attributes.size()),
                    &count);
                if (FAILED(status))
                {
                    return std::unexpected(
                        L"Assembly metadata could not be enumerated in " +
                        path.filename().wstring() + L".");
                }

                for (ULONG index = 0; index < count; ++index)
                {
                    mdToken owner = mdTokenNil;
                    mdToken constructor = mdTokenNil;
                    const void* rawBlob = nullptr;
                    ULONG blobSize = 0;
                    const HRESULT propertiesStatus =
                        metadata->GetCustomAttributeProps(
                            attributes[index],
                            &owner,
                            &constructor,
                            &rawBlob,
                            &blobSize);
                    if (FAILED(propertiesStatus) || owner != assemblyToken)
                    {
                        return std::unexpected(
                            L"A custom attribute could not be read from " +
                            path.filename().wstring() + L".");
                    }

                    if (!IsAssemblyMetadataConstructor(
                            metadata,
                            constructor))
                    {
                        continue;
                    }

                    if (rawBlob == nullptr || blobSize == 0)
                    {
                        return std::unexpected(
                            L"An assembly metadata attribute is empty in " +
                            path.filename().wstring() + L".");
                    }

                    AttributeBlobReader reader(
                        std::span<const std::uint8_t>(
                            static_cast<const std::uint8_t*>(rawBlob),
                            blobSize));
                    std::uint16_t prolog = 0;
                    std::wstring key;
                    std::wstring value;
                    std::uint16_t namedArgumentCount = 0;
                    if (!reader.ReadUInt16(prolog) || prolog != 1 ||
                        !reader.ReadString(key) ||
                        !reader.ReadString(value) ||
                        !reader.ReadUInt16(namedArgumentCount) ||
                        namedArgumentCount != 0 ||
                        !reader.AtEnd())
                    {
                        return std::unexpected(
                            L"An assembly metadata attribute is malformed in " +
                            path.filename().wstring() + L".");
                    }

                    if (key.starts_with(L"CCHL.") &&
                        !values.emplace(
                            std::move(key),
                            std::move(value)).second)
                    {
                        return std::unexpected(
                            L"A CCHL assembly metadata key is duplicated in " +
                            path.filename().wstring() + L".");
                    }
                }

                if (count < static_cast<ULONG>(attributes.size()))
                {
                    break;
                }
            }

            return values;
        }

        [[nodiscard]]
        const std::wstring* FindValue(
            const MetadataValues& values,
            std::wstring_view key) noexcept
        {
            const auto found = values.find(key);
            return found == values.end() ? nullptr : &found->second;
        }

        [[nodiscard]]
        bool ParseUInt16(
            std::wstring_view text,
            std::uint16_t& value) noexcept
        {
            if (text.empty())
            {
                return false;
            }

            std::uint32_t parsed = 0;
            for (const wchar_t character : text)
            {
                if (character < L'0' || character > L'9')
                {
                    return false;
                }
                const std::uint32_t digit =
                    static_cast<std::uint32_t>(character - L'0');
                if (parsed >
                    ((std::numeric_limits<std::uint16_t>::max)() - digit) /
                        10u)
                {
                    return false;
                }
                parsed = parsed * 10u + digit;
            }

            value = static_cast<std::uint16_t>(parsed);
            return true;
        }

        [[nodiscard]]
        HostResult<std::optional<ManagedBrain>> InspectCandidate(
            const std::filesystem::path& path)
        {
            void* rawDispenser = nullptr;
            HRESULT status = MetaDataGetDispenser(
                CLSID_CorMetaDataDispenser,
                IID_IMetaDataDispenser,
                &rawDispenser);
            if (FAILED(status) || rawDispenser == nullptr)
            {
                return std::unexpected(
                    L"Windows metadata services could not be initialized.");
            }
            ComOwner dispenser(
                static_cast<IMetaDataDispenser*>(rawDispenser));

            IUnknown* rawScope = nullptr;
            const std::wstring pathText = path.wstring();
            status = dispenser->OpenScope(
                pathText.c_str(),
                0,
                IID_IMetaDataImport,
                &rawScope);
            if (FAILED(status) || rawScope == nullptr)
            {
                return std::optional<ManagedBrain>{};
            }
            ComOwner scope(rawScope);

            void* rawImport = nullptr;
            status = scope->QueryInterface(IID_IMetaDataImport, &rawImport);
            if (FAILED(status) || rawImport == nullptr)
            {
                return std::optional<ManagedBrain>{};
            }
            ComOwner metadata(static_cast<IMetaDataImport*>(rawImport));

            void* rawAssemblyImport = nullptr;
            status = scope->QueryInterface(
                IID_IMetaDataAssemblyImport,
                &rawAssemblyImport);
            if (FAILED(status) || rawAssemblyImport == nullptr)
            {
                return std::optional<ManagedBrain>{};
            }
            ComOwner assemblyMetadata(
                static_cast<IMetaDataAssemblyImport*>(rawAssemblyImport));

            mdAssembly assemblyToken = mdAssemblyNil;
            status = assemblyMetadata->GetAssemblyFromScope(&assemblyToken);
            if (FAILED(status) || assemblyToken == mdAssemblyNil)
            {
                return std::optional<ManagedBrain>{};
            }

            auto values = ReadAssemblyMetadata(
                metadata.Get(),
                assemblyToken,
                path);
            if (!values)
            {
                return std::unexpected(values.error());
            }

            const std::wstring* role = FindValue(*values, RoleKey);
            if (role == nullptr || *role != L"ManagedBrain")
            {
                return std::optional<ManagedBrain>{};
            }

            const std::wstring* contractId =
                FindValue(*values, ContractIdKey);
            const std::wstring* abiMajorText =
                FindValue(*values, AbiMajorKey);
            const std::wstring* abiMinorText =
                FindValue(*values, AbiMinorKey);
            const std::wstring* entryType =
                FindValue(*values, EntryTypeKey);
            const std::wstring* entryMethod =
                FindValue(*values, EntryMethodKey);
            const std::wstring* runtimeTfm =
                FindValue(*values, RuntimeTfmKey);
            const std::wstring* runtimeFramework =
                FindValue(*values, RuntimeFrameworkKey);
            const std::wstring* runtimeVersion =
                FindValue(*values, RuntimeVersionKey);

            ManagedBrain brain{};
            if (contractId == nullptr ||
                abiMajorText == nullptr ||
                abiMinorText == nullptr ||
                entryType == nullptr ||
                entryMethod == nullptr ||
                runtimeTfm == nullptr ||
                runtimeFramework == nullptr ||
                runtimeVersion == nullptr ||
                *contractId != ManagedBrainContractId ||
                !ParseUInt16(*abiMajorText, brain.AbiMajor) ||
                !ParseUInt16(*abiMinorText, brain.AbiMinor) ||
                brain.AbiMajor != ManagedBrainAbiMajor ||
                brain.AbiMinor > ManagedBrainAbiMinor)
            {
                return std::unexpected(
                    L"The managed-brain contract is missing or incompatible in " +
                    path.filename().wstring() + L".");
            }

            if (!IsSupportedText(*entryType) ||
                !IsSupportedText(*entryMethod) ||
                !IsSupportedText(*runtimeTfm) ||
                !IsSupportedText(*runtimeFramework) ||
                !IsSupportedText(*runtimeVersion))
            {
                return std::unexpected(
                    L"The managed-brain runtime metadata is invalid in " +
                    path.filename().wstring() + L".");
            }

            brain.Assembly = path;
            brain.AssemblyName = path.stem().wstring();
            brain.EntryType = *entryType;
            brain.EntryMethod = *entryMethod;
            brain.RuntimeTfm = *runtimeTfm;
            brain.RuntimeFramework = *runtimeFramework;
            brain.RuntimeVersion = *runtimeVersion;
            return std::optional<ManagedBrain>(std::move(brain));
        }

        [[nodiscard]]
        std::wstring JoinCandidateNames(
            std::span<const ManagedBrain> candidates)
        {
            std::wstring result;
            for (std::size_t index = 0; index < candidates.size(); ++index)
            {
                if (index != 0)
                {
                    result += L", ";
                }
                result += candidates[index].Assembly.filename().wstring();
            }
            return result;
        }
    }

    HostResult<std::optional<ManagedBrain>> DiscoverManagedBrain(
        const HostState& state) noexcept
    {
        try
        {
            if (!state.Configuration.BrainAssembly.empty())
            {
                const std::filesystem::path configured =
                    state.Paths.Directory /
                    (state.Configuration.BrainAssembly + L".dll");
                auto inspected = InspectCandidate(configured);
                if (!inspected)
                {
                    WriteLog(LogLevel::Warning, inspected.error());
                }
                else if (*inspected)
                {
                    return inspected;
                }
                else
                {
                    WriteLog(
                        LogLevel::Warning,
                        L"The configured managed brain is missing or no longer "
                        L"declares the required contract. Discovery will run again.");
                }
            }

            std::vector<ManagedBrain> candidates;
            std::error_code error;
            for (const auto& entry : std::filesystem::directory_iterator(
                     state.Paths.Directory,
                     std::filesystem::directory_options::skip_permission_denied,
                     error))
            {
                if (error)
                {
                    break;
                }
                if (!entry.is_regular_file(error) ||
                    !IsDllPath(entry.path()))
                {
                    continue;
                }

                auto inspected = InspectCandidate(entry.path());
                if (!inspected)
                {
                    WriteLog(LogLevel::Warning, inspected.error());
                    continue;
                }
                if (*inspected)
                {
                    candidates.push_back(std::move(**inspected));
                }
            }

            if (error)
            {
                return std::unexpected(
                    L"The host directory could not be scanned for a managed brain.");
            }
            if (candidates.empty())
            {
                return std::optional<ManagedBrain>{};
            }
            if (candidates.size() != 1)
            {
                return std::unexpected(
                    L"More than one compatible managed brain was found: " +
                    JoinCandidateNames(
                        std::span<const ManagedBrain>(candidates)) +
                    L". Select one with [Brain] Assembly in "
                    L"CoreCLRHostLoader.ini.");
            }
            return std::optional<ManagedBrain>(
                std::move(candidates.front()));
        }
        catch (const std::exception& exception)
        {
            const int required = MultiByteToWideChar(
                CP_UTF8,
                0,
                exception.what(),
                -1,
                nullptr,
                0);
            std::wstring message;
            if (required > 1)
            {
                std::wstring buffer(
                    static_cast<std::size_t>(required),
                    L'\0');
                if (MultiByteToWideChar(
                        CP_UTF8,
                        0,
                        exception.what(),
                        -1,
                        buffer.data(),
                        required) == required)
                {
                    buffer.pop_back();
                    message = std::move(buffer);
                }
            }
            return std::unexpected(
                L"Managed-brain discovery failed: " + message);
        }
        catch (...)
        {
            return std::unexpected(
                L"Managed-brain discovery failed with an unknown exception.");
        }
    }
}
