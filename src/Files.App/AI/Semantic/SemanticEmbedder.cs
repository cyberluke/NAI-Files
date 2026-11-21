// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Files.App.AI.Semantic
{
	/// <summary>
	/// The embeddings plane for semantic recall. Resolution order:
	///  1. the capability-matrix binding (an OpenAI-compatible endpoint), when set;
	///  2. the LUSI engine (shared OpenVINO NPU gateway, `POST /v1/embeddings { input, operation }`);
	///  3. Ollama's OpenAI-compatible endpoint as a local fallback.
	/// Vectors are zero-padded (or truncated) to the Lattice store dimension — zero padding
	/// preserves cosine similarity exactly, so a 768-dim model can index into the 1024-dim store.
	/// </summary>
	internal static class SemanticEmbedder
	{
		private const string LusiDefaultBaseUrl = "http://127.0.0.1:27130";
		private const string OllamaBaseUrl = "http://127.0.0.1:11434/v1";
		private const int StoreDimension = 1024;

		private enum BackendKind
		{
			OpenAiCompatible,
			Lusi,
			Ollama,
		}

		private sealed record Backend(BackendKind Kind, string BaseUrl, string? Token, string? Model);

		private static readonly HttpClient http = new(new SocketsHttpHandler
		{
			UseProxy = false,
			UseCookies = false,
			AllowAutoRedirect = false,
			ConnectTimeout = TimeSpan.FromSeconds(3),
		})
		{ Timeout = TimeSpan.FromSeconds(120) };

		/// <summary>True when at least one embedding backend answers.</summary>
		public static async Task<bool> IsAvailableAsync(CancellationToken token)
			=> await ResolveAsync(token) is not null;

		/// <summary>Embeds inputs in order. `query` selects the query instruction vs document index.</summary>
		public static async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, bool query, CancellationToken token)
		{
			if (inputs.Count == 0)
				return [];

			var backend = await ResolveAsync(token)
				?? throw new InvalidOperationException("No embedding backend is available (LUSI, the configured gateway, or Ollama)");

			var vectors = backend.Kind == BackendKind.Lusi
				? await EmbedLusiAsync(backend, inputs, query, token)
				: await EmbedOpenAiAsync(backend, inputs, token);

			return NormalizeDimension(vectors);
		}

		private static async Task<Backend?> ResolveAsync(CancellationToken token)
		{
			// 1. Explicit matrix binding wins.
			if (TryResolveOpenAiEndpoint(out var endpoint))
			{
				var binding = new Backend(BackendKind.OpenAiCompatible, endpoint.BaseUrl, endpoint.ApiKey, endpoint.Model);
				if (await ProbeAsync(endpoint.BaseUrl.TrimEnd('/') + "/models", endpoint.ApiKey, token))
					return binding;
			}

			// 2. LUSI (canonical engine).
			var (lusiBaseUrl, lusiToken) = await ResolveLusiAsync(token);
			if (await ProbeAsync(lusiBaseUrl.TrimEnd('/') + "/v1/models", lusiToken, token))
				return new Backend(BackendKind.Lusi, lusiBaseUrl, lusiToken, null);

			// 3. Ollama fallback.
			var ollama = await ResolveOllamaAsync(token);
			if (ollama is not null)
				return ollama;

			return null;
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

		private static async Task<(string BaseUrl, string Token)> ResolveLusiAsync(CancellationToken token)
		{
			var active = await NaiGatewayDiscovery.ActiveAsync(token);
			return active.Lusi is { } lusi ? (lusi.BaseUrl, lusi.Token) : (LusiDefaultBaseUrl, string.Empty);
		}

		private static async Task<Backend?> ResolveOllamaAsync(CancellationToken token)
		{
			try
			{
				using var response = await http.GetAsync(OllamaBaseUrl + "/models", token);
				if (!response.IsSuccessStatusCode)
					return null;

				using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
				if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
					return null;

				string? first = null;
				var embeddingModel = default(string);
				foreach (var model in data.EnumerateArray())
				{
					if (!model.TryGetProperty("id", out var idValue) || idValue.GetString() is not { Length: > 0 } id)
						continue;

					first ??= id;
					if (id.Contains("embed", StringComparison.OrdinalIgnoreCase))
					{
						embeddingModel = id;
						break;
					}
				}

				var chosen = embeddingModel ?? first;
				return chosen is null ? null : new Backend(BackendKind.Ollama, OllamaBaseUrl, null, chosen);
			}
			catch
			{
				return null;
			}
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

		private static async Task<IReadOnlyList<float[]>> EmbedLusiAsync(Backend backend, IReadOnlyList<string> inputs, bool query, CancellationToken token)
		{
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

			using var request = new HttpRequestMessage(HttpMethod.Post, backend.BaseUrl.TrimEnd('/') + "/v1/embeddings")
			{
				Content = content,
			};
			if (!string.IsNullOrEmpty(backend.Token))
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", backend.Token);

			using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token);
			response.EnsureSuccessStatusCode();
			return await ReadEmbeddingsAsync(response, token);
		}

		private static async Task<IReadOnlyList<float[]>> EmbedOpenAiAsync(Backend backend, IReadOnlyList<string> inputs, CancellationToken token)
		{
			if (string.IsNullOrWhiteSpace(backend.Model))
				throw new InvalidOperationException("The embedding provider has no model selected");

			using var stream = new MemoryStream();
			using (var writer = new Utf8JsonWriter(stream))
			{
				writer.WriteStartObject();
				writer.WriteString("model", backend.Model);
				writer.WritePropertyName("input");
				writer.WriteStartArray();
				foreach (var text in inputs)
					writer.WriteStringValue(text);
				writer.WriteEndArray();
				writer.WriteEndObject();
			}

			using var request = new HttpRequestMessage(HttpMethod.Post, backend.BaseUrl.TrimEnd('/') + "/embeddings")
			{
				Content = new ByteArrayContent(stream.ToArray()),
			};
			request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
			if (!string.IsNullOrEmpty(backend.Token))
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", backend.Token);

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

		/// <summary>Zero-pads (or truncates) to the Lattice store dimension; padding is cosine-exact.</summary>
		private static IReadOnlyList<float[]> NormalizeDimension(IReadOnlyList<float[]> vectors)
		{
			var normalized = new List<float[]>(vectors.Count);
			foreach (var vector in vectors)
			{
				if (vector.Length == StoreDimension)
				{
					normalized.Add(vector);
					continue;
				}

				var resized = new float[StoreDimension];
				Array.Copy(vector, resized, Math.Min(vector.Length, StoreDimension));
				normalized.Add(resized);
			}
			return normalized;
		}
	}
}
