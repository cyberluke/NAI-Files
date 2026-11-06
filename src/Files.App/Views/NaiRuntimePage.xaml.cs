// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Files.App.Views
{
	public sealed partial class NaiRuntimePage : Page, IDisposable
	{
		private CancellationTokenSource lifetime = new();
		private readonly DispatcherQueueTimer timer;
		private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
		private readonly TextBox snapshot = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
		private bool loading;
		private bool disposed;
		private static string L(string key) => key.GetLocalizedResource();
		[WinRT.DynamicWindowsRuntimeCast(typeof(Style))]
		public NaiRuntimePage()
		{
			InitializeComponent();
			var body = new StackPanel { Padding = new Thickness(24), Spacing = 12 };
			body.Children.Add(new TextBlock { Text = L("NaiRuntimeTitle"), Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] });
			body.Children.Add(new TextBlock { Text = L("NaiRuntimeDescription"), TextWrapping = TextWrapping.Wrap });
			var refresh = new Button { Content = L("NaiRuntimeRefresh") }; refresh.Click += async (_, _) => await RefreshAsync();
			body.Children.Add(refresh); body.Children.Add(status); body.Children.Add(snapshot);
			Content = new ScrollViewer { Content = body };
			timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromSeconds(5);
			timer.Tick += async (_, _) => { if (IsLoaded && MainWindow.Instance.AppWindow.IsVisible) await RefreshAsync(); };
			Unloaded += (_, _) => { timer.Stop(); if (!disposed) lifetime.Cancel(); };
			Loaded += async (_, _) =>
			{
				if (disposed) return;
				if (lifetime.IsCancellationRequested) { lifetime.Dispose(); lifetime = new(); }
				timer.Start(); await RefreshAsync();
			};
		}
		protected override async void OnNavigatedTo(NavigationEventArgs e)
		{
			base.OnNavigatedTo(e);
			if (e.Parameter is not NavigationArguments args || args.AssociatedTabInstance is not { } host) return;
			try { await NaiWorkspaceRoutes.PrepareAsync(host, NaiWorkspaceRoutes.Runtime); await RefreshAsync(); timer.Start(); }
			catch (Exception ex) { status.Text = string.Format(L("NaiRuntimeUnavailable"), ex.Message); }
		}
		protected override void OnNavigatedFrom(NavigationEventArgs e) { Dispose(); base.OnNavigatedFrom(e); }
		public void Dispose() { if (disposed) return; disposed = true; timer.Stop(); lifetime.Cancel(); lifetime.Dispose(); }
		private async Task RefreshAsync()
		{
			if (disposed || loading || lifetime.IsCancellationRequested) return;
			loading = true;
			var currentLifetime = lifetime; var token = currentLifetime.Token;
			try
			{
				var data = await NaiAiClient.RuntimeSnapshotAsync(token); token.ThrowIfCancellationRequested();
				if (disposed || !ReferenceEquals(currentLifetime, lifetime)) return;
				var lines = new List<string> { string.Format(L("NaiRuntimeSnapshotAt"), data.ObservedAt.LocalDateTime.ToString("g")),
					data.Active is int active && data.Queued is int queued ? string.Format(L("NaiRuntimeQueue"), active, queued) : L("NaiRuntimeQueueUnknown") };
				foreach (var provider in data.Resources.GroupBy(resource => resource.Provider))
				{
					lines.Add("\n" + provider.Key);
					foreach (var resource in provider)
						lines.Add($"  {resource.Id} · {resource.Kind} · {resource.Model} · {resource.State} / {resource.Health} · {resource.UpdatedAt.LocalDateTime:g}");
				}
				lines.Add("\n" + L("NaiRuntimeAssignments"));
				foreach (var work in data.Workloads) lines.Add($"{work.Capability} · {work.State} · {work.Provider ?? "—"} → {work.Device ?? "—"}");
				lines.Add("\n" + L("NaiRuntimeRelationships"));
				foreach (var edge in data.Relationships) lines.Add($"{edge.From} → {edge.To} ({edge.Kind})");
				snapshot.Text = string.Join("\n", lines);
				status.Text = data.Truncated ? L("NaiRuntimeTruncated") : L("NaiRuntimeReadOnly");
			}
			catch (OperationCanceledException) { }
			catch (Exception ex) { if (!disposed && !token.IsCancellationRequested) status.Text = string.Format(L("NaiRuntimeUnavailable"), ex.Message); }
			finally { loading = false; }
		}
	}
}
