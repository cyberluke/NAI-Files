// Copyright (c) Files Community
// Licensed under the MIT License.
//
// Legacy file-dialog interception for the Files bridge.
//
// The COM CLSID shadow (FilesOpenDialog/FilesSaveDialog) only covers callers
// that create CLSID_FileOpenDialog/FileSaveDialog. Legacy Win32 apps call
// comdlg32's GetOpenFileName/GetSaveFileName or shell32's SHBrowseForFolder
// directly; on modern Windows comdlg32 renders those with its own in-proc UI,
// so a per-user CLSID override never sees them.
//
// This DLL closes that gap with an import-table hook: NAI OS installs a
// WH_GETMESSAGE hook (x64 in-process, x86 through a small host EXE), which
// injects this module into every message-pumping process. On attach it rewrites
// the GetOpenFileNameW/A, GetSaveFileNameW/A and SHBrowseForFolderW import
// slots to shims that run the same `files-dev:` bridge as the COM servers.
// The per-call registry flag (HKCU\Software\NAI OS\Files\Dialogs\Legacy)
// decides interception, so toggling the setting takes effect without restarting
// target apps; when disabled the original comdlg32/shell32 function runs.

#include <windows.h>
#include <shlobj.h>
#include <commdlg.h>
#include <tlhelp32.h>
#include <shellapi.h>
#include <string>
#include <vector>
#include <fstream>
#include "../Files.App.OpenDialog/DialogBridge.h"

using std::wstring;
using std::vector;

static HMODULE g_hModule = NULL;
static HHOOK g_hook = NULL;

// Debug diagnostics (development builds only; removed for release packaging by
// the _DEBUG hook below). Append-only log helps trace injection/patch failures.
static void DebugLog(const wchar_t* format, ...)
{
#ifdef NAI_LEGACY_DEBUG
	wchar_t path[MAX_PATH];
	GetTempPathW(MAX_PATH - 1, path);
	wcscat_s(path, L"legacy-hook.log");
	FILE* f = nullptr; _wfopen_s(&f, path, L"a");
	if (!f) return;
	va_list args;
	va_start(args, format);
	vfwprintf(f, format, args);
	va_end(args);
	fwprintf(f, L"\n");
	fclose(f);
#endif
}

// Original function addresses captured while patching the import tables.
static FARPROC g_origOpenFileW = NULL;
static FARPROC g_origOpenFileA = NULL;
static FARPROC g_origSaveFileW = NULL;
static FARPROC g_origSaveFileA = NULL;
static FARPROC g_origBrowseFolder = NULL;
static FARPROC g_origOpenFolderAndSelect = NULL;
static FARPROC g_origOpenFolderAndSelectEx = NULL;

// ---------------------------------------------------------------------------
// Registry preference
// ---------------------------------------------------------------------------

static bool LegacyEnabled()
{
	DWORD value = 0;
	DWORD size = sizeof(value);
	return RegGetValueW(HKEY_CURRENT_USER, L"Software\\NAI OS\\Files\\Dialogs", L"Legacy",
		RRF_RT_REG_DWORD, NULL, &value, &size) == ERROR_SUCCESS && value != 0;
}

// ---------------------------------------------------------------------------
// files-dev bridge (same protocol as the COM dialog servers)
// ---------------------------------------------------------------------------

static wstring Utf8ToWide(const std::string& text)
{
	if (text.empty()) return L"";
	int needed = MultiByteToWideChar(CP_UTF8, 0, text.c_str(), (int)text.size(), NULL, 0);
	if (needed <= 0) return L"";
	wstring result(needed, L'\0');
	MultiByteToWideChar(CP_UTF8, 0, text.c_str(), (int)text.size(), &result[0], needed);
	return result;
}

static wstring DirNameOf(const wstring& path)
{
	auto pos = path.find_last_of(L"\\/");
	return pos == wstring::npos ? L"" : path.substr(0, pos);
}

static wstring BaseNameOf(const wstring& path)
{
	auto pos = path.find_last_of(L"\\/");
	return pos == wstring::npos ? path : path.substr(pos + 1);
}

