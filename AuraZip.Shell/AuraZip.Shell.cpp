#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shlobj.h>
#include <shellapi.h>
#include <shlwapi.h>
#include <appmodel.h>
#include <strsafe.h>
#include <wrl/client.h>
#include <atomic>
#include <string>
#include <vector>
#include <algorithm>
#include <fstream>
#include "ArchiveFormats.g.h"

using Microsoft::WRL::ComPtr;
static HMODULE module;
static std::atomic<long> objects{0};
// Keep these identifiers in sync with Package.appxmanifest.
static const CLSID ModernId = {0x817fcb27,0x554d,0x4db7,{0xa1,0x8b,0x3d,0xc3,0x78,0x55,0x7d,0xe1}};
static const CLSID ClassicId = {0x04a89c32,0x9378,0x4e77,{0x83,0x4f,0xf4,0xa6,0xcf,0x89,0xc7,0x08}};
static constexpr unsigned Count = 7;
static const wchar_t* Actions[Count] = {L"open",L"extract",L"smart",L"here",L"folder",L"test",L"compress"};
static const wchar_t* English[Count] = {L"Open archive",L"Extract to…",L"Extract here (smart)",L"Extract here",L"Extract to archive folder",L"Test archive",L"Compress to…"};
static const wchar_t* Chinese[Count] = {L"打开压缩包",L"解压到…",L"智能解压",L"解压到当前目录",L"解压到同名文件夹",L"测试完整性",L"压缩为…"};

static std::wstring BasePath() {
    wchar_t path[32768];
    DWORD size = GetModuleFileNameW(module, path, ARRAYSIZE(path));
    if (!size || size == ARRAYSIZE(path)) return {};
    std::wstring result(path, size);
    return result.substr(0, result.find_last_of(L"\\/") + 1);
}
static bool ChineseLanguage() {
    UINT32 length = 0;
    if (GetCurrentPackageFamilyName(&length, nullptr) == ERROR_INSUFFICIENT_BUFFER) {
        std::wstring family(length, L'\0');
        if (GetCurrentPackageFamilyName(&length, family.data()) == ERROR_SUCCESS) {
            family.resize(length - 1);
            PWSTR local = nullptr;
            if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_DEFAULT, nullptr, &local))) {
                std::wstring file = std::wstring(local) + L"\\Packages\\" + family + L"\\LocalState\\shell-language.txt";
                CoTaskMemFree(local);
                std::ifstream input(file);
                std::string language;
                if ((input >> language) && language != "System") return language.rfind("zh", 0) == 0;
            }
        }
    }
    return PRIMARYLANGID(GetUserDefaultUILanguage()) == LANG_CHINESE;
}
static const wchar_t* Title(unsigned action) { return ChineseLanguage() ? Chinese[action] : English[action]; }
static bool Supported(const std::wstring& path) {
    const wchar_t* extension = PathFindExtensionW(path.c_str());
    for (auto candidate : SupportedExtensions)
        if (_wcsicmp(extension,candidate)==0) return true;
    return false;
}
static std::vector<std::wstring> Selection(IShellItemArray* items) {
    std::vector<std::wstring> paths;
    DWORD count = 0;
    if (!items || FAILED(items->GetCount(&count))) return paths;
    for (DWORD i=0; i<count; ++i) {
        ComPtr<IShellItem> item;
        PWSTR path = nullptr;
        if (FAILED(items->GetItemAt(i,&item)) || FAILED(item->GetDisplayName(SIGDN_FILESYSPATH,&path))) return {};
        std::wstring value(path); CoTaskMemFree(path);
        paths.push_back(std::move(value));
    }
    return paths;
}
static HRESULT Launch(unsigned action, const std::vector<std::wstring>& paths, HWND owner = nullptr) {
    if (action >= Count || paths.empty() || (action == 0 && paths.size() != 1)) return E_INVALIDARG;
    std::wstring args = L"--shell " + std::wstring(Actions[action]) + L" --";
    for (auto& path : paths) {
        // Selected items are filesystem paths, never command fragments.
        if ((action < 6 && !Supported(path)) || path.find(L'"') != std::wstring::npos) return E_INVALIDARG;
        args += L" \"" + path + L"\"";
    }
    std::wstring exe = BasePath() + L"AuraZip.exe";
    if (args.size() + exe.size() > 32760) return HRESULT_FROM_WIN32(ERROR_FILENAME_EXCED_RANGE);
    UINT32 familyLength=0;
    if(GetCurrentPackageFamilyName(&familyLength,nullptr)==ERROR_INSUFFICIENT_BUFFER) {
        std::wstring family(familyLength,L'\0');
        LONG status=GetCurrentPackageFamilyName(&familyLength,family.data());
        if(status!=ERROR_SUCCESS) return HRESULT_FROM_WIN32(status);
        family.resize(familyLength-1);
        ComPtr<IApplicationActivationManager> activation;
        HRESULT hr=CoCreateInstance(CLSID_ApplicationActivationManager,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&activation));
        if(FAILED(hr)) return hr;
        DWORD processId=0;
        return activation->ActivateApplication((family+L"!App").c_str(),args.c_str(),AO_NONE,&processId);
    }
    SHELLEXECUTEINFOW info{sizeof(info)};
    info.fMask = SEE_MASK_FLAG_NO_UI;
    info.hwnd = owner; info.lpFile = exe.c_str(); info.lpParameters = args.c_str(); info.nShow = SW_SHOWNORMAL;
    if (!ShellExecuteExW(&info)) return HRESULT_FROM_WIN32(GetLastError());
    return S_OK;
}

