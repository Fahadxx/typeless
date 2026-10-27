// TypelessShell.cpp
// Native Windows 11 context menu handler (IExplorerCommand) for Typeless.
// Plain C++ COM: no ATL, no WRL, no C++/WinRT.

#include <windows.h>
#include <shobjidl_core.h>
#include <shlwapi.h>
#include <strsafe.h>
#include <new>
#include <string>
#include <vector>

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "user32.lib")

// {7D3A5B1E-4C2F-4E8B-9A61-2F0C8D4E7B19}
static const CLSID CLSID_TypelessCommand =
    { 0x7d3a5b1e, 0x4c2f, 0x4e8b, { 0x9a, 0x61, 0x2f, 0x0c, 0x8d, 0x4e, 0x7b, 0x19 } };

static HMODULE g_module = nullptr;
static LONG g_objects = 0;

// Returns the directory containing this DLL, without a trailing backslash.
static std::wstring ModuleDirectory()
{
    std::vector<wchar_t> buf(MAX_PATH);
    for (;;)
    {
        DWORD len = GetModuleFileNameW(g_module, buf.data(), static_cast<DWORD>(buf.size()));
        if (len == 0)
        {
            return std::wstring();
        }
        if (len < buf.size())
        {
            break;
        }
        if (buf.size() >= 32768)
        {
            return std::wstring();
        }
        buf.resize(buf.size() * 2);
    }
    std::wstring path(buf.data());
    size_t slash = path.find_last_of(L"\\/");
    if (slash == std::wstring::npos)
    {
        return std::wstring();
    }
    return path.substr(0, slash);
}

static std::wstring ExePath()
{
    return ModuleDirectory() + L"\\Typeless.exe";
}

// Appends an argument wrapped in double quotes. Windows paths cannot contain
// quotes, so only trailing backslashes need doubling before the closing quote.
static void AppendQuoted(std::wstring& cmd, const std::wstring& arg)
{
    cmd += L'"';
    cmd += arg;
    size_t trailing = 0;
    for (size_t i = arg.size(); i > 0 && arg[i - 1] == L'\\'; --i)
    {
        ++trailing;
    }
    cmd.append(trailing, L'\\');
    cmd += L'"';
}