/// Runs the Files dialog bridge and returns the selected paths (one per line).
/// Returns false when the user cancelled or no path was produced.
static bool RunLegacyBridge(const wchar_t* mode, const wstring& initialDir, const wstring& suggestedName, vector<wstring>& outPaths)
{
	return SUCCEEDED(FilesDialogBridge::Show(nullptr, mode, initialDir, suggestedName, outPaths));
}

// ---------------------------------------------------------------------------
// OPENFILENAME helpers
// ---------------------------------------------------------------------------

static void CopyResultIntoBuffer(const wstring& path, LPWSTR buffer, DWORD maxChars)
{
	if (maxChars == 0) return;
	size_t copy = path.size();
	if (copy >= maxChars) copy = maxChars - 1;
	memcpy(buffer, path.c_str(), copy * sizeof(wchar_t));
	buffer[copy] = L'\0';
}

/// Packs multi-select results in the OFN_EXPLORER format:
/// <dir>\0<file1>\0<file2>\0\0
static void PackMultiSelect(const vector<wstring>& paths, LPWSTR buffer, DWORD maxChars)
{
	if (paths.empty()) { if (maxChars) buffer[0] = L'\0'; return; }
	wstring dir = DirNameOf(paths[0]);
	wstring packed = dir;
	packed.push_back(L'\0');
	for (size_t i = 0; i < paths.size(); i++)
	{
		packed.append(BaseNameOf(paths[i]));
		packed.push_back(L'\0');
	}
	packed.push_back(L'\0');
	CopyResultIntoBuffer(packed, buffer, maxChars);
}

static void SetFileOffsets(LPOPENFILENAMEW ofn, const wstring& path)
{
	size_t base = path.find_last_of(L"\\/");
	size_t nameStart = base == wstring::npos ? 0 : base + 1;
	ofn->nFileOffset = (WORD)nameStart;
	size_t dot = path.find_last_of(L'.');
	size_t extStart = (dot != wstring::npos && dot > nameStart) ? dot + 1 : path.size();
	ofn->nFileExtension = dot == wstring::npos ? 0 : (WORD)extStart;
}

// ---------------------------------------------------------------------------
// Shims (called from the patched import slots)
// ---------------------------------------------------------------------------

static BOOL ShimOpenFileWImpl(LPOPENFILENAMEW ofn)
{
#ifdef NAI_LEGACY_DEBUG
	DebugLog(L"shim open W entered pid=%d enabled=%d orig=%p", GetCurrentProcessId(), LegacyEnabled() ? 1 : 0, (void*)g_origOpenFileW);
#endif
	if (!LegacyEnabled() || !g_origOpenFileW)
		return ((BOOL(WINAPI*)(LPOPENFILENAMEW))g_origOpenFileW)(ofn);

	wstring initial = ofn->lpstrInitialDir && ofn->lpstrInitialDir[0] ? ofn->lpstrInitialDir : L"";
	wstring suggested;
	if (ofn->lpstrFile && ofn->lpstrFile[0])
	{
		if (initial.empty()) initial = DirNameOf(ofn->lpstrFile);
		suggested = BaseNameOf(ofn->lpstrFile);
	}
	bool multi = (ofn->Flags & OFN_ALLOWMULTISELECT) != 0 && (ofn->Flags & OFN_EXPLORER) != 0;

	vector<wstring> paths;
	if (!RunLegacyBridge(multi ? L"multi" : L"open", initial, suggested, paths))
		return FALSE;

	if (multi && paths.size() > 1)
	{
		PackMultiSelect(paths, ofn->lpstrFile, ofn->nMaxFile);
		ofn->nFileOffset = 0; // explorer multiselect layout; offsets apply to single files
	}
	else
	{
		CopyResultIntoBuffer(paths[0], ofn->lpstrFile, ofn->nMaxFile);
		SetFileOffsets(ofn, paths[0]);
	}
	return TRUE;
}

static BOOL WINAPI ShimOpenFileW(LPOPENFILENAMEW ofn)
{
	return ShimOpenFileWImpl(ofn);
}

