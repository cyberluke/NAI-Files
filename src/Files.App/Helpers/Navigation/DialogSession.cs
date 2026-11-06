// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Utils.Serialization;
using Microsoft.Extensions.Logging;
using System.IO;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using WinRT;

namespace Files.App.Helpers
{
	[GeneratedBindableCustomProperty]
	public sealed partial class DialogFileType
	{
		public string Name { get; set; } = string.Empty;
		public string Spec { get; set; } = "*.*";
		public override string ToString() => Name;
	}

	internal sealed class DialogRequest
	{
		public string Mode { get; set; } = "open";
		public string CallerExecutable { get; set; } = string.Empty;
		public string ClientGuid { get; set; } = string.Empty;
		public string Folder { get; set; } = string.Empty;
		public string DefaultFolder { get; set; } = string.Empty;
		public string SuggestedName { get; set; } = string.Empty;
		public string DefaultExtension { get; set; } = string.Empty;
		public string Title { get; set; } = string.Empty;
		public string OkLabel { get; set; } = string.Empty;
		public string NameLabel { get; set; } = string.Empty;
		public uint? Options { get; set; }
		public int FileTypeIndex { get; set; } = 1;
		public bool ClearClientData { get; set; }
		public DialogFileType[] FileTypes { get; set; } = [];
	}

	internal sealed class DialogClientState
	{
		public string LastFolder { get; set; } = string.Empty;
		// Classic 5:4 ("multisync") footprint; the scaler in MainWindow snaps any
		// persisted size to 5:4 against the current display's work area, so these
		// are just a fallback ~ (¼ × 4K) at 5:4.
		public int Width { get; set; } = 2560;
		public int Height { get; set; } = 2048;
		public LayoutPreferencesItem? Layout { get; set; }
	}

	internal sealed class DialogSession
	{
		public static DialogSession? Current { get; private set; }
		public static bool IsDialogLaunch { get; set; }
		public DialogRequest Request { get; }
		public DialogClientState State { get; private set; } = new();
		public string OutputPath { get; }
		private int selectedTypeIndex;
		public int FilterRevision { get; private set; }
		public int SelectedTypeIndex
		{
			get => selectedTypeIndex;
			set { if (selectedTypeIndex != value) { selectedTypeIndex = value; FilterRevision++; } }
		}
		private string? stateFile;

		private DialogSession(DialogRequest request, string outputPath)
		{
			Request = request;
			OutputPath = outputPath;
			if (request.FileTypeIndex < 1 || (request.FileTypes.Length > 0 && request.FileTypeIndex > request.FileTypes.Length))
				throw new InvalidDataException(Strings.InvalidPath.GetLocalizedResource());
			SelectedTypeIndex = request.FileTypeIndex;
			if (!string.IsNullOrWhiteSpace(request.CallerExecutable))
			{
				var key = request.CallerExecutable.ToUpperInvariant() + "|" + request.ClientGuid.ToUpperInvariant() + "|" + request.Mode;
				var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
				stateFile = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "Settings", "DialogStates", hash + ".json");
				try
				{
					if (request.ClearClientData) File.Delete(stateFile);
					else if (File.Exists(stateFile)) State = JsonSerializer.Deserialize(File.ReadAllText(stateFile), AppJsonSerializerContext.Default.DialogClientState) ?? new();
				}
				catch (Exception ex) { App.Logger.LogWarning(ex, "Could not restore caller dialog preferences"); }
			}
		}

