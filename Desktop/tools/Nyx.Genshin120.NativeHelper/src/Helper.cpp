#include <Windows.h>
#include <bcrypt.h>
#include <knownfolders.h>
#include <shlobj.h>
#include <cstdint>
#include <cstring>
#include <climits>
#include <cwchar>
#include <string>
#include <vector>
#include <algorithm>

#include "Protocol.h"
#include "Authenticode.h"
#include "PayloadHash.generated.h"

#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")

namespace
{
    constexpr wchar_t PipePrefix[] = L"\\\\.\\pipe\\Pengo.Nyx.Genshin120.";
    constexpr wchar_t MutexName[] = L"Local\\Pengo.Nyx.Genshin120.Helper.v1";
    constexpr wchar_t MappingName[] = L"Local\\Pengo.Nyx.Genshin120.Internal.v1";
    constexpr DWORD PipeWaitMilliseconds = 10000;
    constexpr DWORD PipeIoMilliseconds = 5000;
    constexpr DWORD WindowWaitMilliseconds = 30000;
    constexpr DWORD ReadyWaitMilliseconds = 20000;
    constexpr int PayloadResourceId = 101;

    enum class IpcStatus : LONG { None = 0, Error = 1, Ready = 2 };
    struct IpcData { volatile LONG Status; LONG Framerate; };

    class Handle
    {
    public:
        Handle() noexcept = default;
        explicit Handle(HANDLE value) noexcept : value_(value) {}
        ~Handle() { Reset(); }
        Handle(const Handle&) = delete;
        Handle& operator=(const Handle&) = delete;
        Handle(Handle&& other) noexcept : value_(other.Release()) {}
        Handle& operator=(Handle&& other) noexcept { if (this != &other) Reset(other.Release()); return *this; }
        explicit operator bool() const noexcept { return value_ && value_ != INVALID_HANDLE_VALUE; }
        HANDLE Get() const noexcept { return value_; }
        HANDLE Release() noexcept { HANDLE value = value_; value_ = nullptr; return value; }
        void Reset(HANDLE value = nullptr) noexcept { if (*this) CloseHandle(value_); value_ = value; }
    private:
        HANDLE value_ = nullptr;
    };

    struct Request
    {
        std::wstring Executable;
        std::wstring WorkingDirectory;
        std::vector<std::wstring> Arguments;
        std::uint8_t ExpectedExecutableSha256[32]{};
    };

    bool Transfer(HANDLE pipe, void* buffer, DWORD bytes, bool write) noexcept
    {
        auto* next = static_cast<std::uint8_t*>(buffer);
        const ULONGLONG deadline = GetTickCount64() + PipeIoMilliseconds;
        while (bytes)
        {
            Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
            if (!event) return false;
            OVERLAPPED operation{};
            operation.hEvent = event.Get();
            DWORD transferred = 0;
            BOOL started = write
                ? WriteFile(pipe, next, bytes, &transferred, &operation)
                : ReadFile(pipe, next, bytes, &transferred, &operation);
            if (!started)
            {
                const DWORD error = GetLastError();
                if (error != ERROR_IO_PENDING && error != ERROR_MORE_DATA) return false;
                if (error == ERROR_IO_PENDING)
                {
                    const ULONGLONG now = GetTickCount64();
                    const DWORD remaining = now < deadline ? static_cast<DWORD>(deadline - now) : 0;
                    if (WaitForSingleObject(event.Get(), remaining) != WAIT_OBJECT_0)
                    {
                        CancelIoEx(pipe, &operation);
                        WaitForSingleObject(event.Get(), INFINITE);
                        return false;
                    }
                    if (!GetOverlappedResult(pipe, &operation, &transferred, FALSE)) return false;
                }
            }
            if (!transferred) return false;
            next += transferred;
            bytes -= transferred;
        }
        return true;
    }

    bool ReadExact(HANDLE pipe, void* buffer, DWORD bytes) noexcept
    {
        return Transfer(pipe, buffer, bytes, false);
    }

    bool WriteExact(HANDLE pipe, const void* buffer, DWORD bytes) noexcept
    {
        return Transfer(pipe, const_cast<void*>(buffer), bytes, true);
    }

