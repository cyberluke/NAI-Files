// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Net.Http;
using System.Net.Sockets;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Files.App.Views
{
	public sealed partial class NaiEmailPage : Page, IDisposable
	{
		private readonly CancellationTokenSource lifetime = new();
		private readonly DispatcherQueueTimer timer;
		private NaiEmailOverview? overview;
		private bool loading;
		private bool disposed;
		private static string L(string key) => key.GetLocalizedResource();
		public NaiEmailPage()
		{
			InitializeComponent(); ViewBox.Items.Add(L("NaiEmailAttachments")); ViewBox.Items.Add(L("NaiEmailCalendar")); ViewBox.SelectedIndex = 0;
			timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromSeconds(5);
			timer.Tick += async (_, _) => { if (IsLoaded && MainWindow.Instance.AppWindow.IsVisible) await RefreshAsync(); };
			Loaded += async (_, _) => { if (!disposed) { timer.Start(); await RefreshAsync(); } };
			Unloaded += (_, _) => timer.Stop();
		}
		protected override async void OnNavigatedTo(NavigationEventArgs e)
		{
			base.OnNavigatedTo(e);
			if (e.Parameter is not NavigationArguments args || args.AssociatedTabInstance is not { } host) return;
			try { await NaiWorkspaceRoutes.PrepareAsync(host, NaiWorkspaceRoutes.Mail); timer.Start(); await RefreshAsync(); }
			catch (Exception ex) { StatusText.Text = string.Format(L("NaiEmailUnavailable"), ex.Message); }
		}
		protected override void OnNavigatedFrom(NavigationEventArgs e) { Dispose(); base.OnNavigatedFrom(e); }
		public void Dispose()
		{
			if (disposed) return; disposed = true; timer.Stop(); lifetime.Cancel(); overview = null; Rows.ItemsSource = null; lifetime.Dispose();
		}
		private async Task RefreshAsync()
		{
			if (disposed || loading || lifetime.IsCancellationRequested) return;
			loading = LoadingRing.IsActive = true; RefreshButton.IsEnabled = false;
			var token = lifetime.Token;
			try
			{
				var data = await NaiEmailClient.OverviewAsync(token); token.ThrowIfCancellationRequested();
				if (disposed) return; overview = data;
				SummaryText.Text = string.Format(L("NaiEmailSummary"), overview.TodayUnread, overview.DueToday, overview.TodayMeetings, overview.AccountCount);
				StatusText.Text = string.Format(L("NaiEmailProvenance"), overview.GeneratedAt.LocalDateTime.ToString("g"), overview.DayStart.LocalDateTime.ToString("d"), overview.TimeZone)
					+ (overview.Truncated ? "\n" + L("NaiRuntimeTruncated") : string.Empty);
				Render();
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested) { }
			catch (Exception ex)
			{
				if (disposed) return;
				// Do not retain mailbox metadata on a withdrawn or failed grant.
				overview = null; Rows.ItemsSource = null; SummaryText.Text = string.Empty;
				StatusText.Text = TransientOverviewFailure(ex)
					? L("NaiEmailWaiting")
					: string.Format(L("NaiEmailUnavailable"), ex.Message);
			}
			finally { if (!disposed) { loading = LoadingRing.IsActive = false; RefreshButton.IsEnabled = true; } }
		}
		private static bool TransientOverviewFailure(Exception ex)
		{
			for (var current = ex; current is not null; current = current.InnerException)
			{
				if (current is TimeoutException or HttpRequestException or SocketException)
					return true;
				if (current is InvalidOperationException && current.Message == L("NaiEmailGrantMissing"))
					return true;
			}
			return false;
		}
		private void Render()
		{
			if (overview is null) return;
			Rows.ItemsSource = ViewBox.SelectedIndex == 1
				? overview.Meetings.Select(meeting => new NaiWorkspaceRow(meeting.Title, $"{meeting.Start.LocalDateTime:g} – {meeting.End.LocalDateTime:g}"
					+ (meeting.AllDay ? " · " + L("NaiEmailAllDay") : string.Empty) + (meeting.HasRecord ? " · " + L("NaiEmailHasRecord") : string.Empty), meeting.Link)).ToArray()
				: overview.Attachments.Select(attachment => new NaiWorkspaceRow(attachment.Name, $"{attachment.MimeType} · {(attachment.Bytes is long bytes ? $"{bytes:N0} B" : "—")} · {attachment.ReceivedAt.LocalDateTime:g}", attachment.Link)).ToArray();
		}
		private void View_Changed(object sender, SelectionChangedEventArgs e) => Render();
		private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
		private async Task OpenOwnerAsync(string link)
		{
			if (!NaiEmailProtocol.IsSafeLink(link)) return;
			try { if (!await Windows.System.Launcher.LaunchUriAsync(new Uri(link))) StatusText.Text = L("NaiEmailOpenFailed"); }
			catch (Exception ex) { StatusText.Text = string.Format(L("NaiEmailUnavailable"), ex.Message); }
		}
		private async void Owner_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string link }) await OpenOwnerAsync(link); }
		private async void Row_Click(object sender, ItemClickEventArgs e) { if (e.ClickedItem is NaiWorkspaceRow { Path: string link }) await OpenOwnerAsync(link); }
	}
}
