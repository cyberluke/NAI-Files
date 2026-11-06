// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Files.App.Helpers
{
	public sealed partial record NaiImageAnalysis
	{
		public int SchemaVersion { get; init; } = 1;
		public string Path { get; init; } = string.Empty;
		public string ContentSha256 { get; init; } = string.Empty;
		public long Length { get; init; }
		public long LastWriteUtcTicks { get; init; }
		public string Model { get; init; } = string.Empty;
		public string Language { get; init; } = string.Empty;
		public string PromptVersion { get; init; } = "nai-vision-v1";
		public DateTimeOffset AnalyzedAt { get; init; }
		public string Caption { get; init; } = string.Empty;
		public string EnglishCaption { get; init; } = string.Empty;
		public string[] Tags { get; init; } = [];
		public string[] EnglishTags { get; init; } = [];
		public string Text { get; init; } = string.Empty;
		public string PreviewFile { get; init; } = string.Empty;
	}

	[JsonSerializable(typeof(NaiImageAnalysis))]
	public partial class NaiVisionJsonContext : JsonSerializerContext;

	/// <summary>Private per-user cache; search never reads image contents or calls a model.</summary>
	public sealed class NaiVisionCache(string directory)
	{
		private readonly SemaphoreSlim writes = new(1, 1);
		public string DirectoryPath { get; } = directory;
		public static string Identity(string path, string hash, string model, string language) =>
			Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant() + "\n" + hash + "\n" + model + "\n" + language + "\nnai-vision-v1"))).ToLowerInvariant();

		public async Task SaveAsync(NaiImageAnalysis entry, CancellationToken token)
		{
			if (entry.SchemaVersion != 1 || entry.ContentSha256.Length != 64 || !entry.ContentSha256.All(Uri.IsHexDigit) ||
				!System.IO.Path.IsPathFullyQualified(entry.Path) || string.IsNullOrWhiteSpace(entry.Model))
				throw new InvalidDataException("Invalid analysis identity");
			var file = System.IO.Path.Combine(DirectoryPath, Identity(entry.Path, entry.ContentSha256, entry.Model, entry.Language) + ".json");
			await writes.WaitAsync(token);
			try
			{
				Directory.CreateDirectory(DirectoryPath);
				var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
				try
				{
					await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(entry, NaiVisionJsonContext.Default.NaiImageAnalysis), new UTF8Encoding(false), token);
					File.Move(temporary, file, overwrite: true);
				}
				finally { if (File.Exists(temporary)) File.Delete(temporary); }
			}
			finally { writes.Release(); }
		}

		public async Task<IReadOnlyList<NaiImageAnalysis>> ReadAsync(string? folder, CancellationToken token)
		{
			if (!Directory.Exists(DirectoryPath)) return [];
			return await Task.Run(async () =>
			{
				var result = new List<NaiImageAnalysis>();
				foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json").Take(50000))
				{
					token.ThrowIfCancellationRequested();
					try
					{
						if (new FileInfo(file).Length > 65536) continue;
						var entry = JsonSerializer.Deserialize(await File.ReadAllTextAsync(file, token), NaiVisionJsonContext.Default.NaiImageAnalysis);
						if (entry is null || entry.SchemaVersion != 1 || string.IsNullOrWhiteSpace(entry.Path) || !System.IO.Path.IsPathFullyQualified(entry.Path) ||
							entry.ContentSha256 is null || entry.ContentSha256.Length != 64 || !entry.ContentSha256.All(Uri.IsHexDigit) ||
							string.IsNullOrWhiteSpace(entry.Model) || entry.Tags is null || entry.EnglishTags is null ||
							entry.Tags.Length > 12 || entry.EnglishTags.Length > 12 || entry.Caption is null || entry.EnglishCaption is null ||
							entry.Text is null || entry.PreviewFile is null) continue;
						if (folder is null || string.Equals(System.IO.Path.GetDirectoryName(entry.Path)?.TrimEnd('\\', '/'), folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) result.Add(entry);
					}
					catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
				}
				return (IReadOnlyList<NaiImageAnalysis>)result.OrderByDescending(entry => entry.AnalyzedAt).ToArray();
			}, token);
		}

		public static IReadOnlyList<NaiImageAnalysis> Search(IEnumerable<NaiImageAnalysis> entries, string query, string? tag = null, int limit = 200)
			=> Search(entries, (string[])[query], tag, limit);

		public static IReadOnlyList<NaiImageAnalysis> Latest(IEnumerable<NaiImageAnalysis> entries) => entries
			.OrderByDescending(entry => entry.AnalyzedAt)
			.ThenBy(entry => entry.Model, StringComparer.Ordinal)
			.DistinctBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToArray();

		public static IReadOnlyList<NaiImageAnalysis> Search(IEnumerable<NaiImageAnalysis> entries, IEnumerable<string> queries, string? tag = null, int limit = 200)
		{
			var normalized = queries.Take(4).Select(query => NaiImageSearchIntent.Normalize(query.Trim()[..Math.Min(query.Trim().Length, 256)]))
				.Distinct(StringComparer.Ordinal).ToArray();
			if (normalized.Length == 0) return [];
			// Choose the latest analysis before filtering: a better match in an old
			// content version must never resurrect a replaced image's description.
			return Latest(entries).Where(entry => tag is null || entry.Tags.Concat(entry.EnglishTags).Contains(tag, StringComparer.OrdinalIgnoreCase))
				.Select(entry => (Entry: entry, Score: normalized.Max(query => Score(entry, query))))
				.Where(match => match.Score >= 0)
				.OrderByDescending(match => match.Score).ThenByDescending(match => match.Entry.AnalyzedAt)
				.Take(Math.Clamp(limit, 1, 1000)).Select(match => match.Entry).ToArray();
		}

		private static int Score(NaiImageAnalysis entry, string query)
		{
			if (query.Length == 0) return 0;
			var corpus = NaiImageSearchIntent.Normalize(System.IO.Path.GetFileName(entry.Path) + " " + entry.Caption + " " + entry.EnglishCaption + " " +
				string.Join(" ", entry.Tags) + " " + string.Join(" ", entry.EnglishTags) + " " + entry.Text);
			if (corpus.Contains(query, StringComparison.Ordinal)) return 1000;
			var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			var matches = words.Count(word => corpus.Contains(word, StringComparison.Ordinal));
			return matches == words.Length ? 500 + matches : -1;
		}

		public static IReadOnlyList<(NaiImageAnalysis Entry, string[] SharedTags)> Related(IEnumerable<NaiImageAnalysis> entries, NaiImageAnalysis source)
		{
			var tags = source.EnglishTags.Select(NaiImageSearchIntent.Normalize).ToHashSet(StringComparer.Ordinal);
			return Latest(entries).Where(entry => !entry.Path.Equals(source.Path, StringComparison.OrdinalIgnoreCase))
				.Select(entry => (Entry: entry, SharedTags: entry.EnglishTags.Where(tag => tags.Contains(NaiImageSearchIntent.Normalize(tag))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()))
				.Where(match => match.SharedTags.Length > 0).OrderByDescending(match => match.SharedTags.Length)
				.ThenByDescending(match => match.Entry.AnalyzedAt).Take(40).ToArray();
		}
	}
}
