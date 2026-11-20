// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.AI.Providers;

namespace Files.App.AI
{
	/// <summary>
	/// The single routing surface for Files AI work. It owns the ordered ladder of
	/// <see cref="IAiProvider"/> backends (LUSI → Kelvin → configured OpenAI-compatible gateway →
	/// local NPU → local GPU by default) and tries them in the user's priority order until one
	/// answers. Every AI feature depends on this registry, never on a concrete engine, so the
	/// routing can be reconfigured in Settings without touching feature code.
	/// </summary>
	public sealed class AiProviderRegistry
	{
		public static AiProviderRegistry Instance { get; } = new();

		private readonly object gate = new();
		private readonly AiProviderSettings settings = new();
		private readonly List<IAiProvider> providers = new();
		private readonly List<string> priority = new();

		private AiProviderRegistry()
		{
			providers.Add(new LusiProvider());
			providers.Add(new KelvinProvider());
			providers.Add(new OpenAiCompatibleProvider(
				AiProviderIds.OpenAiCompatible, "OpenAI-compatible gateway", AiProviderKind.OpenAiCompatible,
				isLocal: false, () => settings.GetEndpoint(AiProviderIds.OpenAiCompatible)));
			providers.Add(new OpenAiCompatibleProvider(
				AiProviderIds.NpuLocal, "Local NPU", AiProviderKind.NpuLocal,
				isLocal: true, () => settings.GetEndpoint(AiProviderIds.NpuLocal)));
			providers.Add(new OpenAiCompatibleProvider(
				AiProviderIds.GpuLocal, "Local GPU", AiProviderKind.GpuLocal,
				isLocal: true, () => settings.GetEndpoint(AiProviderIds.GpuLocal)));

			ReloadPriority();
		}

		/// <summary>All known providers, in registration (canonical) order.</summary>
		public IReadOnlyList<IAiProvider> Providers => providers;

		/// <summary>Provider ids in the user's current priority order.</summary>
		public IReadOnlyList<string> Priority
		{
			get { lock (gate) return priority.ToArray(); }
		}

		public IAiProvider? Find(string id) => providers.FirstOrDefault(p => p.Id == id);

		/// <summary>True when a provider participates in routing (defaults: LUSI + Kelvin).</summary>
		public bool IsEnabled(string id) => settings.GetEnabled(id, id is AiProviderIds.Lusi or AiProviderIds.Kelvin);

		public void SetEnabled(string id, bool enabled) => settings.SetEnabled(id, enabled);

		public AiEndpointConfig GetEndpoint(string id) => settings.GetEndpoint(id);

		/// <summary>Saves an endpoint and (re)enables its provider so it takes effect immediately.</summary>
		public void SetEndpoint(string id, string? baseUrl, string? apiKey, string? model)
		{
			settings.SetEndpoint(id, baseUrl, apiKey, model);
			if (!string.IsNullOrWhiteSpace(baseUrl))
				settings.SetEnabled(id, true);
		}

		/// <summary>Replaces the whole ladder (used by the drag-reorder settings list).</summary>
		public void SetPriority(IEnumerable<string> ids)
		{
			var ordered = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			lock (gate)
			{
				priority.Clear();
				priority.AddRange(Normalize(ordered));
				settings.SetPriority(priority);
			}
		}

		/// <summary>Moves one provider to a new index in the ladder.</summary>
		public void Move(string id, int newIndex)
		{
			lock (gate)
			{
				var current = priority.IndexOf(id);
				if (current < 0)
					return;
				newIndex = Math.Clamp(newIndex, 0, priority.Count - 1);
				if (newIndex == current)
					return;
				priority.RemoveAt(current);
				priority.Insert(newIndex, id);
				settings.SetPriority(priority);
			}
		}

		/// <summary>Reloads the persisted ladder (after settings changed elsewhere).</summary>
		public void ReloadPriority()
		{
			lock (gate)
			{
				priority.Clear();
				priority.AddRange(Normalize(settings.GetPriority()));
			}
		}

		private IEnumerable<string> Normalize(IEnumerable<string> ordered)
		{
			// Keep every registered provider exactly once: stored order first, then any newcomers.
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var id in ordered)
			{
				var provider = providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
				if (provider is not null && seen.Add(provider.Id))
					yield return provider.Id;
			}
			foreach (var provider in providers)
			{
				if (seen.Add(provider.Id))
					yield return provider.Id;
			}
		}

		/// <summary>
		/// Tries each enabled provider in priority order and returns the first completion. Providers
		/// that cannot answer (offline, unpaired, unconfigured) are skipped; the last error is surfaced
		/// only when the whole ladder is exhausted.
		/// </summary>
		public async Task<AiCompletion> CompleteAsync(AiChatRequest request, CancellationToken token)
		{
			Exception? lastError = null;
			foreach (var id in Priority)
			{
				if (!IsEnabled(id))
					continue;
				var provider = Find(id);
				if (provider is null)
					continue;

				try
				{
					return await provider.CompleteAsync(request, token);
				}
				catch (OperationCanceledException) when (token.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception ex)
				{
					lastError = ex;
				}
			}

			throw lastError ?? new InvalidOperationException("No AI provider is available");
		}
	}
}
