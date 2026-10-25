// Copyright (c) Files Community
// Licensed under the MIT License.
//
// NAI OS integration: desktop-wide icon theme.
//
// NAI OS (Seelen UI fork) exports its active icon packs as a flat, pre-rasterised
// icon theme (see `src/background/resources/icon_theme.rs` in the NAI OS repo):
//
//   %LOCALAPPDATA%\com.seelen.seelen-ui\icon-theme\theme.json
//   %LOCALAPPDATA%\com.seelen.seelen-ui\icon-theme\png\<size>\<id>.png
//
// Like a KDE icon theme, the same icons the shell shows for apps and file types
// are applied to the file manager: folders, known user folders, file extensions
// and executables resolve through the theme before falling back to the Windows
// shell icon. Thumbnails are never replaced, so pictures and videos keep their
// previews exactly like Dolphin does.

using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.IO;

namespace Files.App.Utils.Storage
{
	public static class NaiIconThemeHelper
	{
		private const string ThemeDirName = "icon-theme";
		private const string ThemeFileName = "theme.json";
		private const string EnvOverride = "NAI_ICON_THEME_DIR";

		private static readonly Lock _loadLock = new();
		private static readonly ConcurrentDictionary<string, byte[]?> _pngCache = new();
		private static readonly string _themeDir = ResolveThemeDir();

		private static FileSystemWatcher? _watcher;
		private static Timer? _reloadTimer;
		private static volatile Theme? _theme;
		private static bool _initialized;

		private sealed class Theme
		{
			public bool Enabled;
			public string PngDir = "png";
			public int[] Sizes = [];
			public string? Folder;
			public string? Missing;
			public Dictionary<string, string> Folders = new(StringComparer.OrdinalIgnoreCase);
			public Dictionary<string, string> FolderNames = new(StringComparer.OrdinalIgnoreCase);
			public List<string> FolderVariants = [];
			public Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase);
			public Dictionary<string, string> Apps = new(StringComparer.OrdinalIgnoreCase);
		}

		/// <summary>
		/// True when a NAI OS icon theme is present and enabled.
		/// </summary>
		public static bool IsEnabled
		{
			get
			{
				EnsureInitialized();
				return _theme?.Enabled == true;
			}
		}

		/// <summary>
		/// Fires after the theme was reloaded from disk (icon pack switched in NAI OS).
		/// </summary>
		public static event Action? ThemeChanged;

		/// <summary>
		/// Returns the themed icon (PNG bytes) for a folder or file, or null when the
		/// theme does not cover it and the shell icon should be used instead.
		/// </summary>
		/// <param name="path">File system path (may be a dummy per-extension probe path).</param>
		/// <param name="size">Requested pixel size; the smallest theme size that is not smaller is returned.</param>
		/// <param name="isFolder">Whether the item is a folder.</param>
		public static byte[]? GetIcon(string? path, int size, bool isFolder)
		{
			EnsureInitialized();
			var theme = _theme;
			if (theme is null || !theme.Enabled || string.IsNullOrWhiteSpace(path))
				return null;

			string? id = null;

			// Browsable archives (zip, 7z, ...) are listed as folders by Files; they are
			// files for the theme so they get their archive icon, not the folder one.
			if (isFolder && Path.GetExtension(path) is { Length: > 1 } archiveExtension
				&& theme.Extensions.ContainsKey(archiveExtension.TrimStart('.'))
				&& File.Exists(path))
			{
				isFolder = false;
			}

			if (isFolder)
			{
				var normalized = path.TrimEnd('\\', '/');
				if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal))
					normalized = normalized[4..];

				// Drive roots and network shares keep their shell (device) icons; the
				// theme covers folders only.
				var root = Path.GetPathRoot(normalized)?.TrimEnd('\\', '/');
				if (string.IsNullOrEmpty(normalized) || string.Equals(root, normalized, StringComparison.OrdinalIgnoreCase))
					return null;

