// FPS-only derivative of 34736384/genshin-fps-unlock v3.5.0.
// See PROVENANCE.md and LICENSE-THIRD-PARTY.txt.
#include <Windows.h>
#include <cstdint>
#include <cstring>

namespace
{
    constexpr wchar_t MappingName[] = L"Local\\Pengo.Nyx.Genshin120.Internal.v1";

    enum class IpcStatus : LONG { None = 0, Error = 1, Ready = 2 };
    struct IpcData { volatile LONG Status; LONG Framerate; };

    HMODULE Self = nullptr;

    bool IsGenshin() noexcept
    {
        wchar_t path[32768]{};
        const DWORD length = GetModuleFileNameW(nullptr, path, ARRAYSIZE(path));
        if (!length || length >= ARRAYSIZE(path)) return false;
        const wchar_t* name = wcsrchr(path, L'\\');
        return name && _wcsicmp(name + 1, L"GenshinImpact.exe") == 0;
    }

    std::int32_t* FindFramerate() noexcept
    {
        auto* image = reinterpret_cast<std::uint8_t*>(GetModuleHandleW(nullptr));
        if (!image) return nullptr;
        const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(image);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0) return nullptr;
        const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(image + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC) return nullptr;
        const std::size_t imageSize = nt->OptionalHeader.SizeOfImage;
        auto inside = [&](const std::uint8_t* address, std::size_t bytes) noexcept
        {
            return address >= image && bytes <= imageSize &&
                static_cast<std::size_t>(address - image) <= imageSize - bytes;
        };

        const auto* sections = IMAGE_FIRST_SECTION(nt);
        std::uint8_t* start = nullptr;
        std::size_t size = 0;
        for (WORD i = 0; i < nt->FileHeader.NumberOfSections; ++i)
        {
            if (std::memcmp(sections[i].Name, "il2cpp", 6) == 0)
            {
                start = image + sections[i].VirtualAddress;
                size = sections[i].Misc.VirtualSize;
                if (!inside(start, size)) return nullptr;
                break;
            }
        }
        if (!start || size < 16) return nullptr;

        constexpr std::uint8_t pattern[] = { 0xB9, 0x3C, 0x00, 0x00, 0x00, 0xE8 };
        for (std::size_t i = 0; i + 16 <= size; ++i)
        {
            if (std::memcmp(start + i, pattern, sizeof(pattern)) != 0) continue;
            auto* rip = start + i + 5;
            const auto firstDisplacement = *reinterpret_cast<const std::int32_t*>(rip + 1);
            auto* destination = rip + firstDisplacement + 5;
            if (!inside(destination, 1) || *destination != 0xE9) continue;
            unsigned hops = 0;
            while (inside(rip, 5) && (*rip == 0xE8 || *rip == 0xE9) && hops++ < 16)
            {
                const auto displacement = *reinterpret_cast<const std::int32_t*>(rip + 1);
                rip += displacement + 5;
            }
            if (!inside(rip, 6) || hops >= 16) continue;
            const auto displacement = *reinterpret_cast<const std::int32_t*>(rip + 2);
            auto* value = reinterpret_cast<std::int32_t*>(rip + displacement + 6);
            if (!inside(reinterpret_cast<std::uint8_t*>(value), sizeof(*value))) continue;
            MEMORY_BASIC_INFORMATION memory{};
            if (!VirtualQuery(value, &memory, sizeof(memory))) return nullptr;
            const DWORD writable = PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
            if ((memory.Protect & writable) == 0 || (memory.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0) return nullptr;
            return value;
        }
        return nullptr;
    }

    DWORD WINAPI Run(void*) noexcept
    {
        if (!IsGenshin()) return 0;
        HMODULE pinned = nullptr;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
            reinterpret_cast<LPCWSTR>(&Run), &pinned)) return 0;

        HANDLE mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, MappingName);
        if (!mapping) return 0;
        auto* ipc = static_cast<IpcData*>(MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(IpcData)));
        CloseHandle(mapping);
        if (!ipc) return 0;

        auto* framerate = FindFramerate();
        if (!framerate || ipc->Framerate != 120)
        {
            InterlockedExchange(&ipc->Status, static_cast<LONG>(IpcStatus::Error));
            UnmapViewOfFile(ipc);
            return 0;
        }

        *framerate = 120;
        InterlockedExchange(&ipc->Status, static_cast<LONG>(IpcStatus::Ready));
        for (;;)
        {
            *framerate = 120;
            Sleep(62);
        }
    }
}

extern "C" __declspec(dllexport) LRESULT CALLBACK WndProc(int code, WPARAM wParam, LPARAM lParam)
{
    return CallNextHookEx(nullptr, code, wParam, lParam);
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, void*)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        Self = instance;
        DisableThreadLibraryCalls(instance);
        if (HANDLE thread = CreateThread(nullptr, 0, Run, nullptr, 0, nullptr)) CloseHandle(thread);
    }
    return TRUE;
}
