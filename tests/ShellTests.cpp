#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shlobj.h>
#include <wrl/client.h>
#include <iostream>
#include <vector>
using Microsoft::WRL::ComPtr;
static const CLSID ModernId={0x817fcb27,0x554d,0x4db7,{0xa1,0x8b,0x3d,0xc3,0x78,0x55,0x7d,0xe1}};
static const CLSID ClassicId={0x04a89c32,0x9378,0x4e77,{0x83,0x4f,0xf4,0xa6,0xcf,0x89,0xc7,0x08}};
static void Check(bool value,const char* text) { if(!value) {std::cerr<<"FAIL "<<text<<"\n";exit(1);} std::cout<<"PASS "<<text<<"\n"; }
int wmain(int argc,wchar_t** argv) {
    if(argc<3) return 2;
    CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);
    HMODULE dll=LoadLibraryW(argv[1]); Check(dll!=nullptr,"load native extension");
    auto factoryFn=reinterpret_cast<HRESULT(__stdcall*)(REFCLSID,REFIID,void**)>(GetProcAddress(dll,"DllGetClassObject"));
    Check(factoryFn!=nullptr,"COM export");
    ComPtr<IClassFactory> factory;
    Check(SUCCEEDED(factoryFn(ModernId,IID_PPV_ARGS(&factory))),"modern factory");
    ComPtr<IExplorerCommand> root;
    Check(SUCCEEDED(factory->CreateInstance(nullptr,IID_PPV_ARGS(&root))),"modern IExplorerCommand");
    PWSTR title=nullptr;root->GetTitle(nullptr,&title);Check(title && std::wstring(title)==L"AuraZip","one AuraZip root");CoTaskMemFree(title);
    EXPCMDFLAGS flags;root->GetFlags(&flags);Check((flags&ECF_HASSUBCOMMANDS)!=0,"root is a submenu");
    ComPtr<IShellItem> selected;Check(SUCCEEDED(SHCreateItemFromParsingName(argv[2],nullptr,IID_PPV_ARGS(&selected))),"real selected archive");
    ComPtr<IShellItemArray> items;Check(SUCCEEDED(SHCreateShellItemArrayFromShellItem(selected.Get(),IID_PPV_ARGS(&items))),"shell selection");
    EXPCMDSTATE state;root->GetState(items.Get(),FALSE,&state);Check(state==ECS_ENABLED,"archive enables modern menu");
    ComPtr<IEnumExplorerCommand> commands;root->EnumSubCommands(&commands);
    std::vector<ComPtr<IExplorerCommand>> children;
    for(;;){ComPtr<IExplorerCommand> child;ULONG fetched=0;if(commands->Next(1,&child,&fetched)!=S_OK) break;children.push_back(child);}
    Check(children.size()==7,"seven child commands");
    for(auto& child:children){child->GetTitle(items.Get(),&title);Check(title && *title,"localized child title");CoTaskMemFree(title);}
    commands->Reset();commands->Skip(2);ComPtr<IEnumExplorerCommand> clone;commands->Clone(&clone);ComPtr<IExplorerCommand> cloned;clone->Next(1,&cloned,nullptr);GUID canonical;cloned->GetCanonicalName(&canonical);Check(canonical.Data1==ModernId.Data1+3,"enumerator clone preserves position");
    if(argc>3 && std::wstring(argv[3])==L"--invoke-smart") Check(SUCCEEDED(children[2]->Invoke(items.Get(),nullptr)),"modern smart extraction invokes application");
    if(argc>3 && (std::wstring(argv[3])==L"--registered" || std::wstring(argv[3])==L"--registered-en" || std::wstring(argv[3])==L"--registered-invoke-smart" || std::wstring(argv[3])==L"--registered-invoke-compress")) {
        ComPtr<IExplorerCommand> registered;
        HRESULT hr=CoCreateInstance(ModernId,nullptr,CLSCTX_LOCAL_SERVER,IID_PPV_ARGS(&registered));
        if(FAILED(hr)) std::cerr<<"Registered activation HRESULT: "<<std::hex<<hr<<"\n";
        Check(SUCCEEDED(hr),"manifest COM registration activates modern handler");
        registered->GetTitle(nullptr,&title);Check(title && std::wstring(title)==L"AuraZip","registered modern root title");CoTaskMemFree(title);
        ComPtr<IEnumExplorerCommand> registeredChildren;registered->EnumSubCommands(&registeredChildren);
        ComPtr<IExplorerCommand> open;registeredChildren->Next(1,&open,nullptr);open->GetTitle(items.Get(),&title);
        if(std::wstring(argv[3])==L"--registered-en") Check(title && std::wstring(title)==L"Open archive","registered shell follows saved English preference");
        CoTaskMemFree(title);
        if(std::wstring(argv[3])==L"--registered-invoke-smart") {
            registeredChildren->Skip(1);ComPtr<IExplorerCommand> smart;registeredChildren->Next(1,&smart,nullptr);
            Check(SUCCEEDED(smart->Invoke(items.Get(),nullptr)),"registered modern shell launches smart extraction");
        }
        if(std::wstring(argv[3])==L"--registered-invoke-compress") { registeredChildren->Skip(5);ComPtr<IExplorerCommand> compress;registeredChildren->Next(1,&compress,nullptr);Check(SUCCEEDED(compress->Invoke(items.Get(),nullptr)),"registered modern shell activates packaged compression"); }
        ComPtr<IShellExtInit> registeredClassic;
        Check(SUCCEEDED(CoCreateInstance(ClassicId,nullptr,CLSCTX_LOCAL_SERVER,IID_PPV_ARGS(&registeredClassic))),"manifest COM registration activates classic handler");
    }
    ComPtr<IShellItem> text;std::wstring txt=std::wstring(argv[2])+L".txt";HANDLE file=CreateFileW(txt.c_str(),GENERIC_WRITE,0,nullptr,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);CloseHandle(file);
    SHCreateItemFromParsingName(txt.c_str(),nullptr,IID_PPV_ARGS(&text));ComPtr<IShellItemArray> other;SHCreateShellItemArrayFromShellItem(text.Get(),IID_PPV_ARGS(&other));root->GetState(other.Get(),FALSE,&state);Check(state==ECS_ENABLED,"ordinary files expose the compression submenu");
    for(size_t i=0;i<children.size();++i){children[i]->GetState(other.Get(),FALSE,&state);Check(state==static_cast<EXPCMDSTATE>(i==6?ECS_ENABLED:ECS_HIDDEN),"ordinary file command visibility");}
    Check(SUCCEEDED(factoryFn(ClassicId,IID_PPV_ARGS(factory.ReleaseAndGetAddressOf()))),"classic factory");
    ComPtr<IShellExtInit> init;Check(SUCCEEDED(factory->CreateInstance(nullptr,IID_PPV_ARGS(&init))),"classic IShellExtInit");
    ComPtr<IDataObject> data;Check(SUCCEEDED(items->BindToHandler(nullptr,BHID_DataObject,IID_PPV_ARGS(&data))),"selection data object");
    Check(SUCCEEDED(init->Initialize(nullptr,data.Get(),nullptr)),"classic receives selection");
    ComPtr<IContextMenu> classic;init.As(&classic);HMENU menu=CreatePopupMenu();HRESULT added=classic->QueryContextMenu(menu,0,1,100,CMF_NORMAL);
    Check(SUCCEEDED(added) && HRESULT_CODE(added)==7,"classic command IDs");Check(GetMenuItemCount(menu)==1,"classic exposes only one top-level item");Check(GetMenuItemCount(GetSubMenu(menu,0))==7,"classic has seven children");DestroyMenu(menu);
    menu=CreatePopupMenu();classic->QueryContextMenu(menu,0,1,100,CMF_DEFAULTONLY);Check(GetMenuItemCount(menu)==0,"default action requests add no menu");DestroyMenu(menu);
    ComPtr<IDataObject> ordinaryData;other->BindToHandler(nullptr,BHID_DataObject,IID_PPV_ARGS(&ordinaryData));
    Check(SUCCEEDED(init->Initialize(nullptr,ordinaryData.Get(),nullptr)),"classic receives ordinary files");
    menu=CreatePopupMenu();classic->QueryContextMenu(menu,0,1,100,CMF_NORMAL);
    Check(GetMenuItemCount(menu)==1 && GetMenuItemCount(GetSubMenu(menu,0))==1,"ordinary files expose only compression inside one classic submenu");DestroyMenu(menu);DeleteFileW(txt.c_str());
    std::cout<<"Shell checks passed.\n";
    return 0;
}
