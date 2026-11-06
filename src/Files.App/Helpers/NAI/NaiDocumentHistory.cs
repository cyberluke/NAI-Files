// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Windows.Storage;

namespace Files.App.Helpers
{
	internal static class NaiDocumentHistory
	{
		internal sealed record Entry(DateTimeOffset LastOpened, int Count);
		private static readonly HashSet<string> documentExtensions = new(StringComparer.OrdinalIgnoreCase)
		{
			".md", ".markdown", ".txt", ".pdf", ".doc", ".docx", ".odt", ".rtf", ".xlsx", ".xls", ".pptx", ".ppt", ".csv", ".epub",
		};
		private static readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
		private static readonly SemaphoreSlim writer = new(1, 1);
		private static readonly string storePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "nai-document-history.json");
		private static readonly Lazy<Task> initialization = new(() => Task.Run(() =>
		{
			ReadStore(entries);
			Changed?.Invoke(null);
		}));
		public static event Action<string?>? Changed;
		public static Task EnsureLoadedAsync() => initialization.Value;

		public static bool IsDocument(string? path) => !string.IsNullOrWhiteSpace(path) && documentExtensions.Contains(Path.GetExtension(path));
		public static Entry? Get(string? path)
		{
			_ = initialization.Value;
			return path is not null && entries.TryGetValue(Normalize(path), out var entry) ? entry : null;
		}

		public static async Task RecordOpenedAsync(string? path)
		{
			if (!IsDocument(path) || !Path.IsPathFullyQualified(path!))
				return;
			await UpdateAsync(Normalize(path!), markOpened: true);
		}

		public static Task MarkUnopenedAsync(string path) => UpdateAsync(Normalize(path), markOpened: false);

		private static async Task UpdateAsync(string path, bool markOpened)
		{
			await initialization.Value;
			await writer.WaitAsync();
			try
			{
				await Task.Run(() =>
				{
					// Multiple Files windows can have separate processes. Merge under a
					// session mutex before atomic replacement, not across an await.
					using var mutex = new Mutex(false, "Local\\NAI-Files-DocumentHistory");
					var acquired = false;
					try
					{
						try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
						catch (AbandonedMutexException) { acquired = true; }
						if (!acquired) throw new IOException("Document history is busy");
						var merged = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
						ReadStore(merged, strict: true);
						if (markOpened)
						{
							merged.TryGetValue(path, out var previous);
							merged[path] = new(DateTimeOffset.UtcNow, Math.Min((previous?.Count ?? 0) + 1, 1000000));
						}
						else merged.TryRemove(path, out _);

						var retained = merged.OrderByDescending(pair => pair.Value.LastOpened).Take(10000).ToArray();
						var temporary = storePath + "." + Environment.ProcessId + ".tmp";
						try
						{
							using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
							using (var json = new Utf8JsonWriter(stream))
							{
								json.WriteStartObject();
								foreach (var (key, value) in retained)
								{
									json.WritePropertyName(key);
									json.WriteStartObject();
									json.WriteString("lastOpened", value.LastOpened);
									json.WriteNumber("count", value.Count);
									json.WriteEndObject();
								}
								json.WriteEndObject();
							}
							File.Move(temporary, storePath, overwrite: true);
						}
						finally { if (File.Exists(temporary)) File.Delete(temporary); }
						entries.Clear();
						foreach (var pair in retained) entries[pair.Key] = pair.Value;
					}
					finally { if (acquired) mutex.ReleaseMutex(); }
				});
				Changed?.Invoke(path);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
			{
				// Failed history persistence must not turn a successful document launch
				// into a failed open or falsely display a persisted-read badge.
				App.Logger.LogWarning(ex, "Cannot persist document open history");
			}
			finally { writer.Release(); }
		}

		private static void ReadStore(ConcurrentDictionary<string, Entry> target, bool strict = false)
		{
			if (!File.Exists(storePath)) return;
			try
			{
				using var file = new FileStream(storePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				using var document = JsonDocument.Parse(file);
				if (document.RootElement.ValueKind != JsonValueKind.Object)
					throw new JsonException("Document history must be an object");
				foreach (var property in document.RootElement.EnumerateObject().Take(10000))
				{
					if (property.Value.ValueKind != JsonValueKind.Object) continue;
					if (property.Value.TryGetProperty("lastOpened", out var date) && date.ValueKind == JsonValueKind.String
						&& date.TryGetDateTimeOffset(out var opened) && property.Value.TryGetProperty("count", out var count)
						&& count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var n))
						target[property.Name] = new(opened, Math.Max(1, n));
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
			{
				if (strict) throw;
				App.Logger.LogWarning(ex, "Cannot load document open history");
			}
		}

		private static string Normalize(string path) => path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
			? @"\\" + path[8..]
			: path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
	}
}