static BOOL WINAPI ShimSaveFileW(LPOPENFILENAMEW ofn)
{
	if (!LegacyEnabled() || !g_origSaveFileW)
		return ((BOOL(WINAPI*)(LPOPENFILENAMEW))g_origSaveFileW)(ofn);

	wstring initial = ofn->lpstrInitialDir && ofn->lpstrInitialDir[0] ? ofn->lpstrInitialDir : L"";
	wstring suggested;
	if (ofn->lpstrFile && ofn->lpstrFile[0])
	{
		if (initial.empty()) initial = DirNameOf(ofn->lpstrFile);
		suggested = BaseNameOf(ofn->lpstrFile);
	}

	vector<wstring> paths;
	if (!RunLegacyBridge(L"save", initial, suggested, paths))
		return FALSE;

	CopyResultIntoBuffer(paths[0], ofn->lpstrFile, ofn->nMaxFile);
	SetFileOffsets(ofn, paths[0]);
	return TRUE;
}

static BOOL WINAPI ShimOpenFileA(LPOPENFILENAMEA ofn)
{
	if (!LegacyEnabled() || !g_origOpenFileA)
		return ((BOOL(WINAPI*)(LPOPENFILENAMEA))g_origOpenFileA)(ofn);

	// Convert the essential fields to the wide dialog, run it, convert back.
	OPENFILENAMEW wide = { 0 };
	wide.lStructSize = sizeof(wide);
	wide.hwndOwner = ofn->hwndOwner;
	wide.hInstance = ofn->hInstance;
	wide.nFilterIndex = ofn->nFilterIndex;
	wide.nMaxFile = ofn->nMaxFile;
	wide.nMaxFileTitle = ofn->nMaxFileTitle;
	wide.Flags = ofn->Flags;
	wide.lCustData = ofn->lCustData;
	wide.lpfnHook = ofn->lpfnHook;
	wide.FlagsEx = ofn->FlagsEx;

	std::vector<wchar_t> fileBuf(ofn->nMaxFile > 0 ? ofn->nMaxFile : 1);
	if (ofn->lpstrFile) MultiByteToWideChar(CP_ACP, 0, ofn->lpstrFile, -1, fileBuf.data(), (int)fileBuf.size());
	wide.lpstrFile = fileBuf.data();

	wstring initialDir;
	if (ofn->lpstrInitialDir) { initialDir = Utf8ToWide(ofn->lpstrInitialDir); wide.lpstrInitialDir = initialDir.c_str(); }
	wstring title;
	if (ofn->lpstrTitle) { title = Utf8ToWide(ofn->lpstrTitle); wide.lpstrTitle = title.c_str(); }

	BOOL ok = ShimOpenFileWImpl(&wide);
	if (!ok) return FALSE;

	ofn->nFileOffset = wide.nFileOffset;
	ofn->nFileExtension = wide.nFileExtension;
	if (ofn->lpstrFile)
		WideCharToMultiByte(CP_ACP, 0, wide.lpstrFile, -1, ofn->lpstrFile, ofn->nMaxFile, NULL, NULL);
	return TRUE;
}

static BOOL WINAPI ShimSaveFileA(LPOPENFILENAMEA ofn)
{
	if (!LegacyEnabled() || !g_origSaveFileA)
		return ((BOOL(WINAPI*)(LPOPENFILENAMEA))g_origSaveFileA)(ofn);

	OPENFILENAMEW wide = { 0 };
	wide.lStructSize = sizeof(wide);
	wide.hwndOwner = ofn->hwndOwner;
	wide.hInstance = ofn->hInstance;
	wide.nFilterIndex = ofn->nFilterIndex;
	wide.nMaxFile = ofn->nMaxFile;
	wide.Flags = ofn->Flags;
	wide.FlagsEx = ofn->FlagsEx;

	std::vector<wchar_t> fileBuf(ofn->nMaxFile > 0 ? ofn->nMaxFile : 1);
	if (ofn->lpstrFile) MultiByteToWideChar(CP_ACP, 0, ofn->lpstrFile, -1, fileBuf.data(), (int)fileBuf.size());
	wide.lpstrFile = fileBuf.data();

	wstring initialDir;
	if (ofn->lpstrInitialDir) { initialDir = Utf8ToWide(ofn->lpstrInitialDir); wide.lpstrInitialDir = initialDir.c_str(); }
	wstring title;
	if (ofn->lpstrTitle) { title = Utf8ToWide(ofn->lpstrTitle); wide.lpstrTitle = title.c_str(); }

	if (!ShimSaveFileW(&wide))
		return FALSE;

	if (ofn->lpstrFile)
		WideCharToMultiByte(CP_ACP, 0, wide.lpstrFile, -1, ofn->lpstrFile, ofn->nMaxFile, NULL, NULL);
	return TRUE;
}

