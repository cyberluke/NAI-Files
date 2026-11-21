// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Files.App.AI.Semantic
{
	/// <summary>
	/// The embeddings plane for semantic recall. Defaults to the LUSI engine (the shared
	/// OpenVINO NPU Qwen3 runtime, `POST /v1/embeddings { input, operation }`); when the
	/// capability matrix binds <see cref="AiCapability.Embedding"/> to an OpenAI-compatible
	/// endpoint, that endpoint is used instead (`POST {base}/embeddings { model, input }`).
	/// Vectors are always written to NAI Lattice, keeping the store and query space aligned.
	/// </summary>
	internal static class SemanticEmbedder
	{
		private const string LusiDefaultBaseUrl = "http://127.0.0.1:27130";

		private static readonly HttpClient http = new(new SocketsHttpHandler
		{
			UseProxy = false,
			UseCookies = false,
			AllowAutoRedirect = false,
			ConnectTimeout = TimeSpan.FromSeconds(3),
		})
		{ Timeout = TimeSpan.FromSeconds(120) };

		/// <summary>True when the resolved embedding backend answers a models probe.</summary>
		public static async Task<bool> IsAvailableAsync(CancellationToken token)
		{
			if (TryResolveOpenAiEndpoint(out var endpoint))
				return await ProbeAsync(endpoint.BaseUrl.TrimEnd('/') + "/models", endpoint.ApiKey, token);

			var baseUrl = await ResolveLusiBaseUrlAsync(token);
			return await ProbeAsync(baseUrl.TrimEnd('/') + "/v1/models", null, token);
		}

		/// <summary>Embeds inputs in order. `query` selects the query instruction vs document index.</summary>
		public static async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, bool query, CancellationToken token)
		{
			if (inputs.Count == 0)
				return [];

			if (TryResolveOpenAiEndpoint(out var endpoint))
				return await EmbedOpenAiAsync(endpoint, inputs, token);

			return await EmbedLusiAsync(inputs, query, token);
		}

		private static bool TryResolveOpenAiEndpoint(out AiEndpointConfig endpoint)
		{
			endpoint = AiEndpointConfig.Empty;

			var binding = AiProviderRegistry.Instance.GetBinding(AiCapability.Embedding);
			if (binding is null)
				return false;

			var provider = AiProviderRegistry.Instance.Find(binding.ProviderId);
			if (provider is null || provider.Kind is AiProviderKind.Lusi or AiProviderKind.Kelvin)
				return false;

			var config = AiProviderRegistry.Instance.GetEndpoint(binding.ProviderId);
			if (!config.IsConfigured)
				return false;

			endpoint = string.IsNullOrWhiteSpace(binding.Model) ? config : config with { Model = binding.Model };
			return true;
		}

		private static async Task<bool> ProbeAsync(string url, string? apiKey, CancellationToken token)
		{
			try
			{
				using var request = new HttpRequestMessage(HttpMethod.Get, url);
				if (!string.IsNullOrWhiteSpace(apiKey))
					request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

				using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
				return response.IsSuccessStatusCode;
			}
			catch
			{
				return false;
			}
		}

		private static async Task<string> ResolveLusiBaseUrlAsync(CancellationToken token)
		{
			var active = await NaiGatewayDiscovery.ActiveAsync(token);
			return active.Lusi?.BaseUrl ?? LusiDefaultBaseUrl;
		}

		private static async Task<IReadOnlyList<float[]>> EmbedLusiAsync(IReadOnlyList<string> inputs, bool query, CancellationToken token)
		{
			var baseUrl = await ResolveLusiBaseUrlAsync(token);

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
			return await ReadEmbeddingsAsync(response, token);
		}

		private static async Task<IReadOnlyList<float[]>> EmbedOpenAiAsync(AiEndpointConfig endpoint, IReadOnlyList<string> inputs, CancellationToken token)
		{
			if (string.IsNullOrWhiteSpace(endpoint.Model))
				throw new InvalidOperationException("The embedding provider has no model selected");

			using var stream = new MemoryStream();
			using (var writer = new Utf8JsonWriter(stream))
			{
				writer.WriteStartObject();
				writer.WriteString("model", endpoint.Model);
				writer.WritePropertyName("input");
				writer.WriteStartArray();
				foreach (var text in inputs)
					writer.WriteStringValue(text);
				writer.WriteEndArray();
				writer.WriteEndObject();
			}

			using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.BaseUrl.TrimEnd('/') + "/embeddings")
			{
				Content = new ByteArrayContent(stream.ToArray()),
			};
			request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
			if (!string.IsNullOrWhiteSpace(endpoint.ApiKey))
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);

			using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token);
			response.EnsureSuccessStatusCode();
			return await ReadEmbeddingsAsync(response, token);
		}

		private static async Task<IReadOnlyList<float[]>> ReadEmbeddingsAsync(HttpResponseMessage response, CancellationToken token)
		{
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
	}
}
