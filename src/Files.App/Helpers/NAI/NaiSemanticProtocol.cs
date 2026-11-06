// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Files.App.Helpers
{
	public sealed record NaiSemanticChunk(string Id, string FileId, string Name, string Text);
	public sealed record NaiSemanticHit(string ChunkId, string FileId, double Score, string Evidence);

	/// <summary>Bounded Files-owned retrieval contract. Backend text never supplies a filesystem destination.</summary>
	public static class NaiSemanticProtocol
	{
		public const string Profile = "app-nai-files";
		public const string Prefix = "semantic:";
		public const string EmbeddingModel = "Qwen3-Embedding-0.6B-int4";
		public const int MaximumResults = 50;
		public const int MaximumQueryLength = 512;

		public static bool TryParse(string? value, out string query)
		{
			value = NaiFileSearch.StripFilesPrefix(value ?? string.Empty).TrimStart();
			query = string.Empty;
			if (!value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
			query = value[Prefix.Length..].Trim();
			return true;
		}

		public static string CanonicalFolder(string folder)
		{
			if (!Path.IsPathFullyQualified(folder) || folder.StartsWith("\\\\", StringComparison.Ordinal))
				throw new ArgumentException("Semantic indexing requires an explicitly selected local folder");
			return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
		}

		public static string Dataset(string folder) => "files-v1-" + Digest(CanonicalFolder(folder).ToUpperInvariant());
		public static string FileId(string relativePath) => "file-" + Digest(relativePath.Replace('/', '\\').ToUpperInvariant());
		private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

		public static byte[] RegistrationPayload() => Write(json =>
		{
			json.WriteString("app_id", Profile); json.WriteString("display_name", "NAI Files");
		});

		public static byte[] SearchPayload(string dataset, string query, IReadOnlyList<string> activeChunkIds, int limit = MaximumResults)
		{
			if (query.Trim().Length is < 1 or > MaximumQueryLength || query.Any(char.IsControl)) throw new ArgumentException("Invalid semantic query");
			if (activeChunkIds.Count is < 1 or > 8192 || activeChunkIds.Any(id => !IsId(id))) throw new ArgumentException("Current local index identities are required");
			return Write(json =>
			{
				Scope(json, dataset); json.WriteString("query", query.Trim());
				json.WriteNumber("limit", Math.Clamp(limit, 1, MaximumResults));
				json.WriteBoolean("rerank", true); json.WriteNumber("rerank_top_n", Math.Clamp(limit, 1, MaximumResults));
				json.WriteStartObject("filter"); json.WriteStartArray("must"); json.WriteStartObject(); json.WriteString("key", "source_id");
				json.WriteStartObject("match"); json.WriteStartArray("any");
				foreach (var id in activeChunkIds) json.WriteStringValue(id);
				json.WriteEndArray(); json.WriteEndObject(); json.WriteEndObject(); json.WriteEndArray(); json.WriteEndObject();
			});
		}

		public static byte[] UpsertPayload(string dataset, IReadOnlyList<NaiSemanticChunk> chunks)
		{
			if (chunks.Count is < 1 or > 24) throw new ArgumentException("Index batch exceeds its budget");
			return Write(json =>
			{
				Scope(json, dataset); json.WriteStartArray("items");
				foreach (var chunk in chunks)
				{
					if (!IsId(chunk.Id) || !IsId(chunk.FileId) || string.IsNullOrWhiteSpace(chunk.Text) || chunk.Text.Length > 6000 || chunk.Name.Length > 512)
						throw new ArgumentException("Invalid semantic index chunk");
					json.WriteStartObject(); json.WriteString("id", chunk.Id); json.WriteString("text", chunk.Text);
					json.WriteStartObject("payload"); json.WriteString("file_id", chunk.FileId); json.WriteString("name", chunk.Name);
					json.WriteString("source_kind", "files-document"); json.WriteEndObject(); json.WriteEndObject();
				}
				json.WriteEndArray();
			});
		}

		public static byte[] DeletePayload(string dataset, IReadOnlyList<string> ids)
		{
			// An empty IDs array means dataset-wide deletion in the owner API.
			if (ids.Count is < 1 or > 500 || ids.Any(id => !IsId(id))) throw new ArgumentException("Exact nonempty index IDs are required");
			return Write(json =>
			{
				Scope(json, dataset); json.WriteStartArray("ids");
				foreach (var id in ids) json.WriteStringValue(id);
				json.WriteEndArray();
			});
		}

		public static IReadOnlyList<NaiSemanticHit> ParseSearch(JsonElement root, string dataset)
		{
			// Device is not part of the retrieval protocol: Kelvin serves this
			// profile's model (same IR, same vectors) on whatever execution device
			// it placed, and the response intentionally carries no device field.
			if (Text(root, "app_id", 128) != Profile || Text(root, "profile", 128) != Profile
				|| Text(root, "model", 256) != EmbeddingModel || !root.GetProperty("instruction_applied").GetBoolean())
				throw new FormatException("Semantic response has the wrong profile or model");
			var results = root.GetProperty("results");
			if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() > MaximumResults) throw new FormatException("Semantic result budget exceeded");
			if (results.GetArrayLength() > 0 && (root.GetProperty("rerank_seconds").ValueKind != JsonValueKind.Number || root.GetProperty("rerank_seconds").GetDouble() < 0))
				throw new FormatException("Semantic results were not reranked");
			var hits = new List<NaiSemanticHit>(); var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (var result in results.EnumerateArray())
			{
				var payload = result.GetProperty("payload");
				if (Text(payload, "app_id", 128) != Profile || Text(payload, "dataset", 128) != dataset || Text(payload, "source_kind", 64) != "files-document")
					throw new FormatException("Semantic result escaped the selected index scope");
				var id = Text(result, "id", 256); var fileId = Text(payload, "file_id", 256); var score = result.GetProperty("score").GetDouble();
				if (!IsId(id) || !IsId(fileId) || !double.IsFinite(score)) throw new FormatException("Invalid semantic result identity or relevance");
				var evidence = payload.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? string.Empty : string.Empty;
				if (seen.Add(fileId)) hits.Add(new(id, fileId, score, evidence[..Math.Min(evidence.Length, 480)]));
			}
			return hits;
		}

		private static bool IsId(string? id) => id is { Length: > 0 and <= 256 } && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
		private static string Text(JsonElement root, string property, int limit) => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
			&& value.GetString() is { } text && text.Length <= limit ? text : throw new FormatException("Invalid semantic response field");
		private static void Scope(Utf8JsonWriter json, string dataset)
		{
			if (!dataset.StartsWith("files-v1-", StringComparison.Ordinal) || dataset.Length != 73 || !dataset[9..].All(Uri.IsHexDigit))
				throw new ArgumentException("Invalid Files-owned dataset");
			json.WriteString("app_id", Profile); json.WriteString("dataset", dataset);
		}
		private static byte[] Write(Action<Utf8JsonWriter> body)
		{
			using var stream = new MemoryStream();
			using (var json = new Utf8JsonWriter(stream)) { json.WriteStartObject(); body(json); json.WriteEndObject(); }
			return stream.ToArray();
		}
	}
}
