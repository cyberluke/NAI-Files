// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.AI.Providers
{
	/// <summary>
	/// Adapter for the Kelvin AI Workstation cloud fallback. Pairing and the HTTP wire stay in
	/// <see cref="NaiAiClient"/>; this exposes Kelvin as ladder provider that is skipped
	/// automatically when the workstation is not paired.
	/// </summary>
	internal sealed class KelvinProvider : IAiProvider
	{
		private static readonly IReadOnlySet<AiCapability> capabilities = new HashSet<AiCapability>
		{
			AiCapability.Chat,
			AiCapability.Vision,
			AiCapability.Ocr,
			AiCapability.Caption,
		};

		public string Id => AiProviderIds.Kelvin;
		public string DisplayName => "Kelvin AI Workstation";
		public AiProviderKind Kind => AiProviderKind.Kelvin;
		public bool IsLocal => false;
		public bool RequiresConfiguration => false;
		public IReadOnlySet<AiCapability> Capabilities => capabilities;

		public Task<AiProviderStatus> ProbeAsync(CancellationToken token)
			=> Task.FromResult(NaiAiClient.HasKelvinCredential
				? new AiProviderStatus(AiProviderHealth.Online, "Paired")
				: new AiProviderStatus(AiProviderHealth.NeedsConfiguration, "Not paired"));

		public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken token)
			=> NaiAiClient.HasKelvinCredential ? await NaiAiClient.KelvinModelsAsync(token) : [];

		public async Task<AiCompletion> CompleteAsync(AiChatRequest request, CancellationToken token)
		{
			if (!NaiAiClient.HasKelvinCredential)
				throw new InvalidOperationException("Kelvin AI Workstation is not paired");

			var model = request.Model;
			if (string.IsNullOrWhiteSpace(model))
			{
				var models = await NaiAiClient.KelvinModelsAsync(token);
				model = models.FirstOrDefault() ?? throw new InvalidOperationException("No Kelvin model is available");
			}

			var payload = NaiAiProtocol.ChatPayload(model, request.Instruction, request.Request, request.DisplayedContent, request.Language);
			var content = await NaiAiClient.CompleteChatAsync(payload, token);
			return new AiCompletion(Id, model, content);
		}
	}
}
