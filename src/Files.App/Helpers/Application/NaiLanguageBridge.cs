// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Text.Json;
using Windows.Globalization;

namespace Files.App.Helpers
{
	internal static class NaiLanguageBridge
	{
		private static FileSystemWatcher? watcher;
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
				File.AppendAllText(Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "nai-startup.log"),
					$"{DateTimeOffset.UtcNow:O} {stage}{Environment.NewLine}");
			}
			catch { }
		}

		internal static string? NormalizeLanguage(string? value) => NaiLanguagePolicy.Normalize(value);

		public static void Initialize()
		{
			var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				"AppData", "Roaming", "com.seelen.seelen-ui", "settings.json");
			Apply(settingsPath, initial: true);
			var directory = Path.GetDirectoryName(settingsPath)!;
			if (!Directory.Exists(directory))
				return;

			try
			{
				watcher = new FileSystemWatcher(directory, "settings.json")
				{
					NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
				};
				watcher.Changed += Changed;
				watcher.Created += Changed;
				watcher.Deleted += Changed;
				watcher.Renamed += Changed;
				watcher.EnableRaisingEvents = true;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				watcher?.Dispose();
				watcher = null;
				System.Diagnostics.Trace.WriteLine("Unable to watch NAI OS language: " + ex.Message);
			}

			void Changed(object sender, FileSystemEventArgs args)
			{
				lock (sync)
				{
					debounce?.Dispose();
					debounce = new Timer(_ => App.UiDispatcher?.TryEnqueue(() => Apply(settingsPath, initial: false)),
						null, TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
				}
			}
		}

		private static void Apply(string settingsPath, bool initial)
		{
			try
			{
				if (!File.Exists(settingsPath))
				{
					if (IsFollowingNaiOS) { IsFollowingNaiOS = false; LanguageChanged?.Invoke(); }
					return;
				}
				using var stream = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
				using var document = JsonDocument.Parse(reader.ReadToEnd());
				if (!document.RootElement.TryGetProperty("language", out var property) || property.ValueKind != JsonValueKind.String)
					return;
				var language = NormalizeLanguage(property.GetString());
				if (language is null)
				{
					if (IsFollowingNaiOS) { IsFollowingNaiOS = false; LanguageChanged?.Invoke(); }
					return;
				}
				var followingChanged = !IsFollowingNaiOS;
				IsFollowingNaiOS = true;
				if (language == appliedLanguage)
				{
					if (followingChanged) LanguageChanged?.Invoke();
					return;
				}
				if (initial)
					ApplicationLanguages.PrimaryLanguageOverride = language;
				else
					IsRestartRequired |= AppLanguageHelper.TryChange(language);
				appliedLanguage = language;
				LanguageChanged?.Invoke();
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
			{
				System.Diagnostics.Trace.WriteLine("Unable to read NAI OS language: " + ex.Message);
			}
		}
	}
}