    bool ParseRequest(HANDLE pipe, Request& request) noexcept
    {
        nyx120::RequestHeader header{};
        if (!ReadExact(pipe, &header, sizeof(header)) ||
            header.Magic != nyx120::RequestMagic ||
            header.Version != nyx120::ProtocolVersion ||
            header.Reserved != 0 ||
            header.PayloadBytes < 44 ||
            header.PayloadBytes > nyx120::MaximumRequestBytes ||
            (header.PayloadBytes & 1) != 0) return false;

        std::vector<std::uint8_t> payload(header.PayloadBytes);
        if (!ReadExact(pipe, payload.data(), header.PayloadBytes)) return false;
        std::size_t offset = 0;
        auto read32 = [&](std::uint32_t& value) noexcept
        {
            if (offset + 4 > payload.size()) return false;
            std::memcpy(&value, payload.data() + offset, 4);
            offset += 4;
            return true;
        };
        auto readString = [&](std::uint32_t chars, std::uint32_t limit, std::wstring& value) noexcept
        {
            if (chars == 0 || chars > limit || chars > (payload.size() - offset) / sizeof(wchar_t)) return false;
            const auto* begin = reinterpret_cast<const wchar_t*>(payload.data() + offset);
            if (std::find(begin, begin + chars, L'\0') != begin + chars) return false;
            value.assign(begin, begin + chars);
            offset += chars * sizeof(wchar_t);
            return true;
        };

        std::uint32_t executableChars = 0, directoryChars = 0, argumentCount = 0;
        if (!read32(executableChars) || !read32(directoryChars) || !read32(argumentCount) ||
            argumentCount > nyx120::MaximumArguments ||
            offset + sizeof(request.ExpectedExecutableSha256) > payload.size()) return false;
        std::memcpy(request.ExpectedExecutableSha256, payload.data() + offset, sizeof(request.ExpectedExecutableSha256));
        offset += sizeof(request.ExpectedExecutableSha256);
        if (
            !readString(executableChars, nyx120::MaximumPathChars, request.Executable) ||
            !readString(directoryChars, nyx120::MaximumPathChars, request.WorkingDirectory)) return false;

        request.Arguments.reserve(argumentCount);
        for (std::uint32_t i = 0; i < argumentCount; ++i)
        {
            std::uint32_t chars = 0;
            std::wstring argument;
            if (!read32(chars) || chars > nyx120::MaximumArgumentChars ||
                chars > (payload.size() - offset) / sizeof(wchar_t)) return false;
            if (chars)
            {
                const auto* begin = reinterpret_cast<const wchar_t*>(payload.data() + offset);
                if (std::find(begin, begin + chars, L'\0') != begin + chars) return false;
                argument.assign(begin, begin + chars);
                offset += chars * sizeof(wchar_t);
            }
            request.Arguments.push_back(std::move(argument));
        }
        return offset == payload.size();
    }

