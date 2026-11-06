// Copyright (c) Files Community
// Licensed under the MIT License.
#pragma once
#include <map>
#include <set>

class DialogControlSelections
{
	std::map<DWORD, std::set<DWORD>> items;
	std::map<DWORD, DWORD> selected;
public:
	HRESULT Add(DWORD control, DWORD item)
	{
		try { return items[control].insert(item).second ? S_OK : E_INVALIDARG; }
		catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
	}
	HRESULT Remove(DWORD control, DWORD item)
	{
		const auto found = items.find(control);
		if (found == items.end() || !found->second.erase(item)) return E_INVALIDARG;
		const auto selection = selected.find(control);
		if (selection != selected.end() && selection->second == item) selected.erase(selection);
		return S_OK;
	}
	HRESULT Clear(DWORD control)
	{
		items.erase(control);
		selected.erase(control);
		return S_OK;
	}
	HRESULT Set(DWORD control, DWORD item)
	{
		const auto found = items.find(control);
		if (found == items.end() || found->second.find(item) == found->second.end()) return E_INVALIDARG;
		try { selected[control] = item; return S_OK; }
		catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
	}
	HRESULT Get(DWORD control, DWORD* item) const
	{
		if (!item) return E_POINTER;
		*item = 0;
		const auto found = selected.find(control);
		if (found == selected.end()) return E_FAIL;
		*item = found->second;
		return S_OK;
	}
};
