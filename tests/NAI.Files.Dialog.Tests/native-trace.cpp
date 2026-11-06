// Compiles the production COM implementation; substitutes only activation with a managed worker.
#define FILES_DIALOG_TESTING
#ifdef TEST_SAVE
#include "../../src/Files.App.SaveDialog/FilesSaveDialog.cpp"
#include "../../src/Files.App.SaveDialog/CustomSaveDialog_i.c"
using Dialog = CFilesSaveDialog;
using DialogInterface = IFileSaveDialog;
#else
#include "../../src/Files.App.OpenDialog/FilesOpenDialog.cpp"
#include "../../src/Files.App.OpenDialog/CustomOpenDialog_i.c"
using Dialog = CFilesOpenDialog;
using DialogInterface = IFileOpenDialog;
#endif
#include <filesystem>
#include <thread>
#include <stdexcept>
#include <atomic>

class TraceModule : public ATL::CAtlExeModuleT<TraceModule> {} traceModule;
static std::wstring worker;
static thread_local std::vector<std::wstring> reply;
static thread_local std::wstring requestPath;
static thread_local bool failLaunch = false;
static thread_local bool crashWorker = false;
static std::atomic<int> passed = 0;

class CallerEvents : public IFileDialogEvents
{
	LONG references = 1;
public:
	HRESULT response = E_NOTIMPL;
	bool vetoFirst = false;
	unsigned acceptedCalls = 0;
	HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override
	{
		if (!value) return E_POINTER;
		*value = nullptr;
		if (iid != IID_IUnknown && iid != IID_IFileDialogEvents) return E_NOINTERFACE;
		*value = static_cast<IFileDialogEvents*>(this);
		AddRef();
		return S_OK;
	}
	ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&references); }
	ULONG STDMETHODCALLTYPE Release() override
	{
		const auto count = InterlockedDecrement(&references);
		if (!count) delete this;
		return count;
	}
	HRESULT STDMETHODCALLTYPE OnFileOk(IFileDialog*) override
	{
		++acceptedCalls;
		return vetoFirst && acceptedCalls == 1 ? S_FALSE : response;
	}
	HRESULT STDMETHODCALLTYPE OnFolderChanging(IFileDialog*, IShellItem*) override { return E_NOTIMPL; }
	HRESULT STDMETHODCALLTYPE OnFolderChange(IFileDialog*) override { return E_NOTIMPL; }
	HRESULT STDMETHODCALLTYPE OnSelectionChange(IFileDialog*) override { return E_NOTIMPL; }
	HRESULT STDMETHODCALLTYPE OnShareViolation(IFileDialog*, IShellItem*, FDE_SHAREVIOLATION_RESPONSE*) override { return E_NOTIMPL; }
	HRESULT STDMETHODCALLTYPE OnTypeChange(IFileDialog*) override { return E_NOTIMPL; }
	HRESULT STDMETHODCALLTYPE OnOverwrite(IFileDialog*, IShellItem*, FDE_OVERWRITE_RESPONSE*) override { return E_NOTIMPL; }
};

static void Check(bool condition, const char* message)
{
	if (!condition) throw std::runtime_error(message);
	std::cout << "PASS " << message << std::endl;
	++passed;
}

static HRESULT LaunchWorker(const std::wstring& output, const std::wstring&)
{
	requestPath = output;
	if (failLaunch) return HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED);
	std::wstring command = L"\"" + worker + (crashWorker ? L"\" --dialog-worker-crash \"" : L"\" --dialog-worker \"") + output + L"\"";
	if (reply.empty()) command += L" cancel";
	for (const auto& path : reply) command += L" \"" + path + L"\"";
	STARTUPINFOW startup = { sizeof(startup) };
	PROCESS_INFORMATION process = {};
	if (!CreateProcessW(nullptr, command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process))
		return HRESULT_FROM_WIN32(GetLastError());
	CloseHandle(process.hThread);
	CloseHandle(process.hProcess);
	// Deliberately no process handle: ShellExecute protocol activation also permits this.
	return S_OK;
}

