#include "Protocol.h"
#include "Authenticode.h"
#include <cassert>

int main()
{
    using nyx120::Result;
    assert(nyx120::IsCanonicalGuid(L"01234567-89ab-cdef-0123-456789abcdef"));
    assert(!nyx120::IsCanonicalGuid(L"{01234567-89ab-cdef-0123-456789abcdef}"));
    assert(!nyx120::IsCanonicalGuid(L"01234567-89ab-cdef-0123-456789abcdeg"));
    assert(static_cast<unsigned>(Result::Ready) == 0);
    assert(static_cast<unsigned>(Result::GameStartedAttachFailed) == 1);
    assert(static_cast<unsigned>(Result::GameStartedAttachTimedOut) == 2);
    assert(static_cast<unsigned>(Result::InvalidRequest) == 3);
    assert(static_cast<unsigned>(Result::StartFailure) == 4);
    assert(nyx120::MaximumRequestBytes == 64 * 1024);
    assert(nyx120::IsExpectedExecutableName(L"GenshinImpact.exe"));
    assert(!nyx120::IsExpectedExecutableName(L"NotGenshinImpact.exe"));
    assert(nyx120::IsExpectedPublisher(L"COGNOSPHERE PTE. LTD."));
    assert(!nyx120::IsExpectedPublisher(L"COGNOSPHERE PTE. LTD. FAKE"));

    wchar_t self[MAX_PATH]{};
    assert(GetModuleFileNameW(nullptr, self, ARRAYSIZE(self)) > 0);
    HANDLE file = CreateFileW(self, GENERIC_READ | FILE_READ_ATTRIBUTES, FILE_SHARE_READ,
        nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    assert(file != INVALID_HANDLE_VALUE);
    assert(!nyx120::HasExpectedCachedAuthenticodePublisher(file, self));
    CloseHandle(file);
}
