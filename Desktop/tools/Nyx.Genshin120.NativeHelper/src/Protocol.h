#pragma once

#include <cstdint>
#include <cwchar>

namespace nyx120
{
    constexpr std::uint32_t RequestMagic = 0x3152584E;  // NXR1
    constexpr std::uint32_t ResponseMagic = 0x3153584E; // NXS1
    constexpr std::uint16_t ProtocolVersion = 1;
    constexpr std::uint32_t MaximumRequestBytes = 64 * 1024;
    constexpr std::uint32_t MaximumPathChars = 32 * 1024;
    constexpr std::uint32_t MaximumArgumentChars = 4096;
    constexpr std::uint32_t MaximumArguments = 64;

    enum class Result : std::uint32_t
    {
        Ready = 0,
        GameStartedAttachFailed = 1,
        GameStartedAttachTimedOut = 2,
        InvalidRequest = 3,
        StartFailure = 4,
    };

    constexpr Result ResultWithoutGameWindow(bool processExited, bool modalDialogSeen) noexcept
    {
        return processExited || modalDialogSeen ? Result::StartFailure : Result::GameStartedAttachTimedOut;
    }

#pragma pack(push, 1)
    struct RequestHeader
    {
        std::uint32_t Magic;
        std::uint16_t Version;
        std::uint16_t Reserved;
        std::uint32_t PayloadBytes;
    };

    struct Response
    {
        std::uint32_t Magic;
        std::uint16_t Version;
        std::uint16_t Reserved;
        Result Status;
    };
#pragma pack(pop)

    static_assert(sizeof(RequestHeader) == 12);
    static_assert(sizeof(Response) == 12);

    inline bool IsCanonicalGuid(const wchar_t* text) noexcept
    {
        if (!text || std::wcslen(text) != 36) return false;
        for (std::size_t i = 0; i < 36; ++i)
        {
            if (i == 8 || i == 13 || i == 18 || i == 23)
            {
                if (text[i] != L'-') return false;
            }
            else if (!((text[i] >= L'0' && text[i] <= L'9') ||
                       (text[i] >= L'a' && text[i] <= L'f') ||
                       (text[i] >= L'A' && text[i] <= L'F'))) return false;
        }
        return true;
    }
}
