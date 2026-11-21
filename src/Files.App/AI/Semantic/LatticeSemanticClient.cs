// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Files.App.AI.Semantic
{
	/// <summary>One semantic point to upsert: a chunk id, its embedding and its text.</summary>
	internal sealed record SemanticIndexPoint(string Id, float[] Vector, string Title, string Body, string Namespace);

	/// <summary>A vector hit from Lattice (chunk id + cosine score).</summary>
	internal sealed record SemanticVectorHit(string Id, float Score);

	/// <summary>
	/// The Files → NAI Lattice capability client. Lattice owns the vectors (`lattice-vector`
	/// HNSW) and the text index; its NAF/1 pipe is BLAKE3-framed, so Files uses the host's
	/// loopback JSON capability bridge (same runtime dispatch boundary) instead of
	/// re-implementing the framing. Payloads are written with <see cref="Utf8JsonWriter"/>
	/// so the path stays trim/AOT clean.
	/// </summary>
	internal static class LatticeSemanticClient
	{
		private const string BridgeUrl = "http://127.0.0.1:8789";

		private static readonly HttpClient http = new(new SocketsHttpHandler
		{
			UseProxy = false,
			UseCookies = false,
			AllowAutoRedirect = false,
			ConnectTimeout = TimeSpan.FromSeconds(2),
		})
		{ Timeout = TimeSpan.FromSeconds(120) };

		private static readonly string tokenPath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"NAI", "Lattice", "v1", "json-bridge.token");

		/// <summary>True when the Lattice host and its JSON capability bridge are reachable.</summary>
		public static async Task<bool> IsAvailableAsync(CancellationToken token)
		{
			try
			{
				using var response = await http.GetAsync(BridgeUrl + "/health", token);
				return response.IsSuccessStatusCode;
			}
			catch
			{
				return false;
			}
		}

		public static async Task IndexBatchAsync(IReadOnlyList<SemanticIndexPoint> points, CancellationToken token)
		{
			if (points.Count == 0)
				return;

			using var stream = new MemoryStream();
			using (var writer = new Utf8JsonWriter(stream))
			{
				writer.WriteStartObject();
				writer.WriteString("method", "semantic.index_batch");
				writer.WriteString("producer", "files");
				writer.WritePropertyName("args");
				writer.WriteStartObject();
				writer.WritePropertyName("items");
				writer.WriteStartArray();
				foreach (var point in points)
				{
					writer.WriteStartObject();
					writer.WriteString("id", point.Id);
					writer.WritePropertyName("vector");
					writer.WriteStartArray();
					foreach (var value in point.Vector)
						writer.WriteNumberValue(value);
					writer.WriteEndArray();
					writer.WriteString("title", point.Title);
					writer.WriteString("body", point.Body);
					writer.WriteString("namespace", point.Namespace);
					writer.WriteEndObject();
				}
				writer.WriteEndArray();
				writer.WriteEndObject();
				writer.WriteEndObject();
			}

			using var _ = await DispatchAsync(stream.ToArray(), token);
		}

		public static async Task<IReadOnlyList<SemanticVectorHit>> QueryVectorAsync(float[] vector, int limit, CancellationToken token)
		{
			using var stream = new MemoryStream();
			using (var writer = new Utf8JsonWriter(stream))
			{
				writer.WriteStartObject();
				writer.WriteString("method", "query.vector");
				writer.WriteString("producer", "files");
				writer.WritePropertyName("args");
				writer.WriteStartObject();
				writer.WritePropertyName("vector");
				writer.WriteStartArray();
				foreach (var value in vector)
					writer.WriteNumberValue(value);
				writer.WriteEndArray();
				writer.WriteNumber("limit", Math.Clamp(limit, 1, 512));
				writer.WriteEndObject();
				writer.WriteEndObject();
			}

			using var document = await DispatchAsync(stream.ToArray(), token);
			var hits = new List<SemanticVectorHit>();
			if (document.RootElement.TryGetProperty("hits", out var array) && array.ValueKind == JsonValueKind.Array)
			{
				foreach (var hit in array.EnumerateArray())
				{
					var id = hit.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
					var score = hit.TryGetProperty("score", out var scoreValue) ? scoreValue.GetSingle() : 0f;
					if (!string.IsNullOrEmpty(id))
						hits.Add(new SemanticVectorHit(id, score));
				}
			}
			return hits;
		}

		public static async Task DeleteAsync(IEnumerable<string> ids, CancellationToken token)
		{
			var list = ids.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToArray();
			if (list.Length == 0)
				return;

			using var stream = new MemoryStream();
			using (var writer = new Utf8JsonWriter(stream))
			{
				writer.WriteStartObject();
				writer.WriteString("method", "semantic.delete");
				writer.WriteString("producer", "files");
				writer.WritePropertyName("args");
				writer.WriteStartObject();
				writer.WritePropertyName("ids");
				writer.WriteStartArray();
				foreach (var id in list)
					writer.WriteStringValue(id);
				writer.WriteEndArray();
				writer.WriteEndObject();
				writer.WriteEndObject();
			}

			using var _ = await DispatchAsync(stream.ToArray(), token);
		}

		private static async Task<JsonDocument> DispatchAsync(byte[] payload, CancellationToken token)
		{
			var tokenValue = ReadToken()
				?? throw new InvalidOperationException("NAI Lattice bridge token is missing; is the Lattice host running?");

			using var content = new ByteArrayContent(payload);
			content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

			using var request = new HttpRequestMessage(HttpMethod.Post, BridgeUrl + "/dispatch") { Content = content };
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenValue);

			using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token);
			response.EnsureSuccessStatusCode();

			var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
			var status = document.RootElement.TryGetProperty("status", out var statusValue) ? statusValue.GetInt32() : -1;
			if (status != 0)
			{
				var error = document.RootElement.TryGetProperty("error", out var errorValue)
					? errorValue.GetString()
					: "unknown NAI Lattice error";
				document.Dispose();
				throw new InvalidOperationException(error);
			}
			return document;
		}

		private static string? ReadToken()
		{
			try
			{
				var value = File.ReadAllText(tokenPath).Trim();
				return value.Length == 0 ? null : value;
			}
			catch
			{
				return null;
			}
		}
	}
}
