// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Text.Json;

namespace Files.App.Helpers
{
	/// <summary>
	/// Client-side semantic retrieval over the canonical LUSI Engine.
	///
	/// The LUSI box exposes OpenAI-shaped <c>embeddings</c> + <c>rerank</c>
	/// lanes but NO server-side document index (IndexedDB / Qdrant analog is not
	/// bundled). Files therefore owns the index: it persists the approved
	/// chunk inventory locally (the existing <c>NaiSemanticIndexStore</c>), embeds
	/// the active chunks through the LUSI embedding lane, embeds the query,
	/// recalls the top candidates by cosine similarity, then reranks them with
	/// the LUSI rerank lane. Result shape mirrors the Kelvin search contract so
	/// the rest of the pipeline (scoring, evidence, stale-guard) is unchanged.
	///
	/// This is the SOLID DIP replacement for the Kelvin server-side retriever:
	/// callers depend on the retrieval result, never on which engine served.
	/// </summary>
	public static class NaiLusiRetriever
	{
		private const int MaxCandidates = 128;
		/// <summary>Maximum document characters embedded by the retriever per chunk.</summary>
		public const int MaxDocumentCharacters = 6000;

		public static async Task<IReadOnlyList<NaiSemanticHit>> SearchAsync(
			NaiLusiGateway gateway, string model, string dataset, string query,
			IReadOnlyList<(string ChunkId, string FileId, string Text)> chunks, CancellationToken token)
		{
			if (gateway is null) throw new ArgumentNullException(nameof(gateway));
			if (string.IsNullOrWhiteSpace(model) || model.Length > 256) throw new ArgumentException("Semantic embedding model is required");
			if (query.Trim().Length is < 1 or > NaiSemanticProtocol.MaximumQueryLength || query.Any(char.IsControl)) throw new ArgumentException("Invalid semantic query");
			if (chunks.Count is < 1 or > 8192 || chunks.Any(chunk => !IsId(chunk.ChunkId) || !IsId(chunk.FileId) || string.IsNullOrEmpty(chunk.Text))) throw new ArgumentException("Current local index identities are required");

			// 1) Embed the query.
			string queryEmbeddingId = model;
			var queryResponse = await gateway.EmbedAsync(queryEmbeddingId, "query", new[] { query }, token);
			var queryVector = ExtractQueryVector(queryResponse);
			if (queryVector is null) throw new FormatException("Semantic embedding response has no query vector");

			// 2) Embed the active chunks in bounded batches.
			var batch = new List<string>(); var vectors = new List<float[]?>(); var ids = new List<string>(); var fileIds = new List<string>();
			IReadOnlyList<float[]?>? current = null;
			for (var index = 0; index < chunks.Count; index++)
			{
				var (chunkId, fileId, text) = chunks[index];
				batch.Add(text.Length > MaxDocumentCharacters ? text[..MaxDocumentCharacters] : text);
				if (batch.Count == 32 || index + 1 == chunks.Count)
				{
					var response = await gateway.EmbedAsync(queryEmbeddingId, "index", batch, token);
					current = ExtractVectors(response, batch);
					foreach (var vector in current ?? []) { vectors.Add(vector); ids.Add(chunkId); fileIds.Add(fileId); }
					batch.Clear();
				}
			}

			// 3) Cosine recall over active chunks, bounded candidate budget.
			var scored = new List<(int Index, double Score)>();
			for (var index = 0; index < ids.Count; index++)
			{
				var vector = vectors[index];
				if (vector is null) continue;
				var score = Cosine(queryVector, vector);
				if (double.IsFinite(score) && score > 0) scored.Add((index, score));
			}
			scored.Sort((a, b) => b.Score.CompareTo(a.Score));
			if (scored.Count > MaxCandidates) scored.RemoveRange(MaxCandidates, scored.Count - MaxCandidates);

			// 4) Rerank the recalled candidates with the LUSI rerank lane.
			var docs = scored.Select(item => chunks[item.Index].Text).ToList();
			var reranked = await gateway.RerankAsync(model, query, docs, Math.Min(scored.Count, NaiSemanticProtocol.MaximumResults), token);
			var first = new List<int>(scored.Count);
			var rerankedByDoc = ExtractRerankOrder(reranked, scored.Count);
			foreach (var position in rerankedByDoc)
			{
				if (position >= scored.Count) continue;
				var (index, recallScore) = scored[position];
				first.Add(index);
				_ = recallScore;
				if (first.Count >= NaiSemanticProtocol.MaximumResults) break;
			}
			// Any candidates rerank omitted (guard) are appended by recall score.
			foreach (var item in scored)
			{
				if (first.Count >= NaiSemanticProtocol.MaximumResults) break;
				if (!first.Contains(item.Index)) first.Add(item.Index);
			}

			// 5) Emit Kelvin-shaped hits for the existing evidence/scoring path.
			var hits = new List<NaiSemanticHit>();
			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (var index in first)
			{
				var (chunkId, fileId, text) = chunks[index];
				var evidence = text.Length > 480 ? text[..480] : text;
				if (seen.Add(fileId)) hits.Add(new NaiSemanticHit(chunkId, fileId, scored.First(item => item.Index == index).Score, evidence));
			}
			return hits;
		}

		// ------------------------------------------------------------------
		// Vector parsing
		// ------------------------------------------------------------------

		private static float[]? ExtractQueryVector(JsonElement root)
		{
			var data = ExtractVectors(root, null);
			return data is { Length: > 0 } ? data[0] : null;
		}

		private static float[]?[] ExtractVectors(JsonElement root, IReadOnlyList<string>? expected)
		{
			if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
			var rows = new List<float[]?>();
			foreach (var entry in data.EnumerateArray())
			{
				if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("embedding", out var embedding) || embedding.ValueKind != JsonValueKind.Array)
				{
					rows.Add(null); continue;
				}
				var values = new List<float>();
				foreach (var component in embedding.EnumerateArray())
				{
					if (component.ValueKind == JsonValueKind.Number && component.TryGetSingle(out var value)) values.Add(value);
					else { rows.Add(null); values = []; break; }
				}
				if (values.Count > 0) rows.Add(values.ToArray());
			}
			return rows.ToArray();
		}

		private static List<int> ExtractRerankOrder(JsonElement root, int expectedCount)
		{
			if (root.ValueKind != JsonValueKind.Object) return new List<int>();
			var results = root.TryGetProperty("results", out var resultsElement) && resultsElement.ValueKind == JsonValueKind.Array
				? resultsElement.EnumerateArray().ToList() : root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array
				? dataElement.EnumerateArray().ToList() : null;
			if (results is null) return new List<int>();
			var order = new List<int>();
			foreach (var result in results)
			{
				if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number) continue;
				var position = index.GetInt32();
				if (position >= 0 && position < expectedCount && !order.Contains(position)) order.Add(position);
			}
			return order;
		}

		private static double Cosine(float[] left, float[] right)
		{
			if (left.Length != right.Length || left.Length == 0) return 0;
			double dot = 0, a = 0, b = 0;
			for (var index = 0; index < left.Length; index++)
			{
				dot += (double)left[index] * right[index]; a += (double)left[index] * left[index]; b += (double)right[index] * right[index];
			}
			if (a == 0 || b == 0) return 0;
			return dot / (Math.Sqrt(a) * Math.Sqrt(b));
		}

		private static bool IsId(string? id) => id is { Length: > 0 and <= 256 } && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
	}
}