class RefCount {
    std::atomic<ULONG> refs{1};
public:
    RefCount() { ++objects; }
    virtual ~RefCount() { --objects; }
    ULONG Add() { return ++refs; }
    ULONG ReleaseRef() { ULONG value=--refs; if (!value) delete this; return value; }
};
class CommandEnum;
class ExplorerCommand final : public IExplorerCommand, public RefCount {
    int action;
public:
    explicit ExplorerCommand(int value = -1):action(value){}
    IFACEMETHODIMP QueryInterface(REFIID id, void** value) override {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (id == IID_IUnknown || id == __uuidof(IExplorerCommand)) *value=static_cast<IExplorerCommand*>(this);
        if (!*value) return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return Add(); }
    IFACEMETHODIMP_(ULONG) Release() override { return ReleaseRef(); }
    IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* value) override { return SHStrDupW(action < 0 ? L"AuraZip" : Title(action),value); }
    IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* value) override { return SHStrDupW((BasePath()+L"Assets\\AuraZip.ico").c_str(),value); }
    IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* value) override { *value=nullptr; return E_NOTIMPL; }
    IFACEMETHODIMP GetCanonicalName(GUID* value) override { *value=ModernId; value->Data1 += action+1; return S_OK; }
    IFACEMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* value) override {
        auto paths=Selection(items);
        *value=paths.empty() || (action>=0 && action<6 && !std::all_of(paths.begin(),paths.end(),Supported)) ? ECS_HIDDEN : action==0 && paths.size()!=1 ? ECS_DISABLED : ECS_ENABLED;
        return S_OK;
    }
    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override {
        try { return action<0 ? E_NOTIMPL : Launch(action,Selection(items)); } catch (...) { return E_FAIL; }
    }
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* value) override { *value=action<0 ? ECF_HASSUBCOMMANDS : ECF_DEFAULT; return S_OK; }
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** value) override;
};
class CommandEnum final : public IEnumExplorerCommand, public RefCount {
    ULONG position=0;
public:
    explicit CommandEnum(ULONG start=0):position(start){}
    IFACEMETHODIMP QueryInterface(REFIID id,void** value) override {
        if (!value) return E_POINTER; *value=nullptr;
        if (id==IID_IUnknown || id==__uuidof(IEnumExplorerCommand)) *value=static_cast<IEnumExplorerCommand*>(this);
        if (!*value) return E_NOINTERFACE; AddRef(); return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return Add(); }
    IFACEMETHODIMP_(ULONG) Release() override { return ReleaseRef(); }
    IFACEMETHODIMP Next(ULONG count,IExplorerCommand** values,ULONG* fetched) override {
        if (!values || (!fetched && count!=1)) return E_POINTER;
        ULONG done=0;
        try { while(done<count && position<Count) values[done++]=new ExplorerCommand(position++); }
        catch (...) { if(fetched) *fetched=done; return E_OUTOFMEMORY; }
        if(fetched) *fetched=done; return done==count ? S_OK : S_FALSE;
    }
    IFACEMETHODIMP Skip(ULONG count) override { ULONG remaining=Count-position; position+=std::min(count,remaining); return count<=remaining ? S_OK : S_FALSE; }
    IFACEMETHODIMP Reset() override { position=0; return S_OK; }
    IFACEMETHODIMP Clone(IEnumExplorerCommand** value) override { *value=new(std::nothrow) CommandEnum(position); return *value ? S_OK : E_OUTOFMEMORY; }
};
HRESULT ExplorerCommand::EnumSubCommands(IEnumExplorerCommand** value) {
    *value=nullptr; if(action>=0) return E_NOTIMPL;
    *value=new(std::nothrow) CommandEnum(); return *value ? S_OK : E_OUTOFMEMORY;
}