class TypelessCommand : public IExplorerCommand, public IObjectWithSite
{
public:
    TypelessCommand() : m_ref(1), m_site(nullptr)
    {
        InterlockedIncrement(&g_objects);
    }

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv)
        {
            return E_POINTER;
        }
        if (IsEqualIID(riid, __uuidof(IUnknown)) || IsEqualIID(riid, __uuidof(IExplorerCommand)))
        {
            *ppv = static_cast<IExplorerCommand*>(this);
        }
        else if (IsEqualIID(riid, __uuidof(IObjectWithSite)))
        {
            *ppv = static_cast<IObjectWithSite*>(this);
        }
        else
        {
            *ppv = nullptr;
            return E_NOINTERFACE;
        }
        AddRef();
        return S_OK;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override
    {
        return static_cast<ULONG>(InterlockedIncrement(&m_ref));
    }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        LONG ref = InterlockedDecrement(&m_ref);
        if (ref == 0)
        {
            delete this;
        }
        return static_cast<ULONG>(ref);
    }

    // IExplorerCommand
    IFACEMETHODIMP GetTitle(IShellItemArray* /*items*/, LPWSTR* name) override
    {
        if (!name)
        {
            return E_POINTER;
        }
        return SHStrDupW(L"Convert with Typeless", name);
    }

    IFACEMETHODIMP GetIcon(IShellItemArray* /*items*/, LPWSTR* icon) override
    {
        if (!icon)
        {
            return E_POINTER;
        }
        *icon = nullptr;
        try
        {
            std::wstring value = ExePath() + L",0";
            return SHStrDupW(value.c_str(), icon);
        }
        catch (...)
        {
            return E_FAIL;
        }
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray* /*items*/, LPWSTR* infoTip) override
    {
        if (infoTip)
        {
            *infoTip = nullptr;
        }
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetCanonicalName(GUID* guidCommandName) override
    {
        if (!guidCommandName)
        {
            return E_POINTER;
        }
        *guidCommandName = CLSID_TypelessCommand;
        return S_OK;
    }

    IFACEMETHODIMP GetState(IShellItemArray* /*items*/, BOOL /*okToBeSlow*/, EXPCMDSTATE* cmdState) override
    {
        if (!cmdState)
        {
            return E_POINTER;
        }
        *cmdState = ECS_ENABLED;
        return S_OK;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx* /*bindCtx*/) override
    {
        if (!items)
        {
            return S_OK;
        }
        try
        {
            std::wstring exe = ExePath();
            if (exe.size() <= wcslen(L"\\Typeless.exe"))
            {
                return E_FAIL;
            }

            std::wstring head;
            AppendQuoted(head, exe);
            std::wstring cmd = head;
            bool hasPaths = false;

            DWORD count = 0;
            HRESULT hr = items->GetCount(&count);
            if (FAILED(hr))
            {
                return hr;
            }

            for (DWORD i = 0; i < count; ++i)
            {
                IShellItem* item = nullptr;
                if (FAILED(items->GetItemAt(i, &item)) || !item)
                {
                    continue;
                }
                PWSTR path = nullptr;
                HRESULT hrName = item->GetDisplayName(SIGDN_FILESYSPATH, &path);
                item->Release();
                if (FAILED(hrName) || !path)
                {
                    continue;
                }
                std::wstring p(path);
                CoTaskMemFree(path);
                std::wstring arg = L" ";
                AppendQuoted(arg, p);
                // CreateProcess caps the command line at 32767 chars. A big
                // selection goes out in several launches; SingleInstance
                // forwards the later ones into the first radial.
                if (hasPaths && cmd.size() + arg.size() > kMaxCommandLine)
                {
                    hr = Launch(exe, cmd);
                    if (FAILED(hr))
                    {
                        return hr;
                    }
                    cmd = head;
                }
                if (head.size() + arg.size() > kMaxCommandLine)
                {
                    continue;
                }
                cmd += arg;
                hasPaths = true;
            }

            return hasPaths ? Launch(exe, cmd) : S_OK;
        }
        catch (...)
        {
            return E_FAIL;
        }
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
    {
        if (!flags)
        {
            return E_POINTER;
        }
        *flags = ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** enumCommands) override
    {
        if (enumCommands)
        {
            *enumCommands = nullptr;
        }
        return E_NOTIMPL;
    }

    // IObjectWithSite
    IFACEMETHODIMP SetSite(IUnknown* site) override
    {
        if (site)
        {
            site->AddRef();
        }
        if (m_site)
        {
            m_site->Release();
        }
        m_site = site;
        return S_OK;
    }

    IFACEMETHODIMP GetSite(REFIID riid, void** site) override
    {
        if (!site)
        {
            return E_POINTER;
        }
        *site = nullptr;
        if (!m_site)
        {
            return E_FAIL;
        }
        return m_site->QueryInterface(riid, site);
    }

private:
    static const size_t kMaxCommandLine = 32000;

    static HRESULT Launch(const std::wstring& exe, const std::wstring& cmd)
    {
        std::vector<wchar_t> buffer(cmd.begin(), cmd.end());
        buffer.push_back(L'\0');

        STARTUPINFOW si = {};
        si.cb = sizeof(si);
        PROCESS_INFORMATION pi = {};

        if (!CreateProcessW(exe.c_str(), buffer.data(), nullptr, nullptr, FALSE, 0,
                            nullptr, nullptr, &si, &pi))
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }

        AllowSetForegroundWindow(pi.dwProcessId);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return S_OK;
    }

    ~TypelessCommand()
    {
        if (m_site)
        {
            m_site->Release();
            m_site = nullptr;
        }
        InterlockedDecrement(&g_objects);
    }

    LONG m_ref;
    IUnknown* m_site;
};

class ClassFactory : public IClassFactory
{
public:
    ClassFactory() : m_ref(1)
    {
        InterlockedIncrement(&g_objects);
    }

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv)
        {
            return E_POINTER;
        }
        if (IsEqualIID(riid, __uuidof(IUnknown)) || IsEqualIID(riid, __uuidof(IClassFactory)))
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override
    {
        return static_cast<ULONG>(InterlockedIncrement(&m_ref));
    }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        LONG ref = InterlockedDecrement(&m_ref);
        if (ref == 0)
        {
            delete this;
        }
        return static_cast<ULONG>(ref);
    }

    // IClassFactory
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (!ppv)
        {
            return E_POINTER;
        }
        *ppv = nullptr;
        if (outer)
        {
            return CLASS_E_NOAGGREGATION;
        }
        TypelessCommand* cmd = new (std::nothrow) TypelessCommand();
        if (!cmd)
        {
            return E_OUTOFMEMORY;
        }
        HRESULT hr = cmd->QueryInterface(riid, ppv);
        cmd->Release();
        return hr;
    }

    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        if (lock)
        {
            InterlockedIncrement(&g_objects);
        }
        else
        {
            InterlockedDecrement(&g_objects);
        }
        return S_OK;
    }

private:
    ~ClassFactory()
    {
        InterlockedDecrement(&g_objects);
    }

    LONG m_ref;
};

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID /*reserved*/)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv)
{
    if (!ppv)
    {
        return E_POINTER;
    }
    *ppv = nullptr;
    if (!IsEqualCLSID(rclsid, CLSID_TypelessCommand))
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }
    ClassFactory* factory = new (std::nothrow) ClassFactory();
    if (!factory)
    {
        return E_OUTOFMEMORY;
    }
    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

STDAPI DllCanUnloadNow(void)
{
    return (InterlockedCompareExchange(&g_objects, 0, 0) == 0) ? S_OK : S_FALSE;
}