static LPITEMIDLIST WINAPI ShimBrowseFolderW(LPBROWSEINFOW bi)
{
	if (!LegacyEnabled() || !g_origBrowseFolder)
		return ((LPITEMIDLIST(WINAPI*)(LPBROWSEINFOW))g_origBrowseFolder)(bi);

	wstring initial = bi->pidlRoot ? L"" : L"";
	if (initial.empty())
	{
		WCHAR path[MAX_PATH];
		if (bi->pidlRoot && SHGetPathFromIDListW(bi->pidlRoot, path))
			initial = path;
	}

	vector<wstring> paths;
	if (!RunLegacyBridge(L"folder", initial, L"", paths))
		return NULL;

	LPITEMIDLIST pidl = NULL;
	if (FAILED(SHParseDisplayName(paths[0].c_str(), NULL, &pidl, 0, NULL)))
		return NULL;
	return pidl;
}

// ---------------------------------------------------------------------------
// "Show in folder" interception (SHOpenFolderAndSelectItems[Ex])
// ---------------------------------------------------------------------------
//
// Browsers (Chrome/MS Edge) download-shelf "Open in folder" and any app that
// calls the shell's reveal API (SHOpenFolderAndSelectItems) opens the folder in
// Windows Explorer. Routing that call to Files gives the "show file in folder"
// behaviour for every app, exactly like the open/save dialog shadow. When the
// caller binds the function dynamically (GetProcAddress) instead of via a
// static import, the IAT patch cannot reach it; the OS-level seam (ETW kernel
// trace / future minifilter) in NAI OS closes that remainder. This hook covers
// the static-import path, which is the dominant one for the browser processes.

/// Converts a PIDL to a display path (best-effort, may return empty).
static wstring PidlToPath(PCIDLIST_ABSOLUTE pidl)
{
	if (!pidl) return L"";
	WCHAR path[MAX_PATH];
	return SHGetPathFromIDListW(pidl, path) ? wstring(path) : L"";
}

/// Launches Files to show `folder` with `file` selected. Fire-and-forget; never
/// blocks, never writes a dialog output. Returns true when the launch succeeded.
static bool LaunchFilesReveal(const wstring& folder, const wstring& file)
{
	if (folder.empty() || !g_hModule) return false;

	wstring arguments = L"files-dev.exe -Directory \"" + folder + L"\"";
	if (!file.empty()) arguments += L" -Select \"" + file + L"\"";

	const wstring uri = L"files-dev:?cmd=" + FilesDialogBridge::Encode(arguments);
	SHELLEXECUTEINFOW info = { sizeof(info) };
	info.fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_FLAG_NO_UI;
	info.lpFile = uri.c_str();
	info.nShow = SW_SHOW;
	BOOL ok = ShellExecuteExW(&info);
	if (info.hProcess) CloseHandle(info.hProcess);
	if (!ok)
		DebugLog(L"reveal launch failed gle=%u uri=%ls", GetLastError(), uri.c_str());
	return ok != FALSE;
}

