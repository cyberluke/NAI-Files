// Copyright (c) Files Community
// Licensed under the MIT License.
#pragma once
#include <atlbase.h>
#include <shobjidl.h>
#include <map>
#include <sstream>
#include <string>
#include <vector>

struct DialogFileType
{
	std::wstring name;
	std::wstring spec;
};

struct DialogOptions
{
	std::vector<DialogFileType> types;
	UINT typeIndex = 1;
	DWORD flags = 0;
	std::wstring title, extension, clientGuid, folder, defaultFolder, suggestedName, okLabel, nameLabel;
	bool clearClientData = false;

	static HRESULT Text(std::wstring& destination, LPCWSTR value) noexcept
	{
		if (!value) return E_INVALIDARG;
		try { destination = value; return S_OK; }
		catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
	}
	static std::wstring Folder(IShellItem* item)
	{
		if (!item) return {};
		ATL::CComHeapPtr<wchar_t> path;
		if (FAILED(item->GetDisplayName(SIGDN_FILESYSPATH, &path))) return {};
		return static_cast<LPCWSTR>(path);
	}

	HRESULT SetTypes(UINT count, const COMDLG_FILTERSPEC* values)
	{
		if (count && !values) return E_POINTER;
		if (count > 4096) return E_INVALIDARG;
		try
		{
			std::vector<DialogFileType> next;
			for (UINT i = 0; i < count; ++i)
			{
				if (!values[i].pszName || !values[i].pszSpec) return E_INVALIDARG;
				next.push_back({ values[i].pszName, values[i].pszSpec });
			}
			types.swap(next);
			return S_OK;
		}
		catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
	}

	static std::string Json(const std::wstring& value)
	{
		const auto size = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
		std::string bytes(size, '\0');
		WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), bytes.data(), size, nullptr, nullptr);
		std::string result = "\"";
		for (unsigned char c : bytes)
		{
			if (c == '"' || c == '\\') { result += '\\'; result += c; }
			else if (c < 32) { result += "\\u00"; result += "0123456789abcdef"[c >> 4]; result += "0123456789abcdef"[c & 15]; }
			else result += c;
		}
		return result + '"';
	}

	std::string Serialize(const std::wstring& mode) const
	{
		wchar_t executable[32768] = {};
		GetModuleFileNameW(nullptr, executable, _countof(executable));
		std::ostringstream json;
		json << "{\"Mode\":" << Json(mode) << ",\"CallerExecutable\":" << Json(executable)
			<< ",\"ClientGuid\":" << Json(clientGuid) << ",\"Options\":" << flags
			<< ",\"FileTypeIndex\":" << typeIndex << ",\"DefaultExtension\":" << Json(extension)
			<< ",\"Title\":" << Json(title) << ",\"Folder\":" << Json(folder)
			<< ",\"DefaultFolder\":" << Json(defaultFolder) << ",\"SuggestedName\":" << Json(suggestedName)
			<< ",\"OkLabel\":" << Json(okLabel) << ",\"NameLabel\":" << Json(nameLabel)
			<< ",\"ClearClientData\":" << (clearClientData ? "true" : "false") << ",\"FileTypes\":[";
		for (size_t i = 0; i < types.size(); ++i)
		{
			if (i) json << ',';
			json << "{\"Name\":" << Json(types[i].name) << ",\"Spec\":" << Json(types[i].spec) << '}';
		}
		json << "]}";
		return json.str();
	}
};

class DialogEventSubscriptions
{
	std::map<DWORD, ATL::CComPtr<IFileDialogEvents>> sinks;
	DWORD nextCookie = 1;
public:
	HRESULT Advise(IFileDialogEvents* sink, DWORD* cookie)
	{
		if (!cookie) return E_POINTER;
		*cookie = 0;
		if (!sink) return E_POINTER;
		try
		{
			while (!nextCookie || sinks.count(nextCookie)) ++nextCookie;
			const auto value = nextCookie++;
			sinks.emplace(value, sink);
			*cookie = value;
			return S_OK;
		}
		catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
	}
	HRESULT Unadvise(DWORD cookie) { return sinks.erase(cookie) ? S_OK : E_INVALIDARG; }
	void Clear() { sinks.clear(); }
	HRESULT FileOk(IFileDialog* dialog)
	{
		// A callback may Unadvise itself. Keep each sink alive for this dispatch.
		const auto snapshot = sinks;
		HRESULT result = S_OK;
		for (const auto& entry : snapshot)
		{
			const auto hr = entry.second->OnFileOk(dialog);
			if (hr == S_FALSE) return S_FALSE;
			if (FAILED(hr)) result = hr; // Diagnostic only; only S_FALSE vetoes acceptance.
		}
		return result;
	}
};
