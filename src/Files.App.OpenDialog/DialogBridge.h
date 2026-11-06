// Copyright (c) Files Community
// Licensed under the MIT License.
#pragma once

#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <fstream>
#include <string>
#include <vector>
#include "DialogOptions.h"

namespace FilesDialogBridge
{
	class FileSystemBindData final : public IFileSystemBindData
	{
		LONG references = 1;
		WIN32_FIND_DATAW data = {};
	public:
		explicit FileSystemBindData(const std::wstring& path)
		{
			data.dwFileAttributes = FILE_ATTRIBUTE_NORMAL;
			const auto name = path.substr(path.find_last_of(L"\\/") + 1);
			wcsncpy_s(data.cFileName, name.c_str(), _TRUNCATE);
		}
		HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override
		{
			if (!result) return E_POINTER;
			*result = nullptr;
			if (iid != IID_IUnknown && iid != IID_IFileSystemBindData) return E_NOINTERFACE;
			*result = static_cast<IFileSystemBindData*>(this);
			AddRef();
			return S_OK;
		}
		ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&references); }
		ULONG STDMETHODCALLTYPE Release() override
		{
			const auto remaining = InterlockedDecrement(&references);
			if (!remaining) delete this;
			return remaining;
		}
		HRESULT STDMETHODCALLTYPE SetFindData(const WIN32_FIND_DATAW* value) override
		{
			if (!value) return E_POINTER;
			data = *value;
			return S_OK;
		}
		HRESULT STDMETHODCALLTYPE GetFindData(WIN32_FIND_DATAW* value) override
		{
			if (!value) return E_POINTER;
			*value = data;
			return S_OK;
		}
	};

	inline HRESULT CreateSaveItem(const std::wstring& path, IShellItem** item)
	{
		if (!item) return E_POINTER;
		*item = nullptr;
		if (path.empty()) return E_UNEXPECTED;
		if (GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES)
			return SHCreateItemFromParsingName(path.c_str(), nullptr, IID_PPV_ARGS(item));
		IBindCtx* context = nullptr;
		auto hr = CreateBindCtx(0, &context);
		if (FAILED(hr)) return hr;
		auto data = new FileSystemBindData(path);
		hr = context->RegisterObjectParam(const_cast<LPOLESTR>(STR_FILE_SYS_BIND_DATA), data);
		data->Release();
		if (SUCCEEDED(hr)) hr = SHCreateItemFromParsingName(path.c_str(), context, IID_PPV_ARGS(item));
		context->Release();
		return hr;
	}

	inline std::wstring EventName(const std::wstring& path)
	{
		return L"Local\\NAI.Files.Dialog." + path.substr(path.find_last_of(L"\\/") + 1);
	}

	inline std::wstring Encode(const std::wstring& text)
	{
		const auto size = WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
		std::string bytes(size, '\0');
		WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), bytes.data(), size, nullptr, nullptr);
		std::wstring hex;
		for (unsigned char byte : bytes)
		{
			hex += L"0123456789abcdef"[byte >> 4];
			hex += L"0123456789abcdef"[byte & 15];
		}
		return hex;
	}

	inline HRESULT ReadResults(const std::wstring& path, std::vector<std::wstring>& results)
	{
		results.clear();
		std::ifstream file(path);
		if (!file.good()) return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
		std::string line;
		while (std::getline(file, line))
		{
			if (line.empty()) continue;
			const int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, line.data(), static_cast<int>(line.size()), nullptr, 0);
			if (!size) return HRESULT_FROM_WIN32(ERROR_NO_UNICODE_TRANSLATION);
			std::wstring value(size, L'\0');
			MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, line.data(), static_cast<int>(line.size()), value.data(), size);
			results.push_back(value);
		}
		return results.empty() ? HRESULT_FROM_WIN32(ERROR_CANCELLED) : S_OK;
	}

	// Tests substitute only process activation; the real COM objects and transport still run.
#ifdef FILES_DIALOG_TESTING
	using LaunchCallback = HRESULT (*)(const std::wstring&, const std::wstring&);
	inline LaunchCallback TestLaunch = nullptr;
