// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Text.Json;
using Windows.Globalization;
using Windows.Storage;

namespace Files.App.Helpers
{
	/// <summary>
	/// Keeps Files in sync with the NAI OS language. The authoritative source is
	/// <c>%APPDATA%\com.naios.nai-os\settings.json</c> ("language"). The upstream Seelen UI file
	/// (<c>com.seelen.seelen-ui</c>) is only a legacy fallback — reading it made Files show a
	/// different language than NAI OS. A manual per-app choice persists in local settings and
	/// wins over NAI OS until the user turns "Follow NAI OS language" back on.
	/// </summary>
	internal static class NaiLanguageBridge
	{
		private const string ManualOverrideKey = "NaiLanguage.ManualOverride";

		private static readonly string naiOsSettingsPath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
			"com.naios.nai-os", "settings.json");

		private static readonly string seelenSettingsPath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
			"com.seelen.seelen-ui", "settings.json");

		private static FileSystemWatcher? naiOsWatcher;
		private static FileSystemWatcher? seelenWatcher;
		private static Timer? debounce;
		private static readonly Lock sync = new();
		private static string? appliedLanguage;

		public static event Action? LanguageChanged;
		public static bool IsRestartRequired { get; private set; }
		public static bool IsFollowingNaiOS { get; private set; }

		/// <summary>
		/// Records a startup phase marker into <c>nai-startup.log</c> (LocalState).
		/// Unconditional so first-run stalls can be located in release builds too.
		/// </summary>
		internal static void StartupStage(string stage)
		{
			try
			{
				File.AppendAllText(Path.Combine(ApplicationData.Current.LocalFolder.Path, "nai-startup.log"),
					$"{DateTimeOffset.UtcNow:O} {stage}{Environment.NewLine}");
			}
			catch { }
		}

		internal static string? NormalizeLanguage(string? value) => NaiLanguagePolicy.Normalize(value);

		public static void Initialize()
		{
			// A language failure must never prevent Files from starting.
			try
			{
				// A manual per-app choice wins and survives restarts.
				if (ReadManualOverride() is { } manual)
				{
					ApplicationLanguages.PrimaryLanguageOverride = manual;
					appliedLanguage = manual;
					IsFollowingNaiOS = false;
					return;
				}

				Apply(initial: true);
				Hook(naiOsSettingsPath, ref naiOsWatcher);
				Hook(seelenSettingsPath, ref seelenWatcher);
			}
			catch (Exception ex)
			{
				System.Diagnostics.Trace.WriteLine("NAI language initialization failed: " + ex);
			}
		}

		/// <summary>Re-enables following NAI OS and clears the manual override.</summary>
		public static void StartFollowing()
		{
			ClearManualOverride();
			appliedLanguage = null;
			IsFollowingNaiOS = true;
			Apply(initial: true);
			Hook(naiOsSettingsPath, ref naiOsWatcher);
			Hook(seelenSettingsPath, ref seelenWatcher);
			LanguageChanged?.Invoke();
		}

		/// <summary>Stops following NAI OS and persists the chosen language for Files only.</summary>
		public static void StopFollowing(string? languageCode)
		{
			var language = NormalizeLanguage(languageCode) ?? languageCode;
			if (string.IsNullOrEmpty(language))
				return;

			IsFollowingNaiOS = false;
			appliedLanguage = language;
			WriteManualOverride(language);
			LanguageChanged?.Invoke();
		}

		private static void Hook(string settingsPath, ref FileSystemWatcher? watcher)
		{
			if (watcher is not null)
				return;

			var directory = Path.GetDirectoryName(settingsPath);
			if (directory is null || !Directory.Exists(directory))
				return;

			try
			{
				watcher = new FileSystemWatcher(directory, "settings.json")
				{
					NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
				};
				watcher.Changed += (_, _) => ScheduleApply();
				watcher.Created += (_, _) => ScheduleApply();
				watcher.Deleted += (_, _) => ScheduleApply();
				watcher.Renamed += (_, _) => ScheduleApply();
				watcher.EnableRaisingEvents = true;
			}
			catch (Exception ex)
			{
				watcher?.Dispose();
				watcher = null;
				System.Diagnostics.Trace.WriteLine($"Unable to watch {settingsPath}: {ex.Message}");
			}
		}

		private static void ScheduleApply()
		{
			lock (sync)
			{
				debounce?.Dispose();
				debounce = new Timer(
					_ => App.UiDispatcher?.TryEnqueue(() => Apply(initial: false)),
					null, TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
			}
		}

		private static void Apply(bool initial)
		{
			// A manual choice is authoritative until the user turns following back on.
			if (!IsFollowingNaiOS && appliedLanguage is not null)
				return;

			try
			{
				// NAI OS is the source of truth; Seelen is a legacy fallback only.
				var language = ReadLanguage(naiOsSettingsPath) ?? ReadLanguage(seelenSettingsPath);
				if (language is null)
				{
					if (IsFollowingNaiOS)
					{
						IsFollowingNaiOS = false;
						LanguageChanged?.Invoke();
					}
					return;
				}

				var followingChanged = !IsFollowingNaiOS;
				IsFollowingNaiOS = true;

				if (language == appliedLanguage)
				{
					if (followingChanged)
						LanguageChanged?.Invoke();
					return;
				}

				if (initial)
					ApplicationLanguages.PrimaryLanguageOverride = language;
				else
					IsRestartRequired |= AppLanguageHelper.TryChange(language);

				appliedLanguage = language;
				LanguageChanged?.Invoke();
			}
			catch (Exception ex)
			{
				System.Diagnostics.Trace.WriteLine("Unable to read the NAI OS language: " + ex.Message);
			}
		}

		private static string? ReadLanguage(string path)
		{
			try
			{
				if (!File.Exists(path))
					return null;

				using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
				using var document = JsonDocument.Parse(reader.ReadToEnd());
				if (!document.RootElement.TryGetProperty("language", out var property) || property.ValueKind != JsonValueKind.String)
					return null;

				return NormalizeLanguage(property.GetString());
			}
			catch
			{
				return null;
			}
		}

		private static string? ReadManualOverride()
		{
			try
			{
				return ApplicationData.Current.LocalSettings.Values.TryGetValue(ManualOverrideKey, out var value)
					&& value is string code
					&& !string.IsNullOrEmpty(code)
					? NormalizeLanguage(code) ?? code
					: null;
			}
			catch
			{
				return null;
			}
		}

		private static void WriteManualOverride(string code)
		{
			try { ApplicationData.Current.LocalSettings.Values[ManualOverrideKey] = code; }
			catch { }
		}

		private static void ClearManualOverride()
		{
			try { ApplicationData.Current.LocalSettings.Values.Remove(ManualOverrideKey); }
			catch { }
		}
	}
}