		public static void Load(string outputPath, string requestPath)
		{
			if (Current?.OutputPath == outputPath) return;
			if (!string.Equals(Path.GetFullPath(requestPath), Path.GetFullPath(outputPath + ".request.json"), StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException(Strings.InvalidPath.GetLocalizedResource());
			if (new FileInfo(requestPath).Length > 1024 * 1024) throw new InvalidDataException(requestPath);
			var request = JsonSerializer.Deserialize(File.ReadAllText(requestPath), AppJsonSerializerContext.Default.DialogRequest)
				?? throw new InvalidDataException(requestPath);
			request.FileTypes ??= [];
			Current = new DialogSession(request, outputPath);
		}

		public string InitialFolder()
		{
			// Explicit requests are never replaced by remembered folders, even if inaccessible.
			if (!string.IsNullOrWhiteSpace(Request.Folder)) return Request.Folder;
			if (Path.IsPathFullyQualified(Request.SuggestedName))
				return Path.GetDirectoryName(Request.SuggestedName) ?? Request.DefaultFolder;
			if (Directory.Exists(State.LastFolder)) return State.LastFolder;
			if (!string.IsNullOrWhiteSpace(Request.DefaultFolder)) return Request.DefaultFolder;
			return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
		}

		public DialogFileType? SelectedType => Request.FileTypes.Length == 0 ? null
			: Request.FileTypes[Math.Clamp(SelectedTypeIndex - 1, 0, Request.FileTypes.Length - 1)];
		public bool RequiresOverwritePrompt => ((Request.Options ?? (Request.Mode == "save" ? 2u : 0u)) & 2) != 0;
		public bool StrictTypes => ((Request.Options ?? 0) & 4) != 0;

		public bool MatchesFile(string path)
		{
			return MatchesSpec(path, SelectedType?.Spec);
		}

		private static bool MatchesSpec(string path, string? spec)
		{
			if (spec is null) return true;
			var filename = Path.GetFileName(path);
			return spec.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
				.Any(mask => FileSystemName.MatchesWin32Expression(FileSystemName.TranslateWin32Expression(mask), filename, ignoreCase: true));
		}

		public string AddDefaultExtension(string input)
		{
			if (Request.Mode != "save" || string.IsNullOrWhiteSpace(input)) return input;
			var value = input.Trim().Trim('"');
			if (!string.IsNullOrEmpty(Path.GetExtension(value)) || value.EndsWith('.')) return value;
			var extension = Request.DefaultExtension.TrimStart('.');
			if (extension.Length == 0 || !MatchesFile(value + "." + extension))
			{
				extension = SelectedType?.Spec.Split(';', StringSplitOptions.TrimEntries)
					.FirstOrDefault(mask => mask.StartsWith("*.", StringComparison.Ordinal) && mask[2..].IndexOfAny(['*', '?']) < 0)?[2..] ?? string.Empty;
			}
			return extension.Length == 0 ? value : value + "." + extension;
		}

		public bool Includes(ListedItem item) => item.PrimaryItemAttribute == Windows.Storage.StorageItemTypes.Folder
			|| MatchesFile(item.ItemPath ?? item.ItemNameRaw ?? item.Name ?? string.Empty);

		public Func<ListedItem, bool> CaptureFilter()
		{
			var spec = SelectedType?.Spec;
			return item => item.PrimaryItemAttribute == Windows.Storage.StorageItemTypes.Folder
				|| MatchesSpec(item.ItemPath ?? item.ItemNameRaw ?? item.Name ?? string.Empty, spec);
		}

		public void SaveCurrent()
		{
			if (stateFile is null) return;
			try
			{
				var folder = Ioc.Default.GetRequiredService<IContentPageContext>().ShellPage?.ShellViewModel?.WorkingDirectory;
				if (!string.IsNullOrEmpty(folder) && Path.IsPathFullyQualified(folder)) State.LastFolder = folder;
				var size = MainWindow.Instance.AppWindow.Size;
				if (size.Width > 0 && size.Height > 0) { State.Width = size.Width; State.Height = size.Height; }
				Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
				var temporary = stateFile + "." + Guid.NewGuid().ToString("N");
				try
				{
					File.WriteAllText(temporary, JsonSerializer.Serialize(State, AppJsonSerializerContext.Default.DialogClientState));
					File.Move(temporary, stateFile, overwrite: true);
				}
				finally { if (File.Exists(temporary)) File.Delete(temporary); }
			}
			catch (Exception ex) { App.Logger.LogWarning(ex, "Could not persist caller dialog preferences"); }
		}

		public static LayoutPreferencesItem CloneLayout(LayoutPreferencesItem item) =>
			JsonSerializer.Deserialize(JsonSerializer.Serialize(item, AppJsonSerializerContext.Default.LayoutPreferencesItem),
				AppJsonSerializerContext.Default.LayoutPreferencesItem)!;
	}
}
