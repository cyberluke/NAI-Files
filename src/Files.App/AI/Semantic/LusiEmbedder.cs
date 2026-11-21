// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Files.App.AI.Semantic
{
	/// <summary>
	/// The embeddings plane of the LUSI engine (the shared OpenVINO NPU Qwen3 runtime).
	/// Lattice federates the same gateway: `POST /v1/embeddings { input, operation }` returns
	/// `{ data: [{ index, embedding }] }`. Files embeds with LUSI directly so the vector and
	/// the query live in the same space Lattice indexes.
	/// </summary>
	internal static class LusiEmbedder
	{
		private const string FallbackBaseUrl = "http://127.0.0.1:27130";

		private static readonly HttpClient http = new(new SocketsHttpHandler
		{
			UseProxy = false,
			UseCookies = false,
			AllowAutoRedirect = false,
			ConnectTimeout = TimeSpan.FromSeconds(3),
		})
		{ Timeout = TimeSpan.FromSeconds(120) };

		/// <summary>Embeds inputs in order. `query` selects the query instruction vs document index.</summary>
		public static async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, bool query, CancellationToken token)
		{
			if (inputs.Count == 0)
				return [];

			var baseUrl = await ResolveBaseUrlAsync(token);

			using var stream = new MemoryStream();
			using (var writer = new Utf8JsonWriter(stream))
			{
				writer.WriteStartObject();
				writer.WriteString("operation", query ? "query" : "index");
				writer.WritePropertyName("input");
				writer.WriteStartArray();
				foreach (var text in inputs)
					writer.WriteStringValue(text);
				writer.WriteEndArray();
				writer.WriteEndObject();
			}

			using var content = new ByteArrayContent(stream.ToArray());
			content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

			using var response = await http.PostAsync(baseUrl.TrimEnd('/') + "/v1/embeddings", content, token);
			response.EnsureSuccessStatusCode();

			using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
			var data = document.RootElement.GetProperty("data");
			var vectors = new List<float[]>(data.GetArrayLength());
			foreach (var item in data.EnumerateArray())
			{
				var embedding = item.GetProperty("embedding");
				var vector = new float[embedding.GetArrayLength()];
				var index = 0;
				foreach (var value in embedding.EnumerateArray())
					vector[index++] = value.GetSingle();
				vectors.Add(vector);
			}
			return vectors;
		}

		private static async Task<string> ResolveBaseUrlAsync(CancellationToken token)
		{
			var active = await NaiGatewayDiscovery.ActiveAsync(token);
			return active.Lusi?.BaseUrl ?? FallbackBaseUrl;
		}
	}
}
