// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.AI
{
	/// <summary>
	/// The single seam every AI backend implements. Files features depend only on this
	/// interface and the <see cref="AiProviderRegistry"/>; the concrete wire (LUSI, Kelvin,
	/// an OpenAI-compatible gateway, a local NPU/GPU lane) never leaks into feature code.
	/// </summary>
	public interface IAiProvider
	{
		/// <summary>Stable id used by the priority ladder and persistence (for example "lusi").</summary>
		string Id { get; }

		/// <summary>Localized-ish display name shown in the settings list.</summary>
		string DisplayName { get; }

		/// <summary>The backend family.</summary>
		AiProviderKind Kind { get; }

		/// <summary>True for on-device engines (LUSI, local NPU/GPU), false for cloud backends.</summary>
		bool IsLocal { get; }

		/// <summary>True when the user must configure an endpoint before this provider can run.</summary>
		bool RequiresConfiguration { get; }

		/// <summary>Probes reachability without changing state.</summary>
		Task<AiProviderStatus> ProbeAsync(CancellationToken token);

		/// <summary>Lists the chat-capable model ids this provider currently exposes.</summary>
		Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken token);

		/// <summary>Runs one chat completion. Throws when the provider cannot answer.</summary>
		Task<AiCompletion> CompleteAsync(AiChatRequest request, CancellationToken token);
	}
}