static HRESULT WINAPI ShimOpenFolderAndSelectItems(PCIDLIST_ABSOLUTE pidlFolder,
	UINT cidl, PCUITEMID_CHILD* apidl, DWORD dwFlags)
{
	if (!LegacyEnabled() || !g_origOpenFolderAndSelect)
		return ((HRESULT(WINAPI*)(PCIDLIST_ABSOLUTE, UINT, PCUITEMID_CHILD*, DWORD))g_origOpenFolderAndSelect)(pidlFolder, cidl, apidl, dwFlags);

	wstring folder = PidlToPath(pidlFolder);
	wstring file;
	if (cidl == 1 && apidl && apidl[0])
	{
		// apidl entries are CHILD pidls relative to the folder; combine to an
		// absolute pidl so the child path can be resolved, then take just the
		// final component as the selection name.
		if (LPITEMIDLIST combined = ILCombine(pidlFolder, apidl[0]))
		{
			wstring child = PidlToPath(combined);
			ILFree(combined);
			if (!child.empty())
				file = child.substr(child.find_last_of(L"\\/") + 1);
		}
	}
	if (LaunchFilesReveal(folder, file))
		return S_OK; // Files handled it; tell the caller the reveal succeeded.
	return ((HRESULT(WINAPI*)(PCIDLIST_ABSOLUTE, UINT, PCUITEMID_CHILD*, DWORD))g_origOpenFolderAndSelect)(pidlFolder, cidl, apidl, dwFlags);
}

// Windows SDK shobjidl.h: the "Ex" reveal differs from the base one in argument
// order — (pidlFolder, apidl, cidl, dwFlags, pfdf). `pfdf` (IFolderFilter*) is
// never taken apart here, so it is carried as an opaque pointer to avoid a
// hard shobjidl.h coupling.
static HRESULT WINAPI ShimOpenFolderAndSelectItemsEx(PCIDLIST_ABSOLUTE pidlFolder,
	PCUITEMID_CHILD* apidl, DWORD cidl, DWORD dwFlags, void* pfdf)
{
	if (!LegacyEnabled() || !g_origOpenFolderAndSelectEx || pfdf)
	{
		// Don't decorate when a caller supplies a filter (complex selection): hand off.
		if (g_origOpenFolderAndSelectEx)
			return ((HRESULT(WINAPI*)(PCIDLIST_ABSOLUTE, PCUITEMID_CHILD*, DWORD, DWORD, void*))g_origOpenFolderAndSelectEx)(pidlFolder, apidl, cidl, dwFlags, pfdf);
		return E_NOTIMPL;
	}
	return ShimOpenFolderAndSelectItems(pidlFolder, cidl, apidl, dwFlags);
}

// ---------------------------------------------------------------------------
// Import table patching
// ---------------------------------------------------------------------------

static void PatchThunks(PIMAGE_IMPORT_DESCRIPTOR desc, HMODULE mod, bool shell32)
{
	PIMAGE_THUNK_DATA nameThunk = desc->OriginalFirstThunk
		? (PIMAGE_THUNK_DATA)((BYTE*)mod + desc->OriginalFirstThunk)
		: (PIMAGE_THUNK_DATA)((BYTE*)mod + desc->FirstThunk);
	PIMAGE_THUNK_DATA iatThunk = (PIMAGE_THUNK_DATA)((BYTE*)mod + desc->FirstThunk);

	for (; nameThunk->u1.AddressOfData; nameThunk++, iatThunk++)
	{
		if (IMAGE_SNAP_BY_ORDINAL(nameThunk->u1.Ordinal)) continue;
		auto byName = (PIMAGE_IMPORT_BY_NAME)((BYTE*)mod + nameThunk->u1.AddressOfData);
		const char* fn = (const char*)byName->Name;

		FARPROC target = NULL;
		FARPROC* saved = NULL;
		if (!shell32)
		{
			if (!_stricmp(fn, "GetOpenFileNameW")) { target = (FARPROC)ShimOpenFileW; saved = &g_origOpenFileW; }
			else if (!_stricmp(fn, "GetOpenFileNameA")) { target = (FARPROC)ShimOpenFileA; saved = &g_origOpenFileA; }
			else if (!_stricmp(fn, "GetSaveFileNameW")) { target = (FARPROC)ShimSaveFileW; saved = &g_origSaveFileW; }
			else if (!_stricmp(fn, "GetSaveFileNameA")) { target = (FARPROC)ShimSaveFileA; saved = &g_origSaveFileA; }
		}
		else if (!_stricmp(fn, "SHBrowseForFolderW")) { target = (FARPROC)ShimBrowseFolderW; saved = &g_origBrowseFolder; }
		else if (!_stricmp(fn, "SHOpenFolderAndSelectItems")) { target = (FARPROC)ShimOpenFolderAndSelectItems; saved = &g_origOpenFolderAndSelect; }
		else if (!_stricmp(fn, "SHOpenFolderAndSelectItemsEx")) { target = (FARPROC)ShimOpenFolderAndSelectItemsEx; saved = &g_origOpenFolderAndSelectEx; }

		if (target && saved)
		{
			if (!*saved) *saved = (FARPROC)iatThunk->u1.Function;
			DWORD oldProtect;
			if (VirtualProtect(&iatThunk->u1.Function, sizeof(void*), PAGE_READWRITE, &oldProtect))
			{
#ifdef NAI_LEGACY_DEBUG
				DebugLog(L"patch %hs -> %p (was %p)", fn, target, (void*)iatThunk->u1.Function);
#endif
				iatThunk->u1.Function = (ULONG_PTR)target;
				VirtualProtect(&iatThunk->u1.Function, sizeof(void*), oldProtect, &oldProtect);
			}
		}
	}
}

