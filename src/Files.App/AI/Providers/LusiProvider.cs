// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.AI.Providers
{
	/// <summary>
	/// Adapter for the canonical on-device LUSI engine. Discovery, capability map and the
	/// OpenAI wire live in <see cref="NaiLusiGateway"/>; this only translates the unified
	/// <see cref="AiChatRequest"/> onto it so LUSI participates in the same ladder as every
	/// other backend.
	/// </summary>
	internal sealed class LusiProvider : IAiProvider
	{
		private static readonly IReadOnlySet<AiCapability> capabilities = new HashSet<AiCapability>
		{
			AiCapability.Chat,
			AiCapability.Vision,
			AiCapability.Ocr,
			AiCapability.Caption,
			AiCapability.Embedding,
			AiCapability.Rerank,
		};

		public string Id => AiProviderIds.Lusi;
		public string DisplayName => "LUSI Engine (local)";
		public AiProviderKind Kind => AiProviderKind.Lusi;
		public bool IsLocal => true;
		public bool RequiresConfiguration => false;
		public IReadOnlySet<AiCapability> Capabilities => capabilities;

		public async Task<AiProviderStatus> ProbeAsync(CancellationToken token)
		{
			var active = await NaiGatewayDiscovery.ActiveAsync(token);
			return active.Lusi is { } lusi
				? new AiProviderStatus(AiProviderHealth.Online, lusi.BaseUrl)
				: new AiProviderStatus(AiProviderHealth.Offline, "LUSI engine not reachable");
		}

		public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken token)
		{
			var active = await NaiGatewayDiscovery.ActiveAsync(token);
			return active.Lusi?.ModelIds("chat") ?? [];
		}

		public async Task<AiCompletion> CompleteAsync(AiChatRequest request, CancellationToken token)
		{
			var active = await NaiGatewayDiscovery.ActiveAsync(token);
			if (active.Lusi is not { } lusi)
				throw new InvalidOperationException("The LUSI engine is not reachable");

			var model = string.IsNullOrWhiteSpace(request.Model)
				? lusi.ModelIds("chat").FirstOrDefault() ?? "chat"
				: request.Model;

			var content = await lusi.ChatAsync(model, request.Instruction, request.Request, request.DisplayedContent, request.Language, token);
			return new AiCompletion(Id, model, content);
		}
	}
}
