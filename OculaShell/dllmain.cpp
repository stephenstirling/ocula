// OculaShell.dll – adds "Open in Ocula" to File Explorer's right-click menu for folders.
//
// Windows 11 only shows packaged apps in its main (compact) right-click menu through an
// IExplorerCommand COM object like this one; it also appears in the classic menu on
// Windows 10. File Explorer loads it on demand, and Invoke() simply launches
//   ocula.exe "<folder>"
// through Ocula's execution alias. If Ocula is already open, the running window picks the
// folder up (see Program.cs).
//
// Build settings (see README): Dynamic-Link Library, C++17 or later,
// Precompiled Headers = Not Using, Module Definition File = OculaShell.def.

#include <windows.h>
#include <shobjidl_core.h>
#include <shellapi.h>
#include <shlwapi.h>
#include <wrl/module.h>
#include <wrl/implements.h>
#include <string>

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "runtimeobject.lib")

using namespace Microsoft::WRL;

static HMODULE g_module = nullptr;

// Must match the Clsid used twice in Package.appxmanifest.
class __declspec(uuid("6B1F2C3A-8D4E-4F7A-9C2B-5E3D1A7F9B42")) OpenInOculaCommand final
    : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IExplorerCommand>
{
public:
    IFACEMETHODIMP GetTitle(_In_opt_ IShellItemArray*, _Outptr_result_nullonfailure_ PWSTR* name) override
    {
        return SHStrDupW(L"Open in Ocula", name);
    }

    IFACEMETHODIMP GetIcon(_In_opt_ IShellItemArray*, _Outptr_result_nullonfailure_ PWSTR* icon) override
    {
        // The menu icon is the app icon, which sits in the package's Assets folder next to this DLL.
        wchar_t path[MAX_PATH];
        if (!GetModuleFileNameW(g_module, path, MAX_PATH)) { *icon = nullptr; return HRESULT_FROM_WIN32(GetLastError()); }
        std::wstring iconPath(path);
        iconPath = iconPath.substr(0, iconPath.find_last_of(L'\\')) + L"\\Assets\\AppIcon.ico";
        return SHStrDupW(iconPath.c_str(), icon);
    }

    IFACEMETHODIMP GetToolTip(_In_opt_ IShellItemArray*, _Outptr_result_nullonfailure_ PWSTR* tip) override
    {
        *tip = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetCanonicalName(_Out_ GUID* name) override
    {
        *name = __uuidof(OpenInOculaCommand);
        return S_OK;
    }

    IFACEMETHODIMP GetState(_In_opt_ IShellItemArray* selection, _In_ BOOL, _Out_ EXPCMDSTATE* state) override
    {
        // Only offer the command for a single folder.
        DWORD count = 0;
        *state = (selection && SUCCEEDED(selection->GetCount(&count)) && count == 1) ? ECS_ENABLED : ECS_HIDDEN;
        return S_OK;
    }

    IFACEMETHODIMP Invoke(_In_opt_ IShellItemArray* selection, _In_opt_ IBindCtx*) override
    {
        if (!selection) return E_INVALIDARG;

        ComPtr<IShellItem> item;
        HRESULT hr = selection->GetItemAt(0, &item);
        if (FAILED(hr)) return hr;

        PWSTR path = nullptr;
        hr = item->GetDisplayName(SIGDN_FILESYSPATH, &path);
        if (FAILED(hr)) return hr; // not a real folder on disk (e.g. "This PC")

        std::wstring arguments = L"\"";
        arguments += path;
        // A trailing backslash (e.g. "D:\") would escape the closing quote.
        if (!arguments.empty() && arguments.back() == L'\\') arguments += L'\\';
        arguments += L"\"";
        CoTaskMemFree(path);

        // ocula.exe is the app execution alias declared in Package.appxmanifest.
        auto result = reinterpret_cast<INT_PTR>(
            ShellExecuteW(nullptr, L"open", L"ocula.exe", arguments.c_str(), nullptr, SW_SHOWNORMAL));
        return result > 32 ? S_OK : HRESULT_FROM_WIN32(GetLastError());
    }

    IFACEMETHODIMP GetFlags(_Out_ EXPCMDFLAGS* flags) override
    {
        *flags = ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(_COM_Outptr_ IEnumExplorerCommand** commands) override
    {
        *commands = nullptr;
        return E_NOTIMPL;
    }
};

CoCreatableClass(OpenInOculaCommand)

// Standard COM DLL exports, handled by WRL.

STDAPI DllGetClassObject(_In_ REFCLSID clsid, _In_ REFIID iid, _Outptr_ LPVOID FAR* instance)
{
    return Module<InProc>::GetModule().GetClassObject(clsid, iid, instance);
}

STDAPI DllCanUnloadNow()
{
    return Module<InProc>::GetModule().GetObjectCount() == 0 ? S_OK : S_FALSE;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}