static void PatchModuleImports(HMODULE mod)
{
	auto dos = (PIMAGE_DOS_HEADER)mod;
	if (dos->e_magic != IMAGE_DOS_SIGNATURE) return;
	auto nt = (PIMAGE_NT_HEADERS)((BYTE*)mod + dos->e_lfanew);
	if (nt->Signature != IMAGE_NT_SIGNATURE) return;

	DWORD impRva = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress;
	if (!impRva) return;

	auto desc = (PIMAGE_IMPORT_DESCRIPTOR)((BYTE*)mod + impRva);
	for (; desc->Name; desc++)
	{
		const char* dll = (const char*)((BYTE*)mod + desc->Name);
		if (!_stricmp(dll, "comdlg32.dll") || !_stricmp(dll, "COMDLG32"))
			PatchThunks(desc, mod, false);
		else if (!_stricmp(dll, "shell32.dll") || !_stricmp(dll, "SHELL32"))
			PatchThunks(desc, mod, true);
	}
}

static void PatchAllModules()
{
#ifdef NAI_LEGACY_DEBUG
	DebugLog(L"patch-all start pid=%d", GetCurrentProcessId());
#endif
	HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, GetCurrentProcessId());
	if (snap == INVALID_HANDLE_VALUE) return;
	MODULEENTRY32W me = { 0 };
	me.dwSize = sizeof(me);
	if (Module32FirstW(snap, &me))
	{
		do
		{
			PatchModuleImports((HMODULE)me.modBaseAddr);
#ifdef NAI_LEGACY_DEBUG
			DebugLog(L"patch module %s", me.szModule);
#endif
		} while (Module32NextW(snap, &me));
	}
	CloseHandle(snap);
#ifdef NAI_LEGACY_DEBUG
	DebugLog(L"patch-all done pid=%d", GetCurrentProcessId());
#endif
}

// ---------------------------------------------------------------------------
// Hook proc + exports
// ---------------------------------------------------------------------------

static LRESULT CALLBACK LegacyHookProc(int nCode, WPARAM wParam, LPARAM lParam)
{
	return CallNextHookEx(NULL, nCode, wParam, lParam);
}

extern "C" __declspec(dllexport) BOOL WINAPI InstallLegacyHook()
{
	if (g_hook) return TRUE;
	g_hook = SetWindowsHookExW(WH_GETMESSAGE, LegacyHookProc, g_hModule, 0);
	return g_hook != NULL;
}

extern "C" __declspec(dllexport) void WINAPI UninstallLegacyHook()
{
	if (g_hook)
	{
		UnhookWindowsHookEx(g_hook);
		g_hook = NULL;
	}
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID)
{
	if (reason == DLL_PROCESS_ATTACH)
	{
		DisableThreadLibraryCalls(hModule);
		g_hModule = hModule;
		PatchAllModules();
	}
	return TRUE;
}
