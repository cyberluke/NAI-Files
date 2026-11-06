// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Files.App.UserControls
{
	public sealed partial class NaiResourcesPanel : UserControl
	{
		private readonly TextBlock values = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
		private readonly Expander expander;
		private readonly DispatcherQueue dispatcherQueue;
		private bool subscribed;
		private static string L(string key) => key.GetLocalizedResource();

		public NaiResourcesPanel()
		{
			var body = new StackPanel { Spacing = 6 };
			body.Children.Add(values);
			var topology = new Button { Content = L("NaiRuntimeTitle"), HorizontalAlignment = HorizontalAlignment.Stretch };
			topology.Click += (_, _) => NaiWorkspaceRoutes.Open(NaiWorkspaceRoutes.Runtime);
			body.Children.Add(topology);
			expander = new Expander { Header = L("NaiResourcesTitle"), Content = body, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
			Content = expander;
			dispatcherQueue = DispatcherQueue.GetForCurrentThread();

			// Sampling runs only while this section is expanded: subscribe on
			// expand/load, unsubscribe on collapse/unload. All panels share one
			// process-wide sampler, so collapsing every panel stops the loop.
			Loaded += (_, _) => { if (expander.IsExpanded) Subscribe(); };
			Unloaded += (_, _) => Unsubscribe();
			expander.Expanding += (_, _) => Subscribe();
			expander.Collapsed += (_, _) => Unsubscribe();
		}

		private void Subscribe()
		{
			if (subscribed) return;
			subscribed = true;
			NaiSystemTelemetry.Shared.Sampled += OnSampled;
			NaiSystemTelemetry.Shared.Start();
		}

		private void Unsubscribe()
		{
			if (!subscribed) return;
			subscribed = false;
			NaiSystemTelemetry.Shared.Sampled -= OnSampled;
			NaiSystemTelemetry.Shared.Stop();
		}

		private void OnSampled(NaiSystemSample sample)
		{
			// Called on the sampler's worker thread; DispatcherQueue.TryEnqueue is
			// thread-safe and Render only touches UI state on the UI thread. Do not
			// read DependencyObject state (IsLoaded) here — it is UI-thread-bound.
			dispatcherQueue?.TryEnqueue(() => Render(sample));
		}

		private void Render(NaiSystemSample sample)
		{
			var unknown = L("NaiTelemetryUnknown");
			string Devices(string kind, NaiEngineUtilization[] devices) => devices.Length == 0 ? kind + ": " + unknown
				: string.Join("\n", devices.Select(device => double.IsNaN(device.Percent)
					? $"{kind} {device.Adapter}"
					: $"{kind} {device.Adapter.Replace("luid_0x00000000_", string.Empty, StringComparison.Ordinal)}: {device.Percent:F0}%"));
			string Network() => sample.Network switch
			{
				null => unknown,
				{ IsWlan: true } wlan => "Wi-Fi: " + (wlan.Bars is byte bars ? $"{bars}/5" : unknown),
				{ Name: not null } wired => "Síť: " + wired.Name,
				_ => "Síť: Ethernet",
			};
			values.Text = "CPU: " + (sample.Cpu is double cpu ? $"{cpu:F0}%" : unknown) + "\nRAM: " +
				(sample.RamUsed is ulong used && sample.RamTotal is ulong total ? $"{used / 1073741824d:F1}/{total / 1073741824d:F1} GiB" : unknown) +
				"\n" + L("NaiTelemetryDisk") + ": " + (sample.DiskFree is long free && sample.DiskTotal is long size ? $"{free / 1073741824d:F1}/{size / 1073741824d:F1} GiB" : unknown) +
				"\n" + Network() + "\n" + Devices("GPU", sample.Gpu) + "\n" + Devices("NPU", sample.Npu) +
				"\n" + string.Format(L("NaiTelemetryObserved"), sample.At.LocalDateTime.ToString("T"));
			ToolTipService.SetToolTip(values, L("NaiTelemetrySource") + "\n" + L("NaiTelemetryGpuSource"));
		}
	}
}