class ClassicMenu final : public IShellExtInit, public IContextMenu, public RefCount {
    std::vector<std::wstring> paths;
public:
    IFACEMETHODIMP QueryInterface(REFIID id,void** value) override {
        if(!value) return E_POINTER; *value=nullptr;
        if(id==IID_IUnknown || id==IID_IShellExtInit) *value=static_cast<IShellExtInit*>(this);
        else if(id==IID_IContextMenu) *value=static_cast<IContextMenu*>(this);
        if(!*value) return E_NOINTERFACE; AddRef(); return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return Add(); }
    IFACEMETHODIMP_(ULONG) Release() override { return ReleaseRef(); }
    IFACEMETHODIMP Initialize(PCIDLIST_ABSOLUTE,IDataObject* data,HKEY) override {
        paths.clear(); if(!data) return E_INVALIDARG;
        ComPtr<IShellItemArray> items;
        HRESULT hr=SHCreateShellItemArrayFromDataObject(data,IID_PPV_ARGS(&items));
        if(FAILED(hr)) return hr;
        paths=Selection(items.Get()); return S_OK;
    }
    IFACEMETHODIMP QueryContextMenu(HMENU menu,UINT index,UINT first,UINT last,UINT flags) override {
        if((flags&CMF_DEFAULTONLY) || paths.empty() || last<first || last-first+1<Count) return MAKE_HRESULT(SEVERITY_SUCCESS,0,0);
        HMENU submenu=CreatePopupMenu(); if(!submenu) return E_OUTOFMEMORY;
        for(unsigned i=0;i<Count;++i) if(i==6 || std::all_of(paths.begin(),paths.end(),Supported)) AppendMenuW(submenu,MF_STRING | (i==0 && paths.size()!=1 ? MF_GRAYED : 0),first+i,Title(i));
        MENUITEMINFOW item{sizeof(item)};
        item.fMask=MIIM_STRING|MIIM_SUBMENU; item.dwTypeData=const_cast<PWSTR>(L"AuraZip"); item.hSubMenu=submenu;
        if(!InsertMenuItemW(menu,index,TRUE,&item)) { DestroyMenu(submenu); return HRESULT_FROM_WIN32(GetLastError()); }
        return MAKE_HRESULT(SEVERITY_SUCCESS,0,Count);
    }
    IFACEMETHODIMP InvokeCommand(CMINVOKECOMMANDINFO* info) override {
        if(!info) return E_POINTER;
        unsigned action=Count;
        if(HIWORD(info->lpVerb)==0) action=LOWORD(info->lpVerb);
        else for(unsigned i=0;i<Count;++i) {
            char verb[32]; WideCharToMultiByte(CP_UTF8,0,Actions[i],-1,verb,sizeof(verb),nullptr,nullptr);
            if(_stricmp(info->lpVerb,verb)==0) { action=i; break; }
        }
        try { return Launch(action,paths,info->hwnd); } catch(...) { return E_FAIL; }
    }
    IFACEMETHODIMP GetCommandString(UINT_PTR id,UINT flags,UINT*,LPSTR text,UINT size) override {
        if(id>=Count) return E_INVALIDARG;
        if(flags==GCS_VERBW) return StringCchCopyW(reinterpret_cast<PWSTR>(text),size,Actions[id]);
        if(flags==GCS_VERBA) { return WideCharToMultiByte(CP_UTF8,0,Actions[id],-1,text,size,nullptr,nullptr) ? S_OK : E_FAIL; }
        return E_NOTIMPL;
    }
};
class Factory final : public IClassFactory, public RefCount {
    bool classic;
public:
    explicit Factory(bool value):classic(value){}
    IFACEMETHODIMP QueryInterface(REFIID id,void** value) override {
        if(!value) return E_POINTER; *value=nullptr;
        if(id==IID_IUnknown || id==IID_IClassFactory) *value=static_cast<IClassFactory*>(this);
        if(!*value) return E_NOINTERFACE; AddRef(); return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return Add(); }
    IFACEMETHODIMP_(ULONG) Release() override { return ReleaseRef(); }
    IFACEMETHODIMP CreateInstance(IUnknown* outer,REFIID id,void** value) override {
        if(!value) return E_POINTER; *value=nullptr; if(outer) return CLASS_E_NOAGGREGATION;
        try {
            IUnknown* object=classic ? static_cast<IUnknown*>(static_cast<IShellExtInit*>(new ClassicMenu())) : static_cast<IUnknown*>(static_cast<IExplorerCommand*>(new ExplorerCommand()));
            HRESULT hr=object->QueryInterface(id,value); object->Release(); return hr;
        } catch(...) { return E_OUTOFMEMORY; }
    }
    IFACEMETHODIMP LockServer(BOOL lock) override { if(lock) ++objects; else --objects; return S_OK; }
};
extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID id,REFIID iid,void** value) {
    if(!value) return E_POINTER; *value=nullptr;
    if(id!=ModernId && id!=ClassicId) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory=new(std::nothrow) Factory(id==ClassicId); if(!factory) return E_OUTOFMEMORY;
    HRESULT hr=factory->QueryInterface(iid,value); factory->Release(); return hr;
}
extern "C" HRESULT __stdcall DllCanUnloadNow() { return objects==0 ? S_OK : S_FALSE; }
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID) { if(reason==DLL_PROCESS_ATTACH) { module=instance; DisableThreadLibraryCalls(instance); } return TRUE; }