    bool IsDriveAbsolute(const std::wstring& path) noexcept
    {
        return path.size() >= 3 && ((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z')) &&
            path[1] == L':' && path[2] == L'\\';
    }

    bool IsReparse(HANDLE handle) noexcept
    {
        FILE_ATTRIBUTE_TAG_INFO tag{};
        return !GetFileInformationByHandleEx(handle, FileAttributeTagInfo, &tag, sizeof(tag)) ||
            (tag.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
    }

    bool PinDirectoryChain(const std::wstring& path, bool create, std::vector<Handle>& handles) noexcept
    {
        if (!IsDriveAbsolute(path)) return false;
        std::wstring current = path.substr(0, 3);
        std::size_t at = 3;
        for (;;)
        {
            const std::size_t slash = path.find(L'\\', at);
            const std::wstring part = path.substr(at, slash == std::wstring::npos ? path.size() - at : slash - at);
            if (!part.empty())
            {
                if (current.back() != L'\\') current.push_back(L'\\');
                current += part;
                if (create && !CreateDirectoryW(current.c_str(), nullptr) && GetLastError() != ERROR_ALREADY_EXISTS) return false;
                Handle directory(CreateFileW(current.c_str(), FILE_READ_ATTRIBUTES,
                    FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
                    FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
                if (!directory || IsReparse(directory.Get())) return false;
                FILE_STANDARD_INFO standard{};
                if (!GetFileInformationByHandleEx(directory.Get(), FileStandardInfo, &standard, sizeof(standard)) || !standard.Directory) return false;
                handles.push_back(std::move(directory));
            }
            if (slash == std::wstring::npos) return true;
            at = slash + 1;
        }
    }

    std::wstring FinalPath(HANDLE file) noexcept
    {
        std::wstring path(32768, L'\0');
        const DWORD length = GetFinalPathNameByHandleW(file, path.data(), static_cast<DWORD>(path.size()), FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
        if (!length || length >= path.size()) return {};
        path.resize(length);
        if (path.rfind(L"\\\\?\\", 0) == 0) path.erase(0, 4);
        return path;
    }

    bool SameFile(HANDLE left, HANDLE right) noexcept
    {
        BY_HANDLE_FILE_INFORMATION a{}, b{};
        return GetFileInformationByHandle(left, &a) && GetFileInformationByHandle(right, &b) &&
            a.dwVolumeSerialNumber == b.dwVolumeSerialNumber &&
            a.nFileIndexHigh == b.nFileIndexHigh && a.nFileIndexLow == b.nFileIndexLow;
    }

    bool HashFile(HANDLE file, std::uint8_t output[32]) noexcept;

    bool ValidateLaunch(const Request& request, Handle& executable, std::vector<Handle>& roots) noexcept
    {
        if (!IsDriveAbsolute(request.WorkingDirectory) || !IsDriveAbsolute(request.Executable) ||
            request.WorkingDirectory.back() == L'\\') return false;
        wchar_t full[32768]{};
        if (!GetFullPathNameW(request.WorkingDirectory.c_str(), ARRAYSIZE(full), full, nullptr) ||
            _wcsicmp(full, request.WorkingDirectory.c_str()) != 0) return false;
        const std::wstring expected = request.WorkingDirectory + L"\\GenshinImpact.exe";
        const wchar_t* executableName = std::wcsrchr(request.Executable.c_str(), L'\\');
        if (!executableName || !nyx120::IsExpectedExecutableName(executableName + 1) ||
            _wcsicmp(expected.c_str(), request.Executable.c_str()) != 0) return false;
        if (!PinDirectoryChain(request.WorkingDirectory, false, roots)) return false;

        executable.Reset(CreateFileW(request.Executable.c_str(), GENERIC_READ | FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN, nullptr));
        if (!executable || IsReparse(executable.Get())) return false;
        FILE_STANDARD_INFO standard{};
        if (!GetFileInformationByHandleEx(executable.Get(), FileStandardInfo, &standard, sizeof(standard)) || standard.Directory) return false;
        const std::wstring final = FinalPath(executable.Get());
        std::uint8_t actualHash[32]{};
        return !final.empty() && _wcsicmp(final.c_str(), expected.c_str()) == 0 &&
            HashFile(executable.Get(), actualHash) &&
            std::memcmp(actualHash, request.ExpectedExecutableSha256, sizeof(actualHash)) == 0 &&
            nyx120::HasExpectedCachedAuthenticodePublisher(executable.Get(), final.c_str());
    }

    std::wstring Quote(const std::wstring& value)
    {
        if (!value.empty() && value.find_first_of(L" \t\"") == std::wstring::npos) return value;
        std::wstring result = L"\"";
        std::size_t slashes = 0;
        for (wchar_t character : value)
        {
            if (character == L'\\') { ++slashes; continue; }
            if (character == L'\"')
            {
                result.append(slashes * 2 + 1, L'\\');
                result.push_back(L'\"');
            }
            else
            {
                result.append(slashes, L'\\');
                result.push_back(character);
            }
            slashes = 0;
        }
        result.append(slashes * 2, L'\\');
        result.push_back(L'\"');
        return result;
    }

    std::wstring BuildCommandLine(const Request& request)
    {
        std::wstring command = Quote(request.Executable);
        for (const auto& argument : request.Arguments)
        {
            command.push_back(L' ');
            command += Quote(argument);
        }
        return command;
    }

    bool Sha256(const void* data, std::size_t size, std::uint8_t output[32]) noexcept
    {
        BCRYPT_ALG_HANDLE algorithm = nullptr;
        BCRYPT_HASH_HANDLE hash = nullptr;
        DWORD objectBytes = 0, resultBytes = 0;
        std::vector<std::uint8_t> object;
        bool ok = BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0 &&
            BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&objectBytes), sizeof(objectBytes), &resultBytes, 0) >= 0;
        if (ok)
        {
            object.resize(objectBytes);
            ok = BCryptCreateHash(algorithm, &hash, object.data(), objectBytes, nullptr, 0, 0) >= 0 &&
                size <= ULONG_MAX && BCryptHashData(hash, static_cast<PUCHAR>(const_cast<void*>(data)), static_cast<ULONG>(size), 0) >= 0 &&
                BCryptFinishHash(hash, output, 32, 0) >= 0;
        }
        if (hash) BCryptDestroyHash(hash);
        if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
        return ok;
    }

    bool HashFile(HANDLE file, std::uint8_t output[32]) noexcept
    {
        LARGE_INTEGER zero{};
        if (!SetFilePointerEx(file, zero, nullptr, FILE_BEGIN)) return false;
        BCRYPT_ALG_HANDLE algorithm = nullptr;
        BCRYPT_HASH_HANDLE hash = nullptr;
        DWORD objectBytes = 0, resultBytes = 0;
        std::vector<std::uint8_t> object, buffer(64 * 1024);
        bool ok = BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0 &&
            BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&objectBytes), sizeof(objectBytes), &resultBytes, 0) >= 0;
        if (ok)
        {
            object.resize(objectBytes);
            ok = BCryptCreateHash(algorithm, &hash, object.data(), objectBytes, nullptr, 0, 0) >= 0;
            while (ok)
            {
                DWORD read = 0;
                if (!ReadFile(file, buffer.data(), static_cast<DWORD>(buffer.size()), &read, nullptr)) { ok = false; break; }
                if (!read) break;
                ok = BCryptHashData(hash, buffer.data(), read, 0) >= 0;
            }
            if (ok) ok = BCryptFinishHash(hash, output, 32, 0) >= 0;
        }
        if (hash) BCryptDestroyHash(hash);
        if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
        SetFilePointerEx(file, zero, nullptr, FILE_BEGIN);
        return ok;
    }

    bool ExtractPayload(std::wstring& path, Handle& pinnedFile, std::vector<Handle>& pinnedDirectories) noexcept
    {
        HRSRC resource = FindResourceW(nullptr, MAKEINTRESOURCEW(PayloadResourceId), RT_RCDATA);
        if (!resource) return false;
        const DWORD size = SizeofResource(nullptr, resource);
        HGLOBAL loaded = LoadResource(nullptr, resource);
        const void* bytes = loaded ? LockResource(loaded) : nullptr;
        std::uint8_t hash[32]{};
        if (!bytes || size == 0 || !Sha256(bytes, size, hash) ||
            std::memcmp(hash, nyx120::PayloadSha256, sizeof(hash)) != 0) return false;

        PWSTR local = nullptr;
        if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_CREATE, nullptr, &local))) return false;
        std::wstring directory(local);
        CoTaskMemFree(local);
        directory += L"\\Pengo\\Nyx\\runtime\\genshin120\\";
        directory += nyx120::PayloadSha256Hex;
        if (!PinDirectoryChain(directory, true, pinnedDirectories)) return false;
        path = directory + L"\\Nyx.Genshin120.Stub.dll";