static ATL::CComPtr<DialogInterface> CreatePicker()
{
	ATL::CComObject<Dialog>* instance = nullptr;
	Check(SUCCEEDED(ATL::CComObject<Dialog>::CreateInstance(&instance)), "actual COM dialog implementation constructs");
	ATL::CComPtr<DialogInterface> dialog = static_cast<DialogInterface*>(instance);
	return dialog;
}

static std::wstring GetPath(DialogInterface* dialog)
{
	ATL::CComPtr<IShellItem> item;
	const auto result = dialog->GetResult(&item);
	if (FAILED(result)) std::cerr << "GetResult HRESULT=0x" << std::hex << result << std::dec << std::endl;
	Check(SUCCEEDED(result) && item, "GetResult returns a real IShellItem");
	PWSTR value = nullptr;
	Check(SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &value)), "caller obtains filesystem path from IShellItem");
	std::wstring path = value;
	CoTaskMemFree(value);
	return path;
}

int wmain(int argc, wchar_t** argv)
{
	if (argc != 2) return 2;
	CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
	if (std::wstring(argv[1]) == L"--registered")
	{
		try
		{
			ATL::CComPtr<DialogInterface> installed;
#ifdef TEST_SAVE
			const auto hr = installed.CoCreateInstance(CLSID_FileSaveDialog, nullptr, CLSCTX_INPROC_SERVER);
#else
			const auto hr = installed.CoCreateInstance(CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER);
#endif
			Check(SUCCEEDED(hr), "registered system picker CLSID creates the Files COM implementation");
			MEMORY_BASIC_INFORMATION memory = {};
			Check(VirtualQuery(*reinterpret_cast<void***>(installed.p), &memory, sizeof(memory)) != 0, "registered COM vtable is inspectable");
			wchar_t module[32768];
			GetModuleFileNameW(static_cast<HMODULE>(memory.AllocationBase), module, _countof(module));
			std::wstring loaded = module;
#ifdef TEST_SAVE
			Check(loaded.find(L"Files.App.SaveDialog64.dll") != std::wstring::npos, "loaded registered server is the new Files save bridge");
#else
			Check(loaded.find(L"Files.App.OpenDialog64.dll") != std::wstring::npos, "loaded registered server is the new Files open bridge");
#endif
			std::wcout << L"REGISTERED_SERVER " << loaded << std::endl;
		}
		catch (const std::exception& error)
		{
			std::cerr << error.what() << std::endl;
			return 1;
		}
		return 0;
	}
	worker = argv[1];
	FilesDialogBridge::TestLaunch = LaunchWorker;
	wchar_t temp[MAX_PATH];
	GetTempPathW(MAX_PATH, temp);
	wchar_t fullTemp[32768];
	const auto expanded = GetLongPathNameW(temp, fullTemp, _countof(fullTemp));
	const auto root = std::filesystem::path(expanded ? fullTemp : temp) / (L"nai-com-trace-" + std::to_wstring(GetCurrentProcessId()));
	std::filesystem::create_directories(root);
	try
	{
		const auto first = (root / L"vyber žluťoučký file.txt").wstring();
		const auto second = (root / L"-second file.txt").wstring();
#ifndef TEST_SAVE
		std::ofstream(first) << "fixture one";
		std::ofstream(second) << "fixture two";
#endif
		auto dialog = CreatePicker();
		FILEOPENDIALOGOPTIONS options = {};
		Check(SUCCEEDED(dialog->GetOptions(&options)) && (options & FOS_PATHMUSTEXIST), "new COM instance has deterministic filesystem dialog defaults");
		Check(SUCCEEDED(dialog->SetFileTypeIndex(2)), "caller can set its one-based file type index");
		UINT typeIndex = 0;
		Check(SUCCEEDED(dialog->GetFileTypeIndex(&typeIndex)) && typeIndex == 2, "requested Writer filter is not replaced with constant index one");
		ATL::CComQIPtr<IFileDialogCustomize> customize(dialog);
		Check(customize && SUCCEEDED(customize->AddComboBox(123)), "Writer version-control interface is available");
		DWORD selectedControlItem = 999;
		Check(FAILED(customize->GetSelectedControlItem(123, &selectedControlItem)), "empty version list cannot report a fabricated selected index zero");
		Check(FAILED(customize->SetSelectedControlItem(123, 7)), "custom list cannot select an absent item");
		Check(SUCCEEDED(customize->AddControlItem(123, 7, L"version one"))
			&& SUCCEEDED(customize->SetSelectedControlItem(123, 7)), "custom list records a real selected item");
		customize->AddControlItem(124, 9, L"other control");
		customize->SetSelectedControlItem(124, 9);
		Check(SUCCEEDED(customize->GetSelectedControlItem(123, &selectedControlItem)) && selectedControlItem == 7, "custom selections are scoped by control ID");
		customize->RemoveControlItem(123, 7);
		Check(FAILED(customize->GetSelectedControlItem(123, &selectedControlItem)), "removing selected version clears its selection");
		customize->RemoveAllControlItems(124);
		Check(FAILED(customize->GetSelectedControlItem(124, &selectedControlItem)), "clearing a list cannot leak another control's selection");
		reply = { first };
		Check(dialog->Show(nullptr) == S_OK, "Show waits for managed writer with no launcher process handle");
		const auto actual = GetPath(dialog);
		if (actual != first) std::wcerr << L"Expected: " << first << L"\nActual: " << actual << std::endl;
		Check(actual == first, "native caller receives exact Unicode/space path");
		Check(!std::filesystem::exists(requestPath), "native reader closes before deleting result");
		Check(!std::filesystem::exists(requestPath + L".pid"), "request process marker is removed");
		ATL::CComPtr<CallerEvents> events;
		events.Attach(new CallerEvents());
		DWORD cookie = 0;
		Check(SUCCEEDED(dialog->Advise(events, &cookie)), "caller event sink subscribes to the actual dialog");
		const auto notifiedResult = dialog->Show(nullptr);
		std::cout << "CALLER OnFileOk=0x80004001; Show=0x" << std::hex << notifiedResult << std::dec << std::endl;
		Check(notifiedResult == S_OK, "unimplemented caller notification cannot turn accepted selection into failed Show");
		Check(events->acceptedCalls == 1 && GetPath(dialog) == first, "subscribed caller receives accepted file once");
		events->vetoFirst = true;
		events->acceptedCalls = 0;
		Check(dialog->Show(nullptr) == S_OK && events->acceptedCalls == 2, "only explicit S_FALSE veto requests another selection");
		Check(SUCCEEDED(dialog->Unadvise(cookie)), "caller event sink can unsubscribe");
#ifdef TEST_SAVE
		Check(!std::filesystem::exists(first), "save dialog does not create or truncate caller's destination");
#else
		dialog->SetOptions(FOS_ALLOWMULTISELECT | FOS_FILEMUSTEXIST);
		reply = { first, second };
		Check(dialog->Show(nullptr) == S_OK, "same COM object supports a fresh multi-open request");
		ATL::CComPtr<IShellItemArray> items;
		Check(SUCCEEDED(dialog->GetResults(&items)), "GetResults returns real shell item array");
		DWORD size = 0;
		items->GetCount(&size);
		Check(size == 2, "multi-open delivers both selected files");
#endif
		reply.clear();
		Check(dialog->Show(nullptr) == HRESULT_FROM_WIN32(ERROR_CANCELLED), "Cancel wakes caller instead of hanging or debugging it");
		failLaunch = true;
		Check(dialog->Show(nullptr) == HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED), "activation failure is returned immediately");
		failLaunch = false;
		crashWorker = true;
		Check(dialog->Show(nullptr) == HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED), "picker process failure releases native caller without hanging");
		crashWorker = false;

		std::atomic<int> completed = 0;
		auto parallel = [&](const std::wstring& expected)
		{
			CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
			try
			{
				auto request = CreatePicker();
				reply = { expected };
				if (request->Show(nullptr) == S_OK && GetPath(request) == expected) ++completed;
			}
			catch (...) { }
			CoUninitialize();
		};
		std::thread a(parallel, first), b(parallel, second);
		a.join(); b.join();
		Check(completed == 2, "concurrent COM requests cannot consume each other's completion");
		std::cout << passed << " native dialog assertions passed" << std::endl;
	}
	catch (const std::exception& error)
	{
		std::cerr << "FAIL " << error.what() << std::endl;
		std::filesystem::remove_all(root);
		return 1;
	}
	std::filesystem::remove_all(root);
	CoUninitialize();
	return 0;
}
