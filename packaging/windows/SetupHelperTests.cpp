// Exercise the helper's actual Windows registry and INI code without touching installation keys.
#define wmain SetupHelperMain
#include "SetupHelper.cpp"
#undef wmain

int wmain(int argc, wchar_t** argv) {
    const wchar_t* base = L"Software\\Scriblucent.SetupTests";
    HKEY parent = nullptr, key = nullptr; DWORD parentDisposition = 0, keyDisposition = 0;
    std::wstring keyName; fs::path state; bool stateCreated = false; int result = 0;
    try {
        Require(argc == 2, "Supply a test output directory");
        GUID id{}; wchar_t name[39]{};
        Require(SUCCEEDED(CoCreateGuid(&id)) && StringFromGUID2(id, name, 39), "Cannot create test identifier");
        keyName = std::wstring(base) + L"\\" + name;
        Require(RegCreateKeyExW(HKEY_CURRENT_USER, base, 0, nullptr, 0, KEY_ALL_ACCESS | KEY_WOW64_64KEY, nullptr, &parent, &parentDisposition) == ERROR_SUCCESS, "Cannot create isolated registry test parent");
        Require(RegCreateKeyExW(HKEY_CURRENT_USER, keyName.c_str(), 0, nullptr, 0, KEY_ALL_ACCESS | KEY_WOW64_64KEY, nullptr, &key, &keyDisposition) == ERROR_SUCCESS && keyDisposition == REG_CREATED_NEW_KEY, "Cannot create isolated registry test key");
        state = fs::path(argv[1]) / (std::wstring(name) + L".ini");
        Require(!fs::exists(state), "Registry test result already exists"); NewIni(state); stateCreated = true;
        const std::wstring values[] = {L"", L"1.0.0-beta.2", L"desktopicon,fileassociations", L"C:\\Users\\Example\\AppData\\Local\\Programs\\Scriblucent Žinių biblioteka"};
        for (const auto& expected : values) {
            for (int terminators = 0; terminators <= 2; ++terminators) {
                std::wstring stored = expected; stored.append(terminators, L'\0');
                Require(RegSetValueExW(key, L"Value", 0, REG_SZ, reinterpret_cast<const BYTE*>(stored.data()), static_cast<DWORD>(stored.size() * sizeof(wchar_t))) == ERROR_SUCCESS, "Cannot write registry fixture");
                auto actual = ReadString(HKEY_CURRENT_USER, keyName, L"Value");
                Require(actual == expected, "Registry string contains buffer padding or null terminators");
                UserResult(state, {{L"LegacyPath",actual}});
                Require(Ini(state,L"Result") == L"0" && Ini(state,L"LegacyPath") == expected, "Registry string did not survive a Windows INI round trip");
            }
        }
        Require(ReadString(HKEY_CURRENT_USER,keyName,L"Missing").empty(), "Missing registry value was not empty");
        std::cout << "Passed registry/INI checks: Unicode, empty values, missing values, and zero/one/two null terminators.\n";
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; result = 1; }
    if (key) RegCloseKey(key);
    if (keyDisposition == REG_CREATED_NEW_KEY) RegDeleteTreeW(HKEY_CURRENT_USER,keyName.c_str());
    if (parent) RegCloseKey(parent);
    if (parentDisposition == REG_CREATED_NEW_KEY) RegDeleteKeyW(HKEY_CURRENT_USER,base);
    if (stateCreated) fs::remove(state);
    return result;
}
