// Copyright (c) Files Community
// Licensed under the MIT License.

// Self-healing COM dialog registration for NAI OS.
//
// NAI OS normally maintains the per-user CLSID shadows that route
// `CoCreateInstance(CLSID_FileOpenDialog/FileSaveDialog)` to Files' dialog
// servers, but it only does that while the NAI OS shell process is running.
// A bare sideload of Files, or Files being updated without NAI OS running,
// could therefore leave the picker unregistered — apps would silently fall
// back to (or fail against) the system dialog and the Files picker would
// never open.
//
// This helper makes the registration self-sufficient: every time the Files app
// starts it (idempotently, cheaply, and without ever failing the launch) copies
// its own dialog COM DLLs out of the package into LocalState and re-points the
// CLSID shadows at them, so the native Open/Save integration keeps working
// regardless of whether NAI OS is running.
//
// The user's "replacement enabled" preference, owned by NAI OS, is honored via
// `HKCU\Software\NAI OS\Files\Dialogs`: `OpenEnabled` / `SaveEnabled` DWORDs
// (absent = enabled). When the user turns a replacement off in NAI OS that flag
// is written as 0, and this helper leaves the corresponding shadow untouched so
// the system dialog is restored.

using System;
using System.IO;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Files.App.Helpers;

internal static class DialogRegistrationSelfHeal
{
	private const string ClsidOpen = "{DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7}";
	private const string ClsidSave = "{C0B4E2F3-BA21-4773-8DBA-335EC946EB8B}";
	private const string DialogsKey = @"Software\NAI OS\Files\Dialogs";
	private const string DialogAssetDir = "FilesOpenDialog";

	private static readonly string[] Assets =
	{
		"Files.App.OpenDialog64.dll",
		"Files.App.SaveDialog64.dll",
		"Files.App.LegacyDialog64.dll",
		"Files.App.LegacyDialog32.dll",
		"Files.App.LegacyHookHost32.exe",
	};

	/// <summary>
	/// Stages this package's dialog DLLs into LocalState and (re)binds the
	/// per-user CLSID shadows that route native Open/Save dialogs to Files.
	/// Safe to call on every launch; it is a no-op when nothing needs to change.
	/// </summary>
	internal static void EnsureRegistration()
	{
		Package package;
		try
		{
			package = Package.Current;
		}
		catch (InvalidOperationException)
		{
			// Unpackaged dev run: nothing to self-register.
			return;
		}

		try
		{
			var installedRoot = package.InstalledLocation.Path;
			var packageFullName = package.Id.FullName;
			var sourceDir = Path.Combine(installedRoot, "Assets", DialogAssetDir);
			if (!Directory.Exists(sourceDir))
				return;

			var stagingDir = Path.Combine(
				Windows.Storage.ApplicationData.Current.LocalFolder.Path,
				DialogAssetDir,
				packageFullName);

			if (!TryStageAssets(sourceDir, stagingDir))
				return;

			var (openEnabled, saveEnabled) = ReadEnabledFlags();

			if (openEnabled)
				WriteClsid(ClsidOpen, Path.Combine(stagingDir, "Files.App.OpenDialog64.dll"), "FilesOpenDialog class");
			if (saveEnabled)
				WriteClsid(ClsidSave, Path.Combine(stagingDir, "Files.App.SaveDialog64.dll"), "FilesSaveDialog class");
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine("Dialog registration self-heal failed: " + ex.Message);
		}
	}

	private static bool TryStageAssets(string sourceDir, string stagingDir)
	{
		try
		{
			Directory.CreateDirectory(stagingDir);
			foreach (var asset in Assets)
			{
				var source = Path.Combine(sourceDir, asset);
				var dest = Path.Combine(stagingDir, asset);
				if (!File.Exists(source))
					continue;
				if (File.Exists(dest) && FilesEqual(source, dest))
					continue;
				// Copy via a temporary name so a dialog DLL that is currently
				// loaded by a caller is replaced atomically next launch.
				var tmp = dest + ".pending";
				File.Copy(source, tmp, true);
				File.Move(tmp, dest, true);
			}
			return true;
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine("Staging dialog assets failed: " + ex.Message);
			return false;
		}
	}

	private static bool FilesEqual(string left, string right)
	{
		var a = new FileInfo(left);
		var b = new FileInfo(right);
		return a.Length == b.Length && a.LastWriteTimeUtc == b.LastWriteTimeUtc;
	}

	/// <summary>True when an explicit 0 has been written (replacement turned off in NAI OS).</summary>
	private static (bool open, bool save) ReadEnabledFlags()
	{
		var openEnabled = true;
		var saveEnabled = true;
		try
		{
			using var key = Registry.CurrentUser.OpenSubKey(DialogsKey);
			if (key is not null)
			{
				if (key.GetValue("OpenEnabled") is int openRaw)
					openEnabled = openRaw != 0;
				if (key.GetValue("SaveEnabled") is int saveRaw)
					saveEnabled = saveRaw != 0;
			}
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine("Reading dialog flags failed: " + ex.Message);
		}
		return (openEnabled, saveEnabled);
	}

	private static void WriteClsid(string clsid, string dllPath, string className)
	{
		using var classesKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CLSID", true);
		using var clsidKey = classesKey.CreateSubKey(clsid, true);
		clsidKey.SetValue("NaiOsOwner", "nai-os");
		clsidKey.SetValue(string.Empty, className);
		using var inProc = clsidKey.CreateSubKey("InprocServer32", true);
		inProc.SetValue(string.Empty, dllPath);
		inProc.SetValue("ThreadingModel", "Apartment");
	}
}