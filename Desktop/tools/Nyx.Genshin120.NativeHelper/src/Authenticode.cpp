#include "Authenticode.h"

#include <Softpub.h>
#include <Wintrust.h>
#include <wincrypt.h>

namespace nyx120
{
    bool HasExpectedCachedAuthenticodePublisher(HANDLE pinnedFile, const wchar_t* canonicalPath) noexcept
    {
        if (!pinnedFile || pinnedFile == INVALID_HANDLE_VALUE || !canonicalPath) return false;

        WINTRUST_FILE_INFO file{};
        file.cbStruct = sizeof(file);
        file.pcwszFilePath = canonicalPath;
        file.hFile = pinnedFile;

        WINTRUST_DATA trust{};
        trust.cbStruct = sizeof(trust);
        trust.dwUIChoice = WTD_UI_NONE;
        trust.fdwRevocationChecks = WTD_REVOKE_NONE;
        trust.dwUnionChoice = WTD_CHOICE_FILE;
        trust.pFile = &file;
        trust.dwStateAction = WTD_STATEACTION_VERIFY;
        trust.dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL;

        GUID action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
        bool accepted = false;
        if (WinVerifyTrust(nullptr, &action, &trust) == ERROR_SUCCESS)
        {
            auto* provider = WTHelperProvDataFromStateData(trust.hWVTStateData);
            auto* signer = provider ? WTHelperGetProvSignerFromChain(provider, 0, FALSE, 0) : nullptr;
            const PCCERT_CONTEXT certificate = signer && signer->csCertChain ? signer->pasCertChain[0].pCert : nullptr;
            if (certificate)
            {
                wchar_t publisher[256]{};
                const DWORD characters = CertGetNameStringW(certificate, CERT_NAME_SIMPLE_DISPLAY_TYPE, 0,
                    nullptr, publisher, ARRAYSIZE(publisher));
                accepted = characters > 1 && characters <= ARRAYSIZE(publisher) && IsExpectedPublisher(publisher);
            }
        }

        trust.dwStateAction = WTD_STATEACTION_CLOSE;
        WinVerifyTrust(nullptr, &action, &trust);
        return accepted;
    }
}