#endif

	class Request
	{
	public:
		std::wstring path;
		std::wstring traceDirectory;
		HANDLE event = nullptr;
		HANDLE process = nullptr;
		HRESULT status = S_OK;
		bool writerTrace = false;

		Request()
		{
			wchar_t temp[MAX_PATH], name[MAX_PATH];
			if (!GetTempPathW(MAX_PATH, temp) || !GetTempFileNameW(temp, L"nfd", 0, name))
			{
				status = HRESULT_FROM_WIN32(GetLastError());
				return;
			}
			path = name;
			wchar_t traceMode[16] = {};
			writerTrace = GetEnvironmentVariableW(L"NAI_FILES_DIALOG_TRACE", traceMode, _countof(traceMode)) == 6
				&& wcscmp(traceMode, L"writer") == 0;
			if (writerTrace)
			{
				// Explicit process-scoped diagnostic, never enabled by a dialog filename/URI.
				DeleteFileW(path.c_str());
				GUID id;
				status = CoCreateGuid(&id);
				if (FAILED(status)) return;
				wchar_t guid[40], expandedTemp[32768];
				StringFromGUID2(id, guid, _countof(guid));
				std::wstring token;
				for (const auto ch : guid)
				{
					if (!ch) break;
					if (ch != L'{' && ch != L'}' && ch != L'-') token += ch;
				}
				const auto expanded = GetLongPathNameW(temp, expandedTemp, _countof(expandedTemp));
				const std::wstring root = expanded ? expandedTemp : temp;
				path = root + L"nai-dialog-" + token + L".txt";
				traceDirectory = root + L"nai-dialog-trace-" + token;
				if (!CreateDirectoryW(traceDirectory.c_str(), nullptr))
				{
					status = HRESULT_FROM_WIN32(GetLastError());
					return;
				}
			}
			event = CreateEventW(nullptr, FALSE, FALSE, EventName(path).c_str());
			if (!event) status = HRESULT_FROM_WIN32(GetLastError());
		}
		~Request()
		{
			if (process) CloseHandle(process);
			if (event) CloseHandle(event);
			if (!path.empty())
			{
				DeleteFileW(path.c_str());
				DeleteFileW((path + L".pid").c_str());
				DeleteFileW((path + L".request.json").c_str());
				DeleteFileW((path + L".typeindex").c_str());
			}
		}
		Request(const Request&) = delete;
		Request& operator=(const Request&) = delete;

		HRESULT Wait()
		{
			const auto started = GetTickCount64();
			for (;;)
			{
				if (WaitForSingleObject(event, 0) == WAIT_OBJECT_0) return S_OK;
				// ShellExecute can return no handle, or a short-lived protocol broker. The
				// activated Files process identifies itself separately before loading the picker.
				if (!process)
				{
					std::ifstream pidFile(path + L".pid");
					DWORD pid = 0;
					if (pidFile >> pid && pid)
					{
						process = OpenProcess(SYNCHRONIZE | (writerTrace ? PROCESS_QUERY_LIMITED_INFORMATION : 0), FALSE, pid);
						if (!process) return WaitForSingleObject(event, 0) == WAIT_OBJECT_0
							? S_OK : HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED);
					}
				}
				HANDLE handles[] = { event, process };
				const DWORD count = process ? 2 : 1;
				const auto result = MsgWaitForMultipleObjectsEx(count, handles, 250, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
				if (result == WAIT_OBJECT_0) return S_OK;
				if (process && result == WAIT_OBJECT_0 + 1)
					return WaitForSingleObject(event, 0) == WAIT_OBJECT_0 ? S_OK : HRESULT_FROM_WIN32(ERROR_PROCESS_ABORTED);
				if (result == WAIT_FAILED) return HRESULT_FROM_WIN32(GetLastError());
				if (result == WAIT_OBJECT_0 + count)
				{
					MSG message;
					while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
					{
						if (message.message == WM_QUIT)
						{
							PostQuitMessage(static_cast<int>(message.wParam));
							return HRESULT_FROM_WIN32(ERROR_CANCELLED);
						}
						TranslateMessage(&message);
						DispatchMessageW(&message);
					}
				}
				if (!process && GetTickCount64() - started > 60000)
					return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
			}
		}
	};

	// ------------------------------------------------------------------
	// TEMPORARY diagnostics for the "SystemSettings 'Add desktop app' does
	// not open Files" falsification gate. Captures the caller's activation
	// context and the ShellExecuteExW("files-dev:...") outcome. Append-only,
	// writes to %TEMP%\nfd-show-diagnostic.txt. Remove once diagnosed.
	// ------------------------------------------------------------------
	inline void WriteActivationDiagnostic(DWORD callerPid, HWND owner, BOOL shellOk, DWORD gle,
		HANDLE hProcess, const std::wstring& uri, const std::wstring& mode, ULONGLONG t0)
	{
		// Integrity level + AppContainer presence from the caller's token.
		std::wstring integrity = L"(unknown)";
		std::wstring appContainer = L"no";
		HANDLE proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, callerPid);
		if (proc)
		{
			HANDLE token = nullptr;
			if (OpenProcessToken(proc, TOKEN_QUERY, &token))
			{
				// AppContainer?
				DWORD len = 0;
				GetTokenInformation(token, TokenAppContainerSid, nullptr, 0, &len);
				if (GetLastError() == ERROR_INSUFFICIENT_BUFFER) appContainer = L"yes";

				// Integrity level SID
				len = 0;
				if (!GetTokenInformation(token, TokenIntegrityLevel, nullptr, 0, &len) &&
					GetLastError() == ERROR_INSUFFICIENT_BUFFER && len)
				{
					std::vector<BYTE> buf(len);
					if (GetTokenInformation(token, TokenIntegrityLevel, buf.data(), len, &len))
					{
						auto* label = reinterpret_cast<TOKEN_MANDATORY_LABEL*>(buf.data());
						PSID sid = label->Label.Sid;
						DWORD subCount = *GetSidSubAuthorityCount(sid);
						DWORD rid = *GetSidSubAuthority(sid, subCount - 1);
						if (rid == 0x2000) integrity = L"Low";
						else if (rid == 0x2000 + 0x1000) integrity = L"Medium";
						else if (rid == 0x2000 + 0x2000) integrity = L"High";
						else if (rid == 0x2000 + 0x3000) integrity = L"System";
						else integrity = L"rid:" + std::to_wstring(rid);
					}
				}
				CloseHandle(token);
			}
			CloseHandle(proc);
		}

		// Package identity of the caller (empty when it has none). GetPackageFullName
		// is exported from kernel32 on Win10/11; resolve dynamically to avoid a link dep.
		std::wstring package;
		{
			typedef LONG(WINAPI* FnGetPackageFullName)(DWORD, UINT32*, PWSTR);
			auto fn = (FnGetPackageFullName)GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "GetPackageFullName");
			if (fn)
			{
				UINT32 len = 0;
				if (fn(callerPid, &len, nullptr) == ERROR_INSUFFICIENT_BUFFER && len)
				{
					std::vector<wchar_t> buf(len);
					if (fn(callerPid, &len, buf.data()) == ERROR_SUCCESS)
						package.assign(buf.data());
				}
			}
		}

		const ULONGLONG elapsed = GetTickCount64() - t0;
		std::wstring msg;
		msg += L"[nfd-diagnostic]\n";
		msg += L"  time=" + std::to_wstring(t0) + L"\n";
		msg += L"  callerPid=" + std::to_wstring(callerPid) + L"\n";
		msg += L"  package=" + package + L"\n";
		msg += L"  ownerHwnd=0x" + std::to_wstring((unsigned long long)(unsigned long)(ULONG_PTR)owner) + L"\n";
		msg += L"  integrity=" + integrity + L"\n";
		msg += L"  appContainer=" + appContainer + L"\n";
		msg += L"  mode=" + mode + L"\n";
		msg += L"  uri=" + uri + L"\n";
		msg += L"  shellOk=" + std::to_wstring(shellOk ? 1 : 0) + L"\n";
		msg += L"  gle=0x" + std::to_wstring(gle) + L"\n";
		msg += L"  hProcess=0x" + std::to_wstring((unsigned long long)(ULONG_PTR)hProcess) + L"\n";
		msg += L"  shellElapsedMs=" + std::to_wstring(elapsed) + L"\n";
		msg += L"  ---\n";
		wchar_t temp[MAX_PATH]{};
		if (GetTempPathW(MAX_PATH, temp))
		{
			const std::wstring path = std::wstring(temp) + L"nfd-show-diagnostic.txt";
			std::ofstream out(path, std::ios::app);
			for (wchar_t ch : msg) out.put((char)(ch < 0x80 ? ch : '?'));
		}
	}

	inline void TraceHResult(const std::wstring& path, const char* operation, HRESULT result)
	{
		if (!path.empty()) std::ofstream(path, std::ios::app) << operation << "=0x" << std::hex << result << '\n';
	}

	inline HRESULT Show(HWND owner, const std::wstring& mode, const std::wstring& folder,
		const std::wstring& name, std::vector<std::wstring>& results, std::wstring* diagnosticPath = nullptr,
		DialogOptions* options = nullptr)
	{
		Request request;
		if (FAILED(request.status)) return request.status;
		DialogOptions fallback;
		if (!options)
		{
			fallback.folder = folder;
			fallback.suggestedName = name;
			fallback.flags = FOS_PATHMUSTEXIST | (mode == L"save" ? FOS_OVERWRITEPROMPT : FOS_FILEMUSTEXIST);
			options = &fallback;
		}
		const auto requestFile = request.path + L".request.json";
		{
			std::ofstream metadata(requestFile, std::ios::binary);
			metadata << options->Serialize(mode);
			if (!metadata.good()) return HRESULT_FROM_WIN32(ERROR_WRITE_FAULT);
		}
		const auto trace = request.writerTrace ? request.path + L".native.trace" : std::wstring();
		if (diagnosticPath) *diagnosticPath = trace;
		std::wstring arguments = L"files-dev.exe -outputpath \"" + request.path + L"\" -mode \"" + mode + L"\"";
		arguments += L" -dialogrequest \"" + requestFile + L"\"";
		if (request.writerTrace) arguments += L" -directory \"" + request.traceDirectory + L"\"";
		if (request.writerTrace) arguments += L" -dialogtrace writer";
		HRESULT launched = S_OK;
#ifdef FILES_DIALOG_TESTING
		if (TestLaunch) launched = TestLaunch(request.path, arguments);
		else
#endif
		{
			const std::wstring uri = L"files-dev:?cmd=" + Encode(arguments);
			SHELLEXECUTEINFOW info = { sizeof(info) };
			info.fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_FLAG_NO_UI;
			info.lpFile = uri.c_str();
			info.nShow = SW_SHOW;
			const auto t0 = GetTickCount64();
			const BOOL ok = ShellExecuteExW(&info);
			const DWORD gle = ok ? ERROR_SUCCESS : GetLastError();
			WriteActivationDiagnostic(GetCurrentProcessId(), owner, ok, gle, info.hProcess, uri, mode, t0);
			if (info.hProcess) CloseHandle(info.hProcess);
			if (!ok) launched = HRESULT_FROM_WIN32(gle);
		}
		if (FAILED(launched)) return launched;
		const bool restoreOwner = owner && IsWindowEnabled(owner);
		if (restoreOwner) EnableWindow(owner, FALSE);
		const auto waited = request.Wait();
		if (restoreOwner && IsWindow(owner))
		{
			EnableWindow(owner, TRUE);
			SetForegroundWindow(owner);
		}
		auto result = FAILED(waited) ? waited : ReadResults(request.path, results);
		if (SUCCEEDED(result))
		{
			std::ifstream selectedType(request.path + L".typeindex");
			UINT index = 0;
			if (!(selectedType >> index) || index == 0 || (!options->types.empty() && index > options->types.size()))
				result = HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
			else options->typeIndex = index;
		}
		if (SUCCEEDED(result) || result == HRESULT_FROM_WIN32(ERROR_CANCELLED)) options->clearClientData = false;
		TraceHResult(trace, "transport", result);
		if (request.writerTrace && request.process)
		{
			DWORD exitCode = STILL_ACTIVE;
			if (WaitForSingleObject(request.process, 15000) == WAIT_OBJECT_0)
				GetExitCodeProcess(request.process, &exitCode);
			TraceHResult(trace, "FilesExit", static_cast<HRESULT>(exitCode));
		}
		return result;
	}
}
