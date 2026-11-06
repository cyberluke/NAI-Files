// Copyright (c) Files Community
// Licensed under the MIT License.

// Self-healing repair of the per-user CLSID shadows that route
// `CoCreateInstance(CLSID_FileOpenDialog/CLSID_FileSaveDialog)` to Files'
// dialog servers.
//
// These shadows are normally maintained by NAI OS while its shell process is
// running. When Files is a bare sideload, or Files gets updated without NAI OS
// running, the shadows can point at a stale or missing version of the dialog
// bridge, and the Files picker never opens. Every dialog bridge DLL runs
// in-process inside the CALLER (browser / editor / app), which is not subject
// to Files' package registry virtualization, so this helper rewrites the
// shadows in real per-user context on every load, pointing them at the dialog
// DLLs that are on disk next to the currently-loaded bridge.
//
// The user's "replacement enabled" preference is honored via
// `HKCU\Software\NAI OS\Files\Dialogs` `OpenEnabled` / `SaveEnabled` DWORDs
// (absent means enabled). When a replacement is turned off the matching shadow
// is left untouched so the system dialog stays in control.

#pragma once

#include <windows.h>
#include <winreg.h>
#include <stdio.h>
#include <wchar.h>
#include <string.h>

#define NAI_DIALOGS_REG_KEY L"Software\\NAI OS\\Files\\Dialogs"

#define CLSID_FILE_OPEN_DIALOG L"{DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7}"
#define CLSID_FILE_SAVE_DIALOG L"{C0B4E2F3-BA21-4773-8DBA-335EC946EB8B}"

#define OPEN_DIALOG_DLL L"Files.App.OpenDialog64.dll"
#define SAVE_DIALOG_DLL L"Files.App.SaveDialog64.dll"

// Returns false when the caller explicitly disabled the replacement (a real
// DWORD OpenEnabled / SaveEnabled set to 0); absent values default to enabled.
static bool DialogReplacementEnabled(const wchar_t* valueName)
{
	DWORD value = 1;
	DWORD size = sizeof(value);
	return RegGetValueW(HKEY_CURRENT_USER, NAI_DIALOGS_REG_KEY, valueName,
	                    RRF_RT_REG_DWORD, nullptr, &value, &size) != ERROR_SUCCESS
	           || value != 0;
}

// Rewrites one CLSID shadow to point at dllPath (only when the replacement is
// enabled). Restores the exact shape NAI OS writes so the two never fight.
static void RepairDialogClsid(const wchar_t* clsid, const wchar_t* dllPath,
                              const wchar_t* className)
{
	WCHAR keyPath[512];
	if (_snwprintf_s(keyPath, _countof(keyPath), _TRUNCATE,
	                 L"Software\\Classes\\CLSID\\%s", clsid) < 0)
		return;

	HKEY clsidKey = nullptr;
	if (RegCreateKeyExW(HKEY_CURRENT_USER, keyPath, 0, nullptr, 0,
	                    KEY_SET_VALUE | KEY_CREATE_SUB_KEY, nullptr, &clsidKey, nullptr) != ERROR_SUCCESS)
		return;

	LSTATUS status = RegSetValueExW(clsidKey, L"NaiOsOwner", 0, REG_SZ,
	                                reinterpret_cast<const BYTE*>(L"nai-os"),
	                                (DWORD)(sizeof(wchar_t) * 7));
	if (status != ERROR_SUCCESS) { RegCloseKey(clsidKey); return; }
	if (RegSetValueExW(clsidKey, L"", 0, REG_SZ,
	                   reinterpret_cast<const BYTE*>(className),
	                   (DWORD)(wcslen(className) + 1) * sizeof(wchar_t)) != ERROR_SUCCESS)
	{ RegCloseKey(clsidKey); return; }

	WCHAR inprocPath[560];
	if (_snwprintf_s(inprocPath, _countof(inprocPath), _TRUNCATE,
	                 L"Software\\Classes\\CLSID\\%s\\InprocServer32", clsid) >= 0)
	{
		HKEY inprocKey = nullptr;
		if (RegCreateKeyExW(HKEY_CURRENT_USER, inprocPath, 0, nullptr, 0,
		                    KEY_SET_VALUE, nullptr, &inprocKey, nullptr) == ERROR_SUCCESS)
		{
			RegSetValueExW(inprocKey, L"", 0, REG_SZ,
			               reinterpret_cast<const BYTE*>(dllPath),
			               (DWORD)(wcslen(dllPath) + 1) * sizeof(wchar_t));
			RegSetValueExW(inprocKey, L"ThreadingModel", 0, REG_SZ,
			               reinterpret_cast<const BYTE*>(L"Apartment"),
			               (DWORD)(sizeof(wchar_t) * 10));
			RegCloseKey(inprocKey);
		}
	}

	RegCloseKey(clsidKey);
}

// Repairs the shadow that routed to this bridge, plus its sibling (open when
// this is save, save when this is open), pointing at whichever DLL sits next to
// the currently-loaded bridge on disk.
static void RepairDialogRegistration()
{
	// Derive this DLL's own modules from the address of a function defined in
	// it (robust across ATL versions that do not expose the module instance).
	HMODULE module = nullptr;
	if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
	                            GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
	                        reinterpret_cast<LPCWSTR>(&RepairDialogRegistration),
	                        &module))
		return;

	WCHAR modulePath[MAX_PATH];
	if (GetModuleFileNameW(module, modulePath, _countof(modulePath)) == 0)
		return;

	WCHAR directory[MAX_PATH];
	WCHAR* slash = wcsrchr(modulePath, L'\\');
	if (!slash) return;
	*slash = L'\0';
	wcscpy_s(directory, modulePath);

	WCHAR openDll[MAX_PATH], saveDll[MAX_PATH];
	wcscpy_s(openDll, _countof(openDll), directory);
	wcscat_s(openDll, L"\\");
	wcscat_s(openDll, OPEN_DIALOG_DLL);
	wcscpy_s(saveDll, _countof(saveDll), directory);
	wcscat_s(saveDll, L"\\");
	wcscat_s(saveDll, SAVE_DIALOG_DLL);

	const bool openEnabled = DialogReplacementEnabled(L"OpenEnabled");
	const bool saveEnabled = DialogReplacementEnabled(L"SaveEnabled");

	if (openEnabled && GetFileAttributesW(openDll) != INVALID_FILE_ATTRIBUTES)
		RepairDialogClsid(CLSID_FILE_OPEN_DIALOG, openDll, L"FilesOpenDialog class");
	if (saveEnabled && GetFileAttributesW(saveDll) != INVALID_FILE_ATTRIBUTES)
		RepairDialogClsid(CLSID_FILE_SAVE_DIALOG, saveDll, L"FilesSaveDialog class");
}