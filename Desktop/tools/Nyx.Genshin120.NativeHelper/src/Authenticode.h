#pragma once

#include <Windows.h>
#include <cwchar>

namespace nyx120
{
    inline bool IsExpectedExecutableName(const wchar_t* name) noexcept
    {
        return name && std::wcscmp(name, L"GenshinImpact.exe") == 0;
    }

    inline bool IsExpectedPublisher(const wchar_t* publisher) noexcept
    {
        return publisher && std::wcscmp(publisher, L"COGNOSPHERE PTE. LTD.") == 0;
    }

    bool HasExpectedCachedAuthenticodePublisher(HANDLE pinnedFile, const wchar_t* canonicalPath) noexcept;
}
