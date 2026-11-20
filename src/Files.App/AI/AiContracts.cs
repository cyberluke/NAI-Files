// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.AI
{
	/// <summary>
	/// The kinds of inference backends Files can route AI work to. Every backend — the
	/// canonical local LUSI engine, the Kelvin AI Workstation cloud fallback, any
	/// OpenAI-compatible gateway the user configures, and the local NPU/GPU lanes — is
	/// exposed through the same <see cref="IAiProvider"/> seam and ordered by a single,
	/// user-editable priority ladder.
	/// </summary>
	public enum AiProviderKind
	{
		/// <summary>Canonical on-device LUSI engine (NPU/GPU, auto-discovered).</summary>
		Lusi,
		/// <summary>Kelvin AI Workstation (paired cloud fallback).</summary>
		Kelvin,
		/// <summary>User-configured OpenAI-compatible gateway.</summary>
		OpenAiCompatible,
		/// <summary>Local NPU inference lane exposed over an OpenAI-compatible endpoint.</summary>
		NpuLocal,
		/// <summary>Local GPU inference lane exposed over an OpenAI-compatible endpoint.</summary>
		GpuLocal,
	}

	/// <summary>Reachability of a provider at the moment it was probed.</summary>
	public enum AiProviderHealth
	{
		/// <summary>Has not been probed yet.</summary>
		Unknown,
		/// <summary>Answered a probe successfully.</summary>
		Online,
		/// <summary>Answered but is unreachable / errored.</summary>
		Offline,
		/// <summary>Requires endpoint/credential configuration before it can be used.</summary>
		NeedsConfiguration,
	}

	/// <summary>The result of probing a provider (health plus a short human-readable detail).</summary>
	public sealed record AiProviderStatus(AiProviderHealth Health, string? Detail);

	/// <summary>
	/// A backend-agnostic chat request. The fields mirror the prompt contract the existing
	/// document assistant already uses, so every adapter produces the same instructions and
	/// the same untrusted-data guard regardless of the wire underneath.
	/// </summary>
	public sealed record AiChatRequest(
		string Model,
		string Instruction,
		string Request,
		string DisplayedContent,
		string Language);

	/// <summary>A completed chat, tagged with the provider and model that answered.</summary>
	public sealed record AiCompletion(string ProviderId, string Model, string Content);

	/// <summary>Endpoint configuration for an OpenAI-compatible provider.</summary>
	public sealed record AiEndpointConfig(string BaseUrl, string ApiKey, string Model)
	{
		public static readonly AiEndpointConfig Empty = new(string.Empty, string.Empty, string.Empty);

		public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
	}
}