        auto openPinned = [&]() noexcept
        {
            pinnedFile.Reset(CreateFileW(path.c_str(), GENERIC_READ | FILE_READ_ATTRIBUTES,
                FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN, nullptr));
            if (!pinnedFile || IsReparse(pinnedFile.Get())) return false;
            std::uint8_t existing[32]{};
            return HashFile(pinnedFile.Get(), existing) && std::memcmp(existing, hash, sizeof(hash)) == 0;
        };
        if (openPinned()) return true;
        pinnedFile.Reset();
        if (!DeleteFileW(path.c_str()) && GetLastError() != ERROR_FILE_NOT_FOUND) return false;
        Handle output(CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ, nullptr,
            CREATE_NEW, FILE_ATTRIBUTE_HIDDEN | FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_WRITE_THROUGH, nullptr));
        if (!output || IsReparse(output.Get())) return false;
        DWORD written = 0;
        if (!WriteFile(output.Get(), bytes, size, &written, nullptr) || written != size || !FlushFileBuffers(output.Get())) return false;
        std::uint8_t writtenHash[32]{};
        if (!HashFile(output.Get(), writtenHash) || std::memcmp(writtenHash, hash, sizeof(hash)) != 0) return false;
        pinnedFile = std::move(output);
        return true;
    }

    struct WindowSearch { DWORD ProcessId; HWND Window; };

    bool IsWindowDrawing(HWND window) noexcept
    {
        if (!IsWindowVisible(window)) return false;
        RedrawWindow(window, nullptr, nullptr, RDW_INTERNALPAINT | RDW_NOERASE | RDW_UPDATENOW);
        UpdateWindow(window);
        HDC deviceContext = GetDC(window);
        if (!deviceContext) return false;
        ReleaseDC(window, deviceContext);
        return true;
    }

    BOOL CALLBACK FindGameWindow(HWND window, LPARAM parameter) noexcept
    {
        auto* search = reinterpret_cast<WindowSearch*>(parameter);
        DWORD processId = 0;
        GetWindowThreadProcessId(window, &processId);
        if (processId != search->ProcessId) return TRUE;
        wchar_t className[64]{};
        if (GetClassNameW(window, className, ARRAYSIZE(className)) &&
            std::wcscmp(className, L"UnityWndClass") == 0 &&
            IsWindowDrawing(window))
        {
            search->Window = window;
            return FALSE;
        }
        return TRUE;
    }

    HWND WaitForGameWindow(DWORD processId, HANDLE process) noexcept
    {
        const ULONGLONG deadline = GetTickCount64() + WindowWaitMilliseconds;
        while (GetTickCount64() < deadline)
        {
            WindowSearch search{ processId, nullptr };
            EnumWindows(FindGameWindow, reinterpret_cast<LPARAM>(&search));
            if (search.Window) return search.Window;
            if (WaitForSingleObject(process, 100) == WAIT_OBJECT_0) return nullptr;
        }
        return nullptr;
    }

    nyx120::Result Launch(const Request& request) noexcept
    {
        Handle executable;
        std::vector<Handle> rootHandles;
        if (!ValidateLaunch(request, executable, rootHandles)) return nyx120::Result::InvalidRequest;

        std::wstring payloadPath;
        Handle payloadFile;
        std::vector<Handle> cacheHandles;
        if (!ExtractPayload(payloadPath, payloadFile, cacheHandles)) return nyx120::Result::StartFailure;

        SECURITY_ATTRIBUTES attributes{ sizeof(attributes), nullptr, FALSE };
        Handle mapping(CreateFileMappingW(INVALID_HANDLE_VALUE, &attributes, PAGE_READWRITE, 0, 4096, MappingName));
        if (!mapping || GetLastError() == ERROR_ALREADY_EXISTS) return nyx120::Result::StartFailure;
        auto* ipc = static_cast<IpcData*>(MapViewOfFile(mapping.Get(), FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(IpcData)));
        if (!ipc) return nyx120::Result::StartFailure;
        ipc->Status = static_cast<LONG>(IpcStatus::None);
        ipc->Framerate = 120;

        std::wstring command = BuildCommandLine(request);
        STARTUPINFOW startup{ sizeof(startup) };
        PROCESS_INFORMATION info{};
        if (!CreateProcessW(request.Executable.c_str(), command.data(), nullptr, nullptr, FALSE,
            CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT, nullptr, request.WorkingDirectory.c_str(), &startup, &info))
        {
            UnmapViewOfFile(ipc);
            return nyx120::Result::StartFailure;
        }
        Handle process(info.hProcess), thread(info.hThread);

        wchar_t launchedPath[32768]{};
        DWORD launchedChars = ARRAYSIZE(launchedPath);
        Handle launchedFile;
        bool imageMatches = QueryFullProcessImageNameW(process.Get(), 0, launchedPath, &launchedChars) &&
            _wcsicmp(launchedPath, request.Executable.c_str()) == 0;
        if (imageMatches)
        {
            launchedFile.Reset(CreateFileW(launchedPath, GENERIC_READ | FILE_READ_ATTRIBUTES, FILE_SHARE_READ,
                nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
            imageMatches = launchedFile && !IsReparse(launchedFile.Get()) && SameFile(executable.Get(), launchedFile.Get());
        }
        if (!imageMatches || ResumeThread(thread.Get()) == static_cast<DWORD>(-1))
        {
            TerminateProcess(process.Get(), 1);
            UnmapViewOfFile(ipc);
            return nyx120::Result::StartFailure;
        }
        executable.Reset();
        rootHandles.clear();

        HWND window = WaitForGameWindow(info.dwProcessId, process.Get());
        if (!window)
        {
            UnmapViewOfFile(ipc);
            return WaitForSingleObject(process.Get(), 0) == WAIT_OBJECT_0 ?
                nyx120::Result::GameStartedAttachFailed : nyx120::Result::GameStartedAttachTimedOut;
        }

        HMODULE stub = LoadLibraryExW(payloadPath.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!stub)
        {
            UnmapViewOfFile(ipc);
            return nyx120::Result::GameStartedAttachFailed;
        }
        auto procedure = reinterpret_cast<HOOKPROC>(GetProcAddress(stub, "WndProc"));
        const DWORD windowThread = GetWindowThreadProcessId(window, nullptr);
        HHOOK hook = procedure && windowThread ? SetWindowsHookExW(WH_CALLWNDPROC, procedure, stub, windowThread) : nullptr;
        if (!hook || !PostThreadMessageW(windowThread, WM_NULL, 0, 0))
        {
            if (hook) UnhookWindowsHookEx(hook);
            FreeLibrary(stub);
            UnmapViewOfFile(ipc);
            return nyx120::Result::GameStartedAttachFailed;
        }

        nyx120::Result result = nyx120::Result::GameStartedAttachTimedOut;
        const ULONGLONG deadline = GetTickCount64() + ReadyWaitMilliseconds;
        while (GetTickCount64() < deadline)
        {
            const LONG status = InterlockedCompareExchange(&ipc->Status, 0, 0);
            if (status == static_cast<LONG>(IpcStatus::Ready)) { result = nyx120::Result::Ready; break; }
            if (status == static_cast<LONG>(IpcStatus::Error) || WaitForSingleObject(process.Get(), 100) == WAIT_OBJECT_0)
            { result = nyx120::Result::GameStartedAttachFailed; break; }
        }
        UnhookWindowsHookEx(hook);
        FreeLibrary(stub);
        UnmapViewOfFile(ipc);
        return result;
    }
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR commandLine, int)
{
    if (!nyx120::IsCanonicalGuid(commandLine)) return static_cast<int>(nyx120::Result::InvalidRequest);
    const std::wstring pipeName = std::wstring(PipePrefix) + commandLine;
    if (!WaitNamedPipeW(pipeName.c_str(), PipeWaitMilliseconds)) return static_cast<int>(nyx120::Result::InvalidRequest);
    Handle pipe(CreateFileW(pipeName.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr));
    if (!pipe) return static_cast<int>(nyx120::Result::InvalidRequest);

    ULONG serverProcess = 0;
    if (!GetNamedPipeServerProcessId(pipe.Get(), &serverProcess) || !serverProcess || serverProcess == GetCurrentProcessId())
        return static_cast<int>(nyx120::Result::InvalidRequest);

    Request request;
    nyx120::Result result = nyx120::Result::InvalidRequest;
    if (ParseRequest(pipe.Get(), request))
    {
        Handle mutex(CreateMutexW(nullptr, FALSE, MutexName));
        if (mutex && WaitForSingleObject(mutex.Get(), 0) == WAIT_OBJECT_0)
        {
            result = Launch(request);
            ReleaseMutex(mutex.Get());
        }
        else result = nyx120::Result::StartFailure;
    }

    const nyx120::Response response{ nyx120::ResponseMagic, nyx120::ProtocolVersion, 0, result };
    WriteExact(pipe.Get(), &response, sizeof(response));
    return static_cast<int>(result);
}
