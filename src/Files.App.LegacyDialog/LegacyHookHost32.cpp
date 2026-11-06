// Copyright (c) Files Community
// Licensed under the MIT License.
//
// 32-bit host for the legacy file-dialog hook. A 64-bit process cannot install
// a WH_GETMESSAGE hook whose procedure lives in a 32-bit DLL, so NAI OS keeps
// this small EXE running while the "legacy dialogs" option is enabled; it
// loads the 32-bit hook DLL, installs the hook, and stays alive until the
// NAI OS process exits or signals the exit event.

#include <windows.h>
#include <cstdio>
#include <cstdarg>
#include <string>

static const wchar_t* EXIT_EVENT_NAME = L"NAI.LegacyHook.Exit";

static void LogError(const wchar_t* format, ...)
{
	wchar_t path[MAX_PATH];
	GetTempPathW(MAX_PATH - 1, path);
	wcscat_s(path, L"legacy-hook-host.log");
	FILE* f = nullptr;
	_wfopen_s(&f, path, L"a");
	if (!f) return;
	va_list args;
	va_start(args, format);
	vfwprintf(f, format, args);
	va_end(args);
	fwprintf(f, L"\n");
	fclose(f);
}

int wmain(int argc, wchar_t** argv)
{
	// argv[1] = full path of the 32-bit hook DLL (Files.App.LegacyDialog32.dll)
	// argv[2] = parent (NAI OS) PID to watch
	if (argc < 3)
		return 1;

	HMODULE hook = LoadLibraryW(argv[1]);
	if (!hook)
	{
		LogError(L"LoadLibrary failed: %d", GetLastError());
		return 2;
	}

	auto install = (BOOL(WINAPI*)(void))GetProcAddress(hook, "InstallLegacyHook");
	if (!install)
	{
		LogError(L"InstallLegacyHook export missing: %d", GetLastError());
		return 3;
	}
	if (!install())
	{
		LogError(L"InstallLegacyHook returned FALSE: %d", GetLastError());
		return 3;
	}

	HANDLE exitEvent = CreateEventW(NULL, TRUE, FALSE, EXIT_EVENT_NAME);
	HANDLE parent = OpenProcess(SYNCHRONIZE, FALSE, (DWORD)_wtoi(argv[2]));

	HANDLE waits[2] = { exitEvent, parent };
	int count = 1;
	if (parent) waits[count++] = parent;
	WaitForMultipleObjects(count, waits, FALSE, INFINITE);

	auto uninstall = (void(WINAPI*)(void))GetProcAddress(hook, "UninstallLegacyHook");
	if (uninstall)
		uninstall();

	if (parent) CloseHandle(parent);
	return 0;
}