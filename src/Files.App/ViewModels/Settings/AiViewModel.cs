// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.Specialized;
using Files.App.AI;

namespace Files.App.ViewModels.Settings
{
	/// <summary>
	/// Backs the AI settings page: the OpenAI-compatible gateway form (URL, key, model probe) and the
	/// draggable provider priority ladder. All state lives in <see cref="AiProviderRegistry"/>; this view
	/// model only projects it and writes changes back.
	/// </summary>
	public sealed partial class AiViewModel : ObservableObject
	{
		private readonly AiProviderRegistry registry = AiProviderRegistry.Instance;
		private bool isRebuilding;

		public ObservableCollection<AiProviderRow> Providers { get; } = [];
		public ObservableCollection<string> GatewayModels { get; } = [];

		[ObservableProperty] public partial string GatewayUrl { get; set; } = string.Empty;
		[ObservableProperty] public partial string GatewayApiKey { get; set; } = string.Empty;
		[ObservableProperty] public partial string GatewayModel { get; set; } = string.Empty;
		[ObservableProperty] public partial string NpuUrl { get; set; } = string.Empty;
		[ObservableProperty] public partial string NpuModel { get; set; } = string.Empty;
		[ObservableProperty] public partial string GpuUrl { get; set; } = string.Empty;
		[ObservableProperty] public partial string GpuModel { get; set; } = string.Empty;
		[ObservableProperty] public partial string ProbeStatus { get; set; } = string.Empty;
		[ObservableProperty] public partial bool IsProbing { get; set; }

		public AiViewModel()
		{
			Providers.CollectionChanged += Providers_CollectionChanged;
			Load();
		}

		private void Load()
		{
			registry.ReloadPriority();

			var endpoint = registry.GetEndpoint(AiProviderIds.OpenAiCompatible);
			GatewayUrl = endpoint.BaseUrl;
			GatewayApiKey = endpoint.ApiKey;
			GatewayModel = endpoint.Model;

			var npu = registry.GetEndpoint(AiProviderIds.NpuLocal);
			NpuUrl = npu.BaseUrl;
			NpuModel = npu.Model;

			var gpu = registry.GetEndpoint(AiProviderIds.GpuLocal);
			GpuUrl = gpu.BaseUrl;
			GpuModel = gpu.Model;

			isRebuilding = true;
			try
			{
				Providers.Clear();
				foreach (var id in registry.Priority)
				{
					if (registry.Find(id) is not { } provider)
						continue;
					Providers.Add(new AiProviderRow(provider, registry.IsEnabled(id), OnRowEnabledChanged));
				}
			}
			finally
			{
				isRebuilding = false;
			}
		}

		private void Providers_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
		{
			if (isRebuilding)
				return;

			// A drag-reorder mutates the bound collection; mirror the new order into the ladder.
			registry.SetPriority(Providers.Select(row => row.Id));
		}

		private void OnRowEnabledChanged(string id, bool enabled) => registry.SetEnabled(id, enabled);

		/// <summary>Records the model picked from the probed dropdown.</summary>
		public void SetGatewayModel(string? model)
		{
			if (!string.IsNullOrEmpty(model))
				GatewayModel = model;
		}

		/// <summary>Records the API key typed into the PasswordBox.</summary>
		public void SetGatewayApiKey(string? key) => GatewayApiKey = key ?? string.Empty;

		[RelayCommand]
		private async Task ProbeAsync()
		{
			if (IsProbing)
				return;

			IsProbing = true;
			ProbeStatus = "Probing /v1/models…";
			try
			{
				// Persist first so the probe and the completion path read the same endpoint.
				registry.SetEndpoint(AiProviderIds.OpenAiCompatible, GatewayUrl, GatewayApiKey, GatewayModel);

				if (registry.Find(AiProviderIds.OpenAiCompatible) is not { } provider)
				{
					ProbeStatus = "Gateway provider is unavailable";
					return;
				}

				var status = await provider.ProbeAsync(CancellationToken.None);
				var models = status.Health == AiProviderHealth.Online
					? await provider.ListModelsAsync(CancellationToken.None)
					: [];

				GatewayModels.Clear();
				foreach (var model in models)
					GatewayModels.Add(model);

				if (GatewayModels.Count > 0 && string.IsNullOrWhiteSpace(GatewayModel))
					GatewayModel = GatewayModels[0];

				ProbeStatus = $"{status.Health} — {status.Detail}";
			}
			catch (Exception ex)
			{
				ProbeStatus = ex.Message;
			}
			finally
			{
				IsProbing = false;
			}
		}

		[RelayCommand]
		private void SaveGateway()
		{
			registry.SetEndpoint(AiProviderIds.OpenAiCompatible, GatewayUrl, GatewayApiKey, GatewayModel);
			ProbeStatus = "Saved";
		}

		[RelayCommand]
		private void SaveLocal()
		{
			registry.SetEndpoint(AiProviderIds.NpuLocal, NpuUrl, null, NpuModel);
			registry.SetEndpoint(AiProviderIds.GpuLocal, GpuUrl, null, GpuModel);
			ProbeStatus = "Local lanes saved";
		}
	}

	/// <summary>One row of the draggable provider ladder.</summary>
	public sealed partial class AiProviderRow : ObservableObject
	{
		private readonly Action<string, bool> onEnabledChanged;
		private bool isInitializing;

		public AiProviderRow(IAiProvider provider, bool enabled, Action<string, bool> onEnabledChanged)
		{
			Id = provider.Id;
			DisplayName = provider.DisplayName;
			Kind = provider.Kind;
			IsLocal = provider.IsLocal;
			this.onEnabledChanged = onEnabledChanged;

			isInitializing = true;
			Enabled = enabled;
			isInitializing = false;
		}

		public string Id { get; }
		public string DisplayName { get; }
		public AiProviderKind Kind { get; }
		public bool IsLocal { get; }

		public string Badge => IsLocal ? "Local" : "Cloud";

		public string Subtitle => Kind switch
		{
			AiProviderKind.Lusi => "Canonical on-device engine",
			AiProviderKind.Kelvin => "Paired cloud fallback",
			AiProviderKind.OpenAiCompatible => "Configured gateway",
			AiProviderKind.NpuLocal => "Local NPU lane",
			AiProviderKind.GpuLocal => "Local GPU lane",
			_ => string.Empty,
		};

		[ObservableProperty] public partial bool Enabled { get; set; }

		partial void OnEnabledChanged(bool value)
		{
			if (!isInitializing)
				onEnabledChanged(Id, value);
		}
	}
}
