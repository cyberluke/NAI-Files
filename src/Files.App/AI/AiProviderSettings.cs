// Copyright (c) Files Community
// Licensed under the MIT License.

using Windows.Security.Credentials;
using Windows.Storage;

namespace Files.App.AI
{
	/// <summary>
	/// Persists the AI provider configuration: the priority ladder, per-provider enablement and
	/// each OpenAI-compatible endpoint's URL/model. API keys never go to settings — they live in
	/// the Windows credential vault, matching how the Kelvin pairing secret is handled.
	/// </summary>
	internal sealed class AiProviderSettings
	{
		private const string ContainerName = "FilesApp.AI";
		private const string VaultResource = "NAI.Files.AiGateway";
		private const string PriorityKey = "priority";
		private const string EnabledPrefix = "enabled.";
		private const string UrlPrefix = "url.";
		private const string ModelPrefix = "model.";

		/// <summary>Canonical order used when nothing has been saved yet.</summary>
		public static readonly string[] DefaultPriority =
		[
			AiProviderIds.Lusi,
			AiProviderIds.Kelvin,
			AiProviderIds.OpenAiCompatible,
			AiProviderIds.NpuLocal,
			AiProviderIds.GpuLocal,
		];

		private static ApplicationDataContainer? TryContainer()
		{
			try
			{
				return ApplicationData.Current.LocalSettings.CreateContainer(ContainerName, ApplicationDataCreateDisposition.Always);
			}
			catch
			{
				return null;
			}
		}

		public IReadOnlyList<string> GetPriority()
		{
			var container = TryContainer();
			if (container is not null && container.Values.TryGetValue(PriorityKey, out var raw) && raw is string stored && stored.Length > 0)
			{
				var parsed = stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (parsed.Length > 0)
					return parsed;
			}
			return DefaultPriority.ToArray();
		}

		public void SetPriority(IEnumerable<string> ids)
		{
			if (TryContainer() is { } container)
				container.Values[PriorityKey] = string.Join(',', ids);
		}

		public bool GetEnabled(string id, bool defaultValue)
		{
			var container = TryContainer();
			if (container is not null && container.Values.TryGetValue(EnabledPrefix + id, out var raw) && raw is bool value)
				return value;
			return defaultValue;
		}

		public void SetEnabled(string id, bool enabled)
		{
			if (TryContainer() is { } container)
				container.Values[EnabledPrefix + id] = enabled;
		}

		public AiEndpointConfig GetEndpoint(string id)
		{
			var container = TryContainer();
			var url = string.Empty;
			var model = string.Empty;
			if (container is not null)
			{
				if (container.Values.TryGetValue(UrlPrefix + id, out var rawUrl) && rawUrl is string u) url = u;
				if (container.Values.TryGetValue(ModelPrefix + id, out var rawModel) && rawModel is string m) model = m;
			}
			return new AiEndpointConfig(url, GetApiKey(id), model);
		}

		public void SetEndpoint(string id, string? baseUrl, string? apiKey, string? model)
		{
			if (TryContainer() is { } container)
			{
				container.Values[UrlPrefix + id] = baseUrl ?? string.Empty;
				container.Values[ModelPrefix + id] = model ?? string.Empty;
			}
			SetApiKey(id, apiKey);
		}

		private static string GetApiKey(string id)
		{
			try
			{
				var credential = new PasswordVault().Retrieve(VaultResource, id);
				credential.RetrievePassword();
				return credential.Password;
			}
			catch
			{
				return string.Empty;
			}
		}

		private static void SetApiKey(string id, string? apiKey)
		{
			var vault = new PasswordVault();
			try
			{
				if (string.IsNullOrEmpty(apiKey))
				{
					vault.Remove(vault.Retrieve(VaultResource, id));
				}
				else
				{
					// Remove-then-add keeps a single credential per provider.
					try { vault.Remove(vault.Retrieve(VaultResource, id)); } catch { }
					vault.Add(new PasswordCredential(VaultResource, id, apiKey));
				}
			}
			catch
			{
				// Vault unavailable (unpackaged/hosted tests): keys simply are not persisted.
			}
		}
	}
}
