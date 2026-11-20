// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Net.Http;
using System.Net.Http.Headers;

namespace Files.App.AI.Providers
{
	/// <summary>
	/// A provider backed by any OpenAI-compatible HTTP endpoint: a user-configured gateway,
	/// or a local NPU/GPU inference server exposing the same wire. Probing issues
	/// <c>GET {base}/v1/models</c> (the base is normalized to include <c>/v1</c> when the
	/// user leaves the path off) and a completion posts the standard chat payload.
	/// </summary>
	internal sealed class OpenAiCompatibleProvider : IAiProvider
	{
		private static readonly HttpClient http = new(new SocketsHttpHandler
		{
			UseProxy = false,
			UseCookies = false,
			AllowAutoRedirect = false,
			ConnectTimeout = TimeSpan.FromSeconds(3),
		})
		{ Timeout = TimeSpan.FromSeconds(120) };

		private readonly Func<AiEndpointConfig> config;

		public OpenAiCompatibleProvider(string id, string displayName, AiProviderKind kind, bool isLocal, Func<AiEndpointConfig> config)
		{
			Id = id;
			DisplayName = displayName;
			Kind = kind;
			IsLocal = isLocal;
			this.config = config;
		}

		public string Id { get; }
		public string DisplayName { get; }
		public AiProviderKind Kind { get; }
		public bool IsLocal { get; }
		public bool RequiresConfiguration => true;

		private AiEndpointConfig Config => config();

		public async Task<AiProviderStatus> ProbeAsync(CancellationToken token)
		{
			var cfg = Config;
			if (!cfg.IsConfigured)
				return new AiProviderStatus(AiProviderHealth.NeedsConfiguration, "No endpoint configured");

			try
			{
				var models = await FetchModelsAsync(cfg, token);
				return new AiProviderStatus(AiProviderHealth.Online, $"{models.Count} model(s)");
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				return new AiProviderStatus(AiProviderHealth.Offline, ex.Message);
			}
		}

		public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken token)
		{
			var cfg = Config;
			return cfg.IsConfigured ? await FetchModelsAsync(cfg, token) : [];
		}

		public async Task<AiCompletion> CompleteAsync(AiChatRequest request, CancellationToken token)
		{
			var cfg = Config;
			if (!cfg.IsConfigured)
				throw new InvalidOperationException($"{DisplayName} has no endpoint configured");

			var model = ResolveModel(request, cfg);
			var payload = NaiAiProtocol.ChatPayload(model, request.Instruction, request.Request, request.DisplayedContent, request.Language);

			using var message = new HttpRequestMessage(HttpMethod.Post, Combine(cfg.BaseUrl, "chat/completions"))
			{
				Content = new ByteArrayContent(payload),
			};
			message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
			if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
				message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);

			using var response = await http.SendAsync(message, HttpCompletionOption.ResponseContentRead, token);
			response.EnsureSuccessStatusCode();

			using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
			var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
			return new AiCompletion(Id, model, content ?? throw new InvalidOperationException("The AI provider returned no text"));
		}

		private static string ResolveModel(AiChatRequest request, AiEndpointConfig cfg)
		{
			var model = string.IsNullOrWhiteSpace(request.Model) ? cfg.Model : request.Model;
			if (string.IsNullOrWhiteSpace(model))
				throw new InvalidOperationException("No model selected for this AI provider");
			return model;
		}

		private static async Task<IReadOnlyList<string>> FetchModelsAsync(AiEndpointConfig cfg, CancellationToken token)
		{
			using var message = new HttpRequestMessage(HttpMethod.Get, Combine(cfg.BaseUrl, "models"));
			if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
				message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);

			using var response = await http.SendAsync(message, HttpCompletionOption.ResponseContentRead, token);
			response.EnsureSuccessStatusCode();

			using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
			if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
				return [];

			var models = new List<string>();
			foreach (var model in data.EnumerateArray())
			{
				if (model.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } value)
					models.Add(value);
			}
			return models.Distinct(StringComparer.Ordinal).Take(500).ToArray();
		}

		/// <summary>Joins the OpenAI base with a route, normalizing a bare host to <c>.../v1</c>.</summary>
		private static string Combine(string baseUrl, string route)
		{
			var normalized = NormalizeBase(baseUrl);
			return $"{normalized}/{route}";
		}

		internal static string NormalizeBase(string baseUrl)
		{
			var url = baseUrl.Trim().TrimEnd('/');
			if (url.Length == 0)
				return url;

			// Append the conventional /v1 segment when the user typed only scheme+host (+port).
			var candidate = url.Contains("://", StringComparison.Ordinal) ? url : "http://" + url;
			if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
				return url;

			return string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/"
				? url + "/v1"
				: url;
		}
	}
}
