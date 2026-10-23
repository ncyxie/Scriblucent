#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#define NOMINMAX
#include <windows.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <tlhelp32.h>
#include <sddl.h>
#include <userenv.h>
#include <wincrypt.h>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <sstream>
#include <string>
#include <vector>
#include <stdexcept>
#include <set>
#include <system_error>

#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "userenv.lib")
#pragma comment(lib, "uuid.lib")

namespace fs = std::filesystem;
static const wchar_t* UninstallKey = L"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{2768A225-1F0D-43A6-9AF9-C6A8F8CAAA72}_is1";
static const wchar_t* Extensions[] = {L".scribnote", L".scribflash", L".scribmap", L".scribquiz"};
static const wchar_t* ProgIds[] = {L"Scriblucent.Note", L"Scriblucent.Flashcards", L"Scriblucent.MindMap", L"Scriblucent.Quiz"};

static void Require(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); }
static std::wstring Known(REFKNOWNFOLDERID id) {
    PWSTR value = nullptr;
    Require(SUCCEEDED(SHGetKnownFolderPath(id, 0, nullptr, &value)), "Cannot locate a Windows folder");
    std::wstring result(value); CoTaskMemFree(value); return result;
}
static fs::path Module() {
    std::wstring result(32768, L'\0');
    DWORD size = GetModuleFileNameW(nullptr, result.data(), static_cast<DWORD>(result.size()));
    Require(size && size < result.size(), "Cannot locate setup helper"); result.resize(size); return result;
}
static std::wstring Canon(const fs::path& path) {
    std::wstring result(32768, L'\0');
    DWORD size = GetFullPathNameW(path.c_str(), static_cast<DWORD>(result.size()), result.data(), nullptr);
    Require(size && size < result.size(), "Invalid installation path"); result.resize(size);
    std::wstring longPath(32768, L'\0');
    size = GetLongPathNameW(result.c_str(), longPath.data(), static_cast<DWORD>(longPath.size()));
    if (size && size < longPath.size()) { longPath.resize(size); result = longPath; }
    while (result.size() > 3 && result.back() == L'\\') result.pop_back();
    return result;
}
static bool Same(const fs::path& a, const fs::path& b) { return _wcsicmp(Canon(a).c_str(), Canon(b).c_str()) == 0; }
static bool Inside(const fs::path& child, const fs::path& parent) {
    auto prefix = Canon(parent) + L"\\", path = Canon(child);
    return path.size() > prefix.size() && _wcsnicmp(path.c_str(), prefix.c_str(), prefix.size()) == 0;
}
static void NoReparse(const fs::path& path) {
    for (auto current = path; !current.empty() && current != current.parent_path(); current = current.parent_path()) {
        DWORD attributes = GetFileAttributesW(current.c_str());
        Require(attributes == INVALID_FILE_ATTRIBUTES || !(attributes & FILE_ATTRIBUTE_REPARSE_POINT), "Reparse points are not allowed in setup paths");
    }
}
static fs::path RecoveryRoot() { return fs::path(Known(FOLDERID_ProgramData)) / L"Scriblucent" / L"InstallerRecovery"; }
static void RemoveTree(const fs::path& path, const fs::path& allowed) {
    Require(Same(path, allowed) || Inside(path, allowed), "Refusing removal outside the setup-owned directory");
    NoReparse(path);
    if (fs::exists(path)) fs::remove_all(path);
}
static void SecureDirectory(const fs::path& path, bool userRead = false) {
    NoReparse(path); fs::create_directories(path);
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    const wchar_t* sddl = userRead ? L"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)" : L"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";
    Require(ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SDDL_REVISION_1, &descriptor, nullptr) != FALSE, "Cannot create setup security descriptor");
    BOOL ok = SetFileSecurityW(path.c_str(), DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION, descriptor);
    LocalFree(descriptor); Require(ok != FALSE, "Cannot protect setup directory");
}
static std::wstring ReadString(HKEY root, const std::wstring& key, const wchar_t* name) {
    DWORD bytes = 0;
    if (RegGetValueW(root, key.c_str(), name, RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, nullptr, nullptr, &bytes) != ERROR_SUCCESS) return L"";
    std::wstring result(bytes / sizeof(wchar_t), L'\0');
    Require(RegGetValueW(root, key.c_str(), name, RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, nullptr, result.data(), &bytes) == ERROR_SUCCESS, "Cannot read installation registration");
    result.resize(bytes / sizeof(wchar_t));
    auto terminator = result.find(L'\0');
    if (terminator != std::wstring::npos) result.resize(terminator);
    return result;
}
static void Ini(const fs::path& file, const std::wstring& name, const std::wstring& value) {
    Require(value.find_first_of(L"\r\n") == std::wstring::npos, "Invalid setup state value");
    Require(WritePrivateProfileStringW(L"Setup", name.c_str(), value.c_str(), file.c_str()) != FALSE, "Cannot write setup state");
}
static std::wstring Ini(const fs::path& file, const std::wstring& name) {
    std::wstring result(32768, L'\0');
    DWORD size = GetPrivateProfileStringW(L"Setup", name.c_str(), L"", result.data(), static_cast<DWORD>(result.size()), file.c_str());
    Require(size < result.size() - 1, "Setup state is too large"); result.resize(size); return result;
}
static void NewIni(const fs::path& file) { std::ofstream stream(file, std::ios::binary | std::ios::trunc); Require(stream.good(), "Cannot create setup state"); stream.put('\xff'); stream.put('\xfe'); }
static bool Elevated() {
    HANDLE token = nullptr; TOKEN_ELEVATION elevation{}; DWORD size = 0;
    Require(OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token) != FALSE, "Cannot read process token");
    BOOL ok = GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &size); CloseHandle(token);
    Require(ok != FALSE, "Cannot read process elevation");
    BYTE administrator[SECURITY_MAX_SID_SIZE]; DWORD sidSize = sizeof(administrator); BOOL member = FALSE;
    Require(CreateWellKnownSid(WinBuiltinAdministratorsSid, nullptr, administrator, &sidSize) && CheckTokenMembership(nullptr, administrator, &member), "Cannot check administrator membership");
    return elevation.TokenIsElevated != 0 && member != FALSE;
}
static std::wstring Quote(const std::wstring& value) {
    Require(value.find(L'"') == std::wstring::npos && value.find_first_of(L"\r\n") == std::wstring::npos, "Invalid setup argument");
    std::wstring result = L"\"" + value;
    for (size_t i = value.size(); i && value[i - 1] == L'\\'; --i) result += L'\\';
    return result + L'"';
}
static void Privilege(const wchar_t* name);
static DWORD Run(const fs::path& executable, const std::wstring& arguments, HANDLE token = nullptr) {
    std::wstring command = Quote(executable.wstring()) + L" " + arguments;
    STARTUPINFOW startup{}; startup.cb = sizeof(startup); startup.dwFlags = STARTF_USESHOWWINDOW; startup.wShowWindow = SW_HIDE;
    PROCESS_INFORMATION process{};
    // Inno's protected temporary working directory is not accessible to a limited token.
    auto workingDirectory = executable.parent_path();
    LPVOID environment = nullptr;
    if (token) Require(CreateEnvironmentBlock(&environment, token, FALSE) != FALSE, "Cannot load the original user's environment");
    BOOL ok = FALSE;
    if (token) {
        // The original interactive account's profile is already loaded. Loading it again can require additional service privileges.
        try { Privilege(SE_IMPERSONATE_NAME); } catch (...) { }
        ok = CreateProcessWithTokenW(token, 0, executable.c_str(), command.data(), CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT, environment, workingDirectory.c_str(), &startup, &process);
        if (!ok) {
            try { Privilege(SE_INCREASE_QUOTA_NAME); } catch (...) { }
            command = Quote(executable.wstring()) + L" " + arguments;
            ok = CreateProcessAsUserW(token, executable.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT, environment, workingDirectory.c_str(), &startup, &process);
        }
    } else ok = CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, workingDirectory.c_str(), &startup, &process);
    DWORD error = GetLastError(); if (environment) DestroyEnvironmentBlock(environment); SetLastError(error);
    if (!ok) throw std::system_error(static_cast<int>(error), std::system_category(), "Cannot start setup operation");
    CloseHandle(process.hThread);
    DWORD wait = WaitForSingleObject(process.hProcess, 120000), code = 1;
    if (wait == WAIT_OBJECT_0) GetExitCodeProcess(process.hProcess, &code);
    CloseHandle(process.hProcess); Require(wait == WAIT_OBJECT_0, "Setup operation did not finish within two minutes"); return code;
}
static int AsUser(int argc, wchar_t** argv) {
    if (!Elevated()) return -1;
    HANDLE token = nullptr; TOKEN_LINKED_TOKEN linked{}; DWORD size = 0;
    Require(OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT, &token) != FALSE, "Cannot read process token");
    BOOL ok = GetTokenInformation(token, TokenLinkedToken, &linked, sizeof(linked), &size);
    if (!ok) {
        // Service-hosted deployment can have no UAC linked token. Drop administrator access explicitly.
        BYTE administrator[SECURITY_MAX_SID_SIZE]; DWORD sidSize = sizeof(administrator);
        SID_AND_ATTRIBUTES disabled{};
        ok = CreateWellKnownSid(WinBuiltinAdministratorsSid, nullptr, administrator, &sidSize);
        disabled.Sid = administrator;
        if (ok) ok = CreateRestrictedToken(token, DISABLE_MAX_PRIVILEGE, 1, &disabled, 0, nullptr, 0, nullptr, &linked.LinkedToken);
        if (ok) {
            PSID medium = nullptr; ok = ConvertStringSidToSidW(L"S-1-16-8192", &medium);
            if (ok) { TOKEN_MANDATORY_LABEL label{}; label.Label.Sid = medium; label.Label.Attributes = SE_GROUP_INTEGRITY;
                ok = SetTokenInformation(linked.LinkedToken, TokenIntegrityLevel, &label, sizeof(label) + GetLengthSid(medium)); LocalFree(medium); }
        }
    }
    CloseHandle(token);
    if (!ok || (argc > 3 && std::wstring(argv[3]) == L"--linked")) { if (linked.LinkedToken) CloseHandle(linked.LinkedToken); return 10; }
    std::wstring arguments = Quote(argv[1]) + L" " + Quote(argv[2]) + L" --linked";
    DWORD result = Run(Module(), arguments, linked.LinkedToken); CloseHandle(linked.LinkedToken); return static_cast<int>(result);
}
static bool Running(const fs::path& root) {
    if (root.empty()) return false;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    Require(snapshot != INVALID_HANDLE_VALUE, "Cannot inspect running applications");
    PROCESSENTRY32W entry{}; entry.dwSize = sizeof(entry); bool found = false;
    for (BOOL more = Process32FirstW(snapshot, &entry); more; more = Process32NextW(snapshot, &entry)) {
        if (_wcsicmp(entry.szExeFile, L"Scriblucent.exe") && _wcsicmp(entry.szExeFile, L"Scriblucent.AI.Worker.exe")) continue;
        HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ProcessID);
        if (!process) { found = true; break; } // A process we cannot inspect must not be overwritten.
        std::wstring image(32768, L'\0'); DWORD size = static_cast<DWORD>(image.size());
        BOOL ok = QueryFullProcessImageNameW(process, 0, image.data(), &size); CloseHandle(process);
        if (!ok) { found = true; break; }
        image.resize(size); if (Inside(image, root)) { found = true; break; }
    }
    CloseHandle(snapshot); return found;
}
static std::wstring LinkTarget(const fs::path& path) {
    IShellLinkW* link = nullptr; IPersistFile* file = nullptr;
    Require(SUCCEEDED(CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&link))), "Cannot read Windows shortcut");
    HRESULT result = link->QueryInterface(IID_PPV_ARGS(&file));
    if (SUCCEEDED(result)) result = file->Load(path.c_str(), STGM_READ);
    std::wstring target(32768, L'\0'); if (SUCCEEDED(result)) result = link->GetPath(target.data(), static_cast<int>(target.size()), nullptr, SLGP_RAWPATH);
    if (file) file->Release(); link->Release(); Require(SUCCEEDED(result), "Cannot read Windows shortcut target");
    target.resize(wcslen(target.c_str())); return target;
}
static void UserResult(const fs::path& state, const std::vector<std::pair<std::wstring,std::wstring>>& values, int result = 0) {
    std::wstring text = L"\xFEFF[Setup]\r\nResult=" + std::to_wstring(result) + L"\r\n";
    for (const auto& value : values) {
        Require(value.second.find(L'\0') == std::wstring::npos && value.second.find_first_of(L"\r\n") == std::wstring::npos, "Invalid user setup state");
        text += value.first + L"=" + value.second + L"\r\n";
    }
    HANDLE file = CreateFileW(state.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    Require(file != INVALID_HANDLE_VALUE, "Cannot open user setup result"); DWORD written = 0, bytes = static_cast<DWORD>(text.size() * sizeof(wchar_t));
    BOOL ok = WriteFile(file, text.data(), bytes, &written, nullptr);
    if (ok && written == bytes) ok = SetEndOfFile(file) && FlushFileBuffers(file); else ok = FALSE;
    CloseHandle(file); Require(ok != FALSE, "Cannot write user setup result");
}
static void ProbeUser(const fs::path& state) {
    auto root = ReadString(HKEY_CURRENT_USER, UninstallKey, L"InstallLocation");
    UserResult(state, {{L"Context",L"Unelevated"},{L"LegacyPath",root},{L"LegacyVersion",ReadString(HKEY_CURRENT_USER, UninstallKey, L"DisplayVersion")},
        {L"Tasks",ReadString(HKEY_CURRENT_USER, UninstallKey, L"Inno Setup: Selected Tasks")}, {L"Running",!root.empty() && Running(root) ? L"1" : L"0"}});
}
static void RemoveUser(const fs::path& state) {
    auto oldRoot = ReadString(HKEY_CURRENT_USER, UninstallKey, L"InstallLocation");
    if (oldRoot.empty()) { UserResult(state, {}); return; }
    auto machineRoot = ReadString(HKEY_LOCAL_MACHINE, UninstallKey, L"InstallLocation");
    Require(!machineRoot.empty() && fs::exists(fs::path(machineRoot) / L"Scriblucent.exe"), "The machine installation is not ready");
    Require(!Same(oldRoot, machineRoot) && !Inside(oldRoot, machineRoot) && !Inside(machineRoot, oldRoot), "Legacy and machine installation directories overlap");
    Require(!Running(oldRoot) && !Running(machineRoot), "Close Scriblucent before completing conversion");
    auto command = ReadString(HKEY_CURRENT_USER, UninstallKey, L"UninstallString");
    Require(command.size() > 2 && command.front() == L'"' && command.back() == L'"', "Unrecognized legacy uninstaller registration");
    fs::path uninstaller(command.substr(1, command.size() - 2));
    auto name = uninstaller.filename().wstring();
    bool innoName = name.size() == 12 && _wcsnicmp(name.c_str(), L"unins", 5) == 0 && _wcsicmp(name.c_str() + 8, L".exe") == 0;
    for (size_t i = 5; innoName && i < 8; ++i) innoName = name[i] >= L'0' && name[i] <= L'9';
    Require(Same(uninstaller.parent_path(), oldRoot) && innoName && fs::is_regular_file(uninstaller), "Invalid legacy uninstaller path");
    Require(Run(uninstaller, L"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART") == 0, "Legacy uninstall failed; conversion can be retried");
    Require(ReadString(HKEY_CURRENT_USER, UninstallKey, L"InstallLocation").empty(), "Legacy registration remains; conversion can be retried");
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr); UserResult(state, {});
}
static void LaunchUser(const fs::path& state) {
    auto root = ReadString(HKEY_LOCAL_MACHINE, UninstallKey, L"InstallLocation");
    Require(!root.empty(), "The machine installation is not registered");
    fs::path executable = fs::path(root) / L"Scriblucent.exe";
    std::wstring command = Quote(executable.wstring()); STARTUPINFOW startup{}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    Require(CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, root.c_str(), &startup, &process) != FALSE, "Cannot open Scriblucent");
    CloseHandle(process.hThread); CloseHandle(process.hProcess); UserResult(state, {});
}
static void Bridge(const fs::path& directory) {
    auto parent = fs::path(Known(FOLDERID_ProgramData)) / L"Scriblucent" / L"SetupBridge";
    Require(Inside(directory, parent) && Same(directory.parent_path(), parent), "Invalid setup bridge path");
    SecureDirectory(parent.parent_path(), true); SecureDirectory(parent, true);
    SecureDirectory(directory, true);
    fs::copy_file(Module(), directory / L"SetupHelper.exe", fs::copy_options::overwrite_existing);
    PSECURITY_DESCRIPTOR executableDescriptor = nullptr;
    Require(ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;BU)", SDDL_REVISION_1, &executableDescriptor, nullptr), "Cannot create helper permissions");
    BOOL executableAccess = SetFileSecurityW((directory / L"SetupHelper.exe").c_str(), DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION, executableDescriptor);
    LocalFree(executableDescriptor); Require(executableAccess != FALSE, "Cannot grant read-only access to the setup helper");
    auto state = directory / L"user.ini"; NewIni(state);
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    Require(ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGW;;;BU)", SDDL_REVISION_1, &descriptor, nullptr), "Cannot create setup result permissions");
    BOOL ok = SetFileSecurityW(state.c_str(), DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION, descriptor);
    LocalFree(descriptor); Require(ok != FALSE, "Cannot grant access to setup result");
}
static void Privilege(const wchar_t* name) {
    HANDLE token = nullptr; Require(OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &token) != FALSE, "Cannot access setup privileges");
    TOKEN_PRIVILEGES privileges{}; privileges.PrivilegeCount = 1;
    BOOL ok = LookupPrivilegeValueW(nullptr, name, &privileges.Privileges[0].Luid); privileges.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
    SetLastError(ERROR_SUCCESS); if (ok) ok = AdjustTokenPrivileges(token, FALSE, &privileges, 0, nullptr, nullptr);
    DWORD error = GetLastError(); CloseHandle(token); Require(ok && error == ERROR_SUCCESS, "Required recovery privilege is unavailable");
}
static std::vector<std::wstring> OwnedKeys() {
    std::vector<std::wstring> keys{UninstallKey};
    for (auto id : ProgIds) keys.emplace_back(std::wstring(L"Software\\Classes\\") + id); return keys;
}
static void SaveValue(const fs::path& state, int index, const std::wstring& key, const wchar_t* name) {
    HKEY handle = nullptr; DWORD type = 0, bytes = 0; std::wstring prefix = L"Value" + std::to_wstring(index);
    bool present = RegOpenKeyExW(HKEY_LOCAL_MACHINE, key.c_str(), 0, KEY_READ | KEY_WOW64_64KEY, &handle) == ERROR_SUCCESS && RegQueryValueExW(handle, name, nullptr, &type, nullptr, &bytes) == ERROR_SUCCESS;
    Ini(state, prefix + L"Present", present ? L"1" : L"0");
    if (present) {
        Require(bytes <= 8192, "Association value is too large"); std::vector<BYTE> data(bytes);
        Require(RegQueryValueExW(handle, name, nullptr, &type, data.data(), &bytes) == ERROR_SUCCESS, "Cannot back up association");
        std::wostringstream encoded; for (BYTE byte : data) encoded << std::hex << std::setw(2) << std::setfill(L'0') << static_cast<int>(byte);
        Ini(state, prefix + L"Data", encoded.str()); Ini(state, prefix + L"Type", std::to_wstring(type));
    }
    if (handle) RegCloseKey(handle);
}
static void RestoreValue(const fs::path& state, int index, const std::wstring& key, const wchar_t* name) {
    HKEY handle = nullptr; auto prefix = L"Value" + std::to_wstring(index); bool present = Ini(state, prefix + L"Present") == L"1";
    if (present) {
        Require(RegCreateKeyExW(HKEY_LOCAL_MACHINE, key.c_str(), 0, nullptr, 0, KEY_WRITE | KEY_WOW64_64KEY, nullptr, &handle, nullptr) == ERROR_SUCCESS, "Cannot restore association key");
        auto encoded = Ini(state, prefix + L"Data"); Require(encoded.size() % 2 == 0, "Invalid association backup"); std::vector<BYTE> data;
        for (size_t i = 0; i < encoded.size(); i += 2) data.push_back(static_cast<BYTE>(std::stoul(encoded.substr(i, 2), nullptr, 16)));
        Require(RegSetValueExW(handle, name, 0, std::stoul(Ini(state, prefix + L"Type")), data.data(), static_cast<DWORD>(data.size())) == ERROR_SUCCESS, "Cannot restore association value");
    } else if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, key.c_str(), 0, KEY_SET_VALUE | KEY_WOW64_64KEY, &handle) == ERROR_SUCCESS) RegDeleteValueW(handle, name);
    if (handle) RegCloseKey(handle);
}
static void CopyTree(const fs::path& source, const fs::path& destination) {
    NoReparse(source); NoReparse(destination); fs::create_directories(destination);
    for (const auto& entry : fs::recursive_directory_iterator(source)) {
        Require(!(GetFileAttributesW(entry.path().c_str()) & FILE_ATTRIBUTE_REPARSE_POINT), "Cannot back up a reparse point");
        auto target = destination / fs::relative(entry.path(), source);
        if (entry.is_directory()) fs::create_directories(target);
        else if (entry.is_regular_file()) fs::copy_file(entry.path(), target, fs::copy_options::overwrite_existing);
        else throw std::runtime_error("Unsupported file in installation directory");
    }
}
static fs::path Icon(int index) { return fs::path(Known(index ? FOLDERID_PublicDesktop : FOLDERID_CommonPrograms)) / L"Scriblucent.lnk"; }
static void ValidateTarget(const fs::path& target) {
    NoReparse(target); auto path = Canon(target);
    Require(path.size() > 3 && target.has_filename(), "Invalid installation directory");
    Require(!Same(target, Known(FOLDERID_ProgramData)) && !Inside(target, fs::path(Known(FOLDERID_ProgramData)) / L"Scriblucent"), "Installation cannot overwrite setup recovery data");
    wchar_t windows[32768]{}; GetWindowsDirectoryW(windows, 32768);
    Require(!Same(target, windows) && !Inside(target, windows), "Installation cannot overwrite Windows");
    if (fs::exists(target) && !fs::is_empty(target)) {
        auto registered = ReadString(HKEY_LOCAL_MACHINE, UninstallKey, L"InstallLocation");
        Require(!registered.empty() && Same(registered, target) && fs::exists(target / L"Scriblucent.exe"), "The destination contains files from another installation");
    }
}
static void Begin(const fs::path& target) {
    ValidateTarget(target); Require(!Running(target), "Close Scriblucent before installing");
    auto root = RecoveryRoot(); Require(!fs::exists(root), "An interrupted setup must be recovered first");
    SecureDirectory(root.parent_path(), true); SecureDirectory(root);
    auto state = root / L"state.ini"; NewIni(state); Ini(state, L"Target", Canon(target));
    bool hadApp = fs::exists(target); Ini(state, L"HadApp", hadApp ? L"1" : L"0");
    if (hadApp) {
        uintmax_t bytes = 0; for (const auto& entry : fs::recursive_directory_iterator(target)) if (entry.is_regular_file()) bytes += entry.file_size();
        Require(fs::space(root).available > bytes + 64 * 1024 * 1024, "Not enough space to back up the installed application");
        CopyTree(target, root / L"Files");
    }
    Privilege(SE_BACKUP_NAME); auto keys = OwnedKeys();
    for (size_t i = 0; i < keys.size(); ++i) {
        HKEY key = nullptr; bool present = RegOpenKeyExW(HKEY_LOCAL_MACHINE, keys[i].c_str(), 0, KEY_READ | KEY_WOW64_64KEY, &key) == ERROR_SUCCESS;
        Ini(state, L"Key" + std::to_wstring(i), present ? L"1" : L"0");
        if (present) { auto result = RegSaveKeyW(key, (root / (L"key" + std::to_wstring(i) + L".hiv")).c_str(), nullptr); RegCloseKey(key); Require(result == ERROR_SUCCESS, "Cannot back up installation registration"); }
    }
    for (int i = 0; i < 4; ++i) {
        auto key = std::wstring(L"Software\\Classes\\") + Extensions[i];
        SaveValue(state, i * 2, key, L""); SaveValue(state, i * 2 + 1, key + L"\\OpenWithProgids", ProgIds[i]);
    }
    for (int i = 0; i < 2; ++i) {
        bool exists = fs::exists(Icon(i));
        if (exists) { Require(Same(LinkTarget(Icon(i)), target / L"Scriblucent.exe"), "A shortcut with another target already exists"); fs::copy_file(Icon(i), root / (L"icon" + std::to_wstring(i) + L".lnk")); }
        Ini(state, L"Icon" + std::to_wstring(i), exists ? L"1" : L"0");
    }
    Ini(state, L"Ready", L"1");
}
static void Restore() {
    auto root = RecoveryRoot(); if (!fs::exists(root)) return; NoReparse(root);
    auto state = root / L"state.ini";
    if (Ini(state, L"Committed") == L"1" || Ini(state, L"Ready") != L"1") { RemoveTree(root, RecoveryRoot()); return; }
    fs::path target(Ini(state, L"Target")); Require(!target.empty() && Canon(target).size() > 3, "Invalid recovery target"); NoReparse(target);
    Require(!Running(target), "Close Scriblucent before recovering the interrupted installation");
    RemoveTree(target, target);
    if (Ini(state, L"HadApp") == L"1") CopyTree(root / L"Files", target);
    Privilege(SE_RESTORE_NAME); auto keys = OwnedKeys();
    for (size_t i = 0; i < keys.size(); ++i) {
        HKEY key = nullptr;
        if (Ini(state, L"Key" + std::to_wstring(i)) == L"1") {
            Require(RegCreateKeyExW(HKEY_LOCAL_MACHINE, keys[i].c_str(), 0, nullptr, 0, KEY_ALL_ACCESS | KEY_WOW64_64KEY, nullptr, &key, nullptr) == ERROR_SUCCESS, "Cannot recover installation registration");
            auto result = RegRestoreKeyW(key, (root / (L"key" + std::to_wstring(i) + L".hiv")).c_str(), REG_FORCE_RESTORE); RegCloseKey(key);
            Require(result == ERROR_SUCCESS, "Cannot restore installation registration");
        } else if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"Software", 0, KEY_ALL_ACCESS | KEY_WOW64_64KEY, &key) == ERROR_SUCCESS) {
            RegDeleteTreeW(key, keys[i].substr(9).c_str()); RegCloseKey(key);
        }
    }
    for (int i = 0; i < 4; ++i) {
        auto key = std::wstring(L"Software\\Classes\\") + Extensions[i];
        RestoreValue(state, i * 2, key, L""); RestoreValue(state, i * 2 + 1, key + L"\\OpenWithProgids", ProgIds[i]);
    }
    for (int i = 0; i < 2; ++i) {
        if (Ini(state, L"Icon" + std::to_wstring(i)) == L"1") { fs::create_directories(Icon(i).parent_path()); fs::copy_file(root / (L"icon" + std::to_wstring(i) + L".lnk"), Icon(i), fs::copy_options::overwrite_existing); }
        else if (fs::exists(Icon(i)) && Same(LinkTarget(Icon(i)), target / L"Scriblucent.exe")) fs::remove(Icon(i));
    }
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr); RemoveTree(root, RecoveryRoot());
}
static std::string Hash(const fs::path& file) {
    HCRYPTPROV provider = 0; HCRYPTHASH hash = 0;
    Require(CryptAcquireContextW(&provider, nullptr, nullptr, PROV_RSA_AES, CRYPT_VERIFYCONTEXT) != FALSE, "Cannot initialize payload verification");
    Require(CryptCreateHash(provider, CALG_SHA_256, 0, 0, &hash) != FALSE, "Cannot initialize payload hash");
    std::ifstream input(file, std::ios::binary); Require(input.good(), "Missing installed payload file"); char buffer[65536];
    while (input) { input.read(buffer, sizeof(buffer)); auto size = input.gcount(); if (size) Require(CryptHashData(hash, reinterpret_cast<BYTE*>(buffer), static_cast<DWORD>(size), 0) != FALSE, "Cannot hash installed payload"); }
    Require(input.eof(), "Cannot read installed payload"); BYTE digest[32]; DWORD size = sizeof(digest);
    Require(CryptGetHashParam(hash, HP_HASHVAL, digest, &size, 0) != FALSE, "Cannot finish payload verification"); CryptDestroyHash(hash); CryptReleaseContext(provider, 0);
    std::ostringstream encoded; for (BYTE byte : digest) encoded << std::hex << std::setw(2) << std::setfill('0') << static_cast<int>(byte); return encoded.str();
}
static void Verify(const fs::path& target, const fs::path& manifest) {
    std::ifstream input(manifest, std::ios::binary); Require(input.good(), "Missing payload manifest"); std::string line; int count = 0;
    while (std::getline(input, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back(); Require(line.size() > 65 && line[64] == '|', "Invalid payload manifest");
        auto text = line.substr(65); int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0); Require(length > 0, "Invalid manifest path");
        std::wstring relative(length, L'\0'); MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), relative.data(), length);
        fs::path path = target / relative; Require(relative.find(L':') == std::wstring::npos && Inside(path, target), "Payload path is outside the installation"); NoReparse(path);
        Require(Hash(path) == line.substr(0, 64), "Installed payload checksum mismatch"); ++count;
    }
    Require(count > 0, "Empty payload manifest");
}
static std::set<std::wstring> ManifestPaths(const fs::path& manifest) {
    std::ifstream input(manifest, std::ios::binary); Require(input.good(), "Cannot read installed file manifest"); std::string line; std::set<std::wstring> paths;
    while (std::getline(input, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back(); Require(line.size() > 65 && line[64] == '|', "Invalid installed file manifest");
        auto text = line.substr(65); int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0);
        Require(length > 0, "Invalid installed file path"); std::wstring path(length, L'\0'); MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), path.data(), length); paths.insert(path);
    }
    return paths;
}
static void Prune(const fs::path& target, const fs::path& manifest) {
    auto current = ManifestPaths(manifest); auto previous = RecoveryRoot() / L"Files" / L"InstallManifest.sha256";
    if (fs::exists(previous)) {
        for (const auto& relative : ManifestPaths(previous)) {
            fs::path path = target / relative;
            Require(relative.find(L':') == std::wstring::npos && Inside(path, target), "Invalid obsolete payload path"); NoReparse(path);
            if (!current.count(relative) && fs::is_regular_file(path)) {
                fs::remove(path);
                for (auto parent = path.parent_path(); Inside(parent, target) && fs::is_empty(parent); parent = parent.parent_path()) fs::remove(parent);
            }
        }
    }
    // Older installers had no manifest. Their known satellite folders contain only shipped resources.
    const wchar_t* languages[] = {L"cs",L"de",L"es",L"fr",L"it",L"ja",L"ko",L"pl",L"pt-BR",L"ru",L"tr",L"zh-Hans",L"zh-Hant"};
    for (auto language : languages) {
        auto directory = target / language; NoReparse(directory); if (!fs::is_directory(directory)) continue;
        for (const auto& entry : fs::directory_iterator(directory)) {
            auto name = entry.path().filename().wstring(); std::wstring suffix = L".resources.dll";
            if (entry.is_regular_file() && name.size() >= suffix.size() && name.compare(name.size() - suffix.size(), suffix.size(), suffix) == 0 && !current.count(fs::relative(entry.path(), target).wstring())) fs::remove(entry.path());
        }
        if (fs::is_empty(directory)) fs::remove(directory);
    }
}
int wmain(int argc, wchar_t** argv) {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    HRESULT com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED); int result = 0;
    try {
        Require(argc >= 2, "Missing setup operation"); std::wstring operation(argv[1]);
        if (operation == L"probe-user" || operation == L"remove-user" || operation == L"launch-user") {
            Require(argc >= 3, "Missing setup result path"); int user = AsUser(argc, argv);
            // A directly elevated fresh install has no per-user work. Never remove a legacy copy elevated.
            if (user == 10 && operation == L"probe-user" && ReadString(HKEY_CURRENT_USER, UninstallKey, L"InstallLocation").empty()) UserResult(argv[2], {});
            else if (user >= 0) result = user;
            else if (operation == L"probe-user") ProbeUser(argv[2]);
            else if (operation == L"remove-user") RemoveUser(argv[2]);
            else LaunchUser(argv[2]);
        } else if (operation == L"verify") {
            Require(argc == 4, "Missing payload manifest"); Verify(argv[2], argv[3]);
        } else {
            Require(Elevated(), "This setup operation requires administrator approval");
            if (operation == L"prepare-bridge") { Require(argc == 3, "Missing bridge directory"); Bridge(argv[2]); }
            else if (operation == L"begin") { Require(argc == 3, "Missing installation directory"); Begin(argv[2]); }
            else if (operation == L"recover" || operation == L"restore") Restore();
            else if (operation == L"commit") {
                auto state = RecoveryRoot() / L"state.ini"; Require(Ini(state, L"Ready") == L"1", "Missing installation recovery state"); Ini(state, L"Committed", L"1");
                try { RemoveTree(RecoveryRoot(), RecoveryRoot()); } catch (const std::exception& error) { std::cerr << error.what() << '\n'; }
            } else if (operation == L"prune") { Require(argc == 4, "Missing payload manifest"); Prune(argv[2], argv[3]); }
            else throw std::runtime_error("Unknown setup operation");
        }
    } catch (const std::exception& error) {
        std::cerr << error.what() << " (Windows error " << GetLastError() << ")\n"; result = 1;
        if (argc >= 3 && (std::wstring(argv[1]) == L"probe-user" || std::wstring(argv[1]) == L"remove-user" || std::wstring(argv[1]) == L"launch-user")) {
            try { std::string text(error.what()); UserResult(argv[2], {{L"Error",std::wstring(text.begin(),text.end())}}, 1); } catch (...) { }
        }
    }
    if (SUCCEEDED(com)) CoUninitialize(); return result;
}