				if (!theme.Folders.TryGetValue(normalized, out id))
				{
					// Semantic glyphs by folder name ("github", "development", "cloud",
					// "downloads", ...) beat the random color variant so a folder called
					// "GitHub" always keeps its GitHub glyph wherever it lives.
					var name = Path.GetFileName(normalized);
					if (string.IsNullOrEmpty(name) || !theme.FolderNames.TryGetValue(name, out id))
					{
						// Deterministic color variation for generic folders: the same
						// path always resolves to the same variant (stable FNV-1a, not
						// the randomized string hash).
						var variants = theme.FolderVariants;
						if (variants.Count > 0)
							id = variants[(int)(StableHash(normalized) % (ulong)variants.Count)];
						else
							id = theme.Folder;
					}
				}
			}
			else
			{
				var extension = Path.GetExtension(path);
				if (string.IsNullOrEmpty(extension))
					return null;

				if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
				{
					// Apps are keyed by executable file name (`firefox.exe`), the way the
					// NAI OS shell resolves them; unknown executables keep their own icon.
					var fileName = Path.GetFileName(path);
					if (!theme.Apps.TryGetValue(fileName, out id))
						return null;
				}
				else if (!theme.Extensions.TryGetValue(extension.TrimStart('.'), out id))
				{
					return null;
				}
			}

			return id is null ? null : LoadPng(theme, id, size);
		}

		/// <summary>
		/// Whether the shell would normally show a generated thumbnail for this file
		/// (pictures, videos, PDFs). For those the theme is only a fallback.
		/// </summary>
		public static bool PrefersThumbnail(string? path)
		{
			var extension = Path.GetExtension(path);
			return Files.Shared.Helpers.FileExtensionHelpers.IsImageFile(extension)
				|| Files.Shared.Helpers.FileExtensionHelpers.IsVideoFile(extension)
				|| string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// Stable FNV-1a 64-bit hash (case-insensitive). Unlike
		/// <see cref="string.GetHashCode"/> this is identical across processes and
		/// restarts, so folder color variants stay deterministic per path.
		/// </summary>
		private static ulong StableHash(string value)
		{
			const ulong offset = 14695981039346656037UL;
			const ulong prime = 1099511628211UL;
			ulong hash = offset;
			foreach (var c in value)
			{
				hash ^= char.ToLowerInvariant(c);
				hash *= prime;
			}
			return hash;
		}

		private static string ResolveThemeDir()
		{
			var overridden = Environment.GetEnvironmentVariable(EnvOverride);
			if (!string.IsNullOrWhiteSpace(overridden))
				return overridden;

			var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			return Path.Combine(localAppData, "com.seelen.seelen-ui", ThemeDirName);
		}

		private static void EnsureInitialized()
		{
			if (_initialized)
				return;

			lock (_loadLock)
			{
				if (_initialized)
					return;

				_initialized = true;
				Reload();

				try
				{
					// The theme directory may not exist yet (NAI OS not installed / first start);
					// watch the parent so the theme is picked up as soon as it is written.
					var watchDir = Directory.Exists(_themeDir)
						? _themeDir
						: Path.GetDirectoryName(_themeDir.TrimEnd('\\'));

					if (watchDir is not null && Directory.Exists(watchDir))
					{
						_watcher = new FileSystemWatcher(watchDir)
						{
							IncludeSubdirectories = watchDir != _themeDir,
							NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
							Filter = "*.json",
							EnableRaisingEvents = true,
						};
						_watcher.Changed += OnThemeFileChanged;
						_watcher.Created += OnThemeFileChanged;
						_watcher.Renamed += OnThemeFileChanged;
					}
				}
				catch (Exception ex)
				{
					App.Logger.LogWarning(ex, "NAI icon theme: could not watch {Dir}", _themeDir);
				}
			}
		}

		private static void OnThemeFileChanged(object sender, FileSystemEventArgs e)
		{
			if (!string.Equals(Path.GetFileName(e.FullPath), ThemeFileName, StringComparison.OrdinalIgnoreCase))
				return;

			// NAI OS writes `theme.json.tmp` and renames it; debounce the burst of events.
			lock (_loadLock)
			{
				_reloadTimer?.Dispose();
				_reloadTimer = new Timer(_ =>
				{
					Reload();
					try
					{
						Ioc.Default.GetService<IIconCacheService>()?.Clear();
					}
					catch (Exception ex)
					{
						App.Logger.LogDebug(ex, "NAI icon theme: icon cache clear failed");
					}
					ThemeChanged?.Invoke();
				}, null, 500, Timeout.Infinite);
			}
		}

		private static void Reload()
		{
			var file = Path.Combine(_themeDir, ThemeFileName);
			Theme? parsed = null;

			try
			{
				if (File.Exists(file))
					parsed = Parse(File.ReadAllBytes(file));
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "NAI icon theme: failed to read {File}", file);
			}

			_pngCache.Clear();
			_theme = parsed;

			if (parsed is not null)
				App.Logger.LogInformation(
					"NAI icon theme loaded: enabled={Enabled}, {Extensions} extensions, {Apps} apps, {Folders} known folders, {FolderNames} semantic names, {Variants} color variants",
					parsed.Enabled, parsed.Extensions.Count, parsed.Apps.Count, parsed.Folders.Count, parsed.FolderNames.Count, parsed.FolderVariants.Count);
		}

		private static Theme Parse(byte[] json)
		{
			// Manual JsonDocument parsing: no reflection-based serialization, so this stays
			// trimming/AOT friendly like the rest of Files.App.
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			var theme = new Theme
			{
				Enabled = root.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True,
				Folder = GetString(root, "folder"),
				Missing = GetString(root, "missing"),
			};

			if (root.TryGetProperty("pngDir", out var pngDir) && pngDir.ValueKind == JsonValueKind.String)
				theme.PngDir = pngDir.GetString() ?? "png";

			if (root.TryGetProperty("sizes", out var sizes) && sizes.ValueKind == JsonValueKind.Array)
				theme.Sizes = sizes.EnumerateArray()
					.Where(s => s.ValueKind == JsonValueKind.Number)
					.Select(s => s.GetInt32())
					.OrderBy(s => s)
					.ToArray();

			ReadMap(root, "folders", theme.Folders);
			ReadMap(root, "folderNames", theme.FolderNames);
			ReadMap(root, "extensions", theme.Extensions);
			ReadMap(root, "apps", theme.Apps);

			if (root.TryGetProperty("folderVariants", out var variants) && variants.ValueKind == JsonValueKind.Array)
			{
				foreach (var v in variants.EnumerateArray())
				{
					if (v.ValueKind == JsonValueKind.String && v.GetString() is { } value)
						theme.FolderVariants.Add(value);
				}
			}
			return theme;
		}

		private static string? GetString(JsonElement root, string name)
			=> root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

		private static void ReadMap(JsonElement root, string name, Dictionary<string, string> target)
		{
			if (!root.TryGetProperty(name, out var map) || map.ValueKind != JsonValueKind.Object)
				return;

			foreach (var property in map.EnumerateObject())
			{
				if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } value)
					target[property.Name] = value;
			}
		}

		private static byte[]? LoadPng(Theme theme, string id, int size)
		{
			var chosen = 0;
			foreach (var s in theme.Sizes)
			{
				chosen = s;
				if (s >= size)
					break;
			}
			if (chosen == 0)
				return null;

			var key = $"{id}:{chosen}";
			return _pngCache.GetOrAdd(key, _ =>
			{
				var file = Path.Combine(_themeDir, theme.PngDir, chosen.ToString(), id + ".png");
				try
				{
					return File.Exists(file) ? File.ReadAllBytes(file) : null;
				}
				catch (Exception ex)
				{
					App.Logger.LogDebug(ex, "NAI icon theme: failed to read {File}", file);
					return null;
				}
			});
		}
	}
}