// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.IO;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Files.App.Views
{
	public sealed partial record NaiWorkspaceRow(string Name, string Detail, string? Path = null);
	public sealed partial class NaiWorkspacePage : Page, IDisposable
	{
		private readonly CancellationTokenSource lifetime = new();
		private CancellationTokenSource? operation;
		private IShellPage? host;
		private NaiSavedWorkspace[] saved = [];
		private bool disposed;
		internal static NaiWorkspaceStore Store { get; } = new(Path.Combine(ApplicationData.Current.LocalFolder.Path, "nai-workspaces.json"));
		private static string L(string key) => key.GetLocalizedResource();
		public NaiWorkspacePage() => InitializeComponent();
		protected override async void OnNavigatedTo(NavigationEventArgs e)
		{
			base.OnNavigatedTo(e);
			if (e.Parameter is not NavigationArguments args || args.AssociatedTabInstance is not { } pane) return;
			host = pane;
			try
			{
				await NaiWorkspaceRoutes.PrepareAsync(pane, NaiWorkspaceRoutes.Workbench);
				var folder = args.SelectItems?.FirstOrDefault();
				if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) folder = ApplicationData.Current.LocalSettings.Values.TryGetValue("NaiWorkspace.Folder", out var prior) ? prior as string : null;
				FolderBox.Text = folder ?? string.Empty;
				await ReloadSavedAsync();
			}
			catch (Exception ex) { Failure(ex); }
		}
		protected override void OnNavigatedFrom(NavigationEventArgs e) { Dispose(); base.OnNavigatedFrom(e); }
		public void Dispose()
		{
			if (disposed) return; disposed = true; operation?.Cancel(); lifetime.Cancel(); lifetime.Dispose();
		}
		private void Failure(Exception ex) { if (!disposed && !lifetime.IsCancellationRequested) StatusText.Text = string.Format(L("NaiAiFailure"), ex.Message); }
		private async Task RunAsync(Func<CancellationToken, Task> work)
		{
			if (disposed || operation is not null) return;
			using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
			operation = request; SetReviewEnabled(false); CancelButton.Visibility = Visibility.Visible;
			try { await work(request.Token); }
			catch (OperationCanceledException) { if (!disposed && !lifetime.IsCancellationRequested) StatusText.Text = L("NaiWorkspaceCancelled"); }
			catch (Exception ex) { Failure(ex); }
			finally { operation = null; if (!disposed) { SetReviewEnabled(true); CancelButton.Visibility = Visibility.Collapsed; } }
		}
		private void SetReviewEnabled(bool enabled)
		{
			ChooseFolderButton.IsEnabled = enabled;
			foreach (var control in ReviewActions.Children.OfType<Control>()) control.IsEnabled = enabled;
		}
		private void Rows(IEnumerable<NaiWorkspaceRow> rows)
		{
			if (disposed) return;
			var all = rows.ToArray(); Results.ItemsSource = all.Take(500).ToArray();
			StatusText.Text = string.Format(L("NaiWorkspaceResults"), Math.Min(500, all.Length), all.Length);
		}
		private async Task<NaiFolderSnapshot> SnapshotAsync(CancellationToken token)
		{
			var snapshot = await NaiFileWorkspace.SnapshotAsync(FolderBox.Text, token);
			token.ThrowIfCancellationRequested();
			ScopeText.Text = string.Format(L("NaiWorkspaceScope"), snapshot.Items.Length, snapshot.Skipped, snapshot.Truncated ? L("NaiWorkspaceTruncated") : string.Empty);
			return snapshot;
		}
		private async Task<string?> PickFolderAsync()
		{
			var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
			WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);
			return (await picker.PickSingleFolderAsync())?.Path;
		}
		private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
		{
			try { if (await PickFolderAsync() is string path) { FolderBox.Text = path; ApplicationData.Current.LocalSettings.Values["NaiWorkspace.Folder"] = path; Results.ItemsSource = null; ScopeText.Text = StatusText.Text = string.Empty; } }
			catch (Exception ex) { Failure(ex); }
		}
		private async void Storage_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
		{
			var snapshot = await SnapshotAsync(token);
			Rows(snapshot.Items.Where(item => !item.IsFolder).OrderByDescending(item => item.Size).Select(item => new NaiWorkspaceRow(item.Name,
				$"{item.Size:N0} B · {new DateTime(item.ModifiedUtcTicks, DateTimeKind.Utc).ToLocalTime():g}" + (item.Local ? string.Empty : " · " + L("NaiWorkspaceOnlineOnly")), item.Path)));
		});
		private async void Compare_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				if (await PickFolderAsync() is not string other) return;
				await RunAsync(async token =>
				{
					var left = await SnapshotAsync(token); var right = await NaiFileWorkspace.SnapshotAsync(other, token);
					ScopeText.Text += "\n" + L("NaiWorkspaceComparisonNote") + "\n" + other + " · " + string.Format(L("NaiWorkspaceScope"), right.Items.Length, right.Skipped, right.Truncated ? L("NaiWorkspaceTruncated") : string.Empty);
					Rows(NaiFileWorkspace.Compare(left, right).Select(item => new NaiWorkspaceRow(item.Name, L("NaiCompare" + item.Kind), (item.Left ?? item.Right)?.Path)));
				});
			}
			catch (Exception ex) { Failure(ex); }
		}
		private async void Duplicates_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
		{
			if (await new ContentDialog { XamlRoot = XamlRoot, Title = L("NaiWorkspaceDuplicates"), Content = new TextBlock { Text = L("NaiWorkspaceHashConsent"), TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = L("NaiWorkspaceDuplicates"), CloseButtonText = Strings.Cancel.GetLocalizedResource() }.ShowAsync() != ContentDialogResult.Primary) return;
			var snapshot = await SnapshotAsync(token);
			var progress = new Progress<NaiReviewProgress>(state => { if (!disposed && !token.IsCancellationRequested) StatusText.Text = string.Format(L("NaiWorkspaceHashProgress"), state.Completed, state.Total, state.Skipped); });
			var groups = await NaiFileWorkspace.ReviewDuplicatesAsync(snapshot, progress, token);
			Rows(groups.Select(group => new NaiWorkspaceRow(string.Format(L("NaiWorkspaceDuplicateGroup"), group.Files.Length, group.Files[0].Size),
				string.Join("\n", group.Files.Select(file => file.Name)) + "\nSHA-256: " + group.Sha256, group.Files[0].Path)));
		});
		private async void History_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
		{
			var snapshot = await SnapshotAsync(token); await NaiDocumentHistory.EnsureLoadedAsync();
			Rows(snapshot.Items.Where(item => NaiDocumentHistory.IsDocument(item.Path)).Select(item => (Item: item, History: NaiDocumentHistory.Get(item.Path)))
				.OrderByDescending(row => row.History?.LastOpened).Select(row => new NaiWorkspaceRow(row.Item.Name, row.History is { } history
					? string.Format(L("NaiWorkspaceOpened"), history.LastOpened.LocalDateTime.ToString("g"), history.Count) : L("NaiWorkspaceNotOpened"), row.Item.Path)));
		});
		private async void Changes_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
		{
			var snapshot = await SnapshotAsync(token); await NaiDocumentHistory.EnsureLoadedAsync();
			var cached = NaiVisionCache.Latest(await NaiVisionIndexer.Cache.ReadAsync(snapshot.Folder, token)).ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
			var rows = new List<NaiWorkspaceRow>();
			foreach (var item in snapshot.Items.Where(item => !item.IsFolder))
			{
				var reasons = new List<string>();
				if (NaiDocumentHistory.Get(item.Path) is { } opened && item.ModifiedUtcTicks > opened.LastOpened.UtcTicks) reasons.Add(L("NaiWorkspaceChangedSinceOpen"));
				if (cached.TryGetValue(item.Path, out var analysis) && (item.Size != analysis.Length || item.ModifiedUtcTicks != analysis.LastWriteUtcTicks)) reasons.Add(L("NaiVisionStale"));
				if (reasons.Count > 0) rows.Add(new(item.Name, string.Join("\n", reasons), item.Path));
			}
			Rows(rows); ScopeText.Text += "\n" + L("NaiWorkspaceChangesNote");
		});
		private async void Groups_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
		{
			var entries = NaiVisionCache.Latest(await NaiVisionIndexer.Cache.ReadAsync(FolderBox.Text, token));
			Rows(entries.Where(entry => entry.EnglishTags.Length > 0).GroupBy(entry => entry.EnglishTags[0], StringComparer.OrdinalIgnoreCase)
				.OrderByDescending(group => group.Count()).Select(group => new NaiWorkspaceRow(group.Key,
					string.Join("\n", group.Take(100).Select(entry => Path.GetFileName(entry.Path) + " · " + entry.Model)))));
			ScopeText.Text = L("NaiWorkspaceGroupsNote");
		});
		private void Vision_Click(object sender, RoutedEventArgs e) { if (host is not null) NaiWorkspaceRoutes.OpenInPane(host, NaiWorkspaceRoutes.Vision, FolderBox.Text); }
		private void Cancel_Click(object sender, RoutedEventArgs e) => operation?.Cancel();
		private async Task ReloadSavedAsync()
		{
			saved = (await Store.ReadAsync(lifetime.Token)).Workspaces;
			SavedBox.ItemsSource = saved.Select(workspace => workspace.Name).ToArray();
		}
		private async void SaveWorkspace_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				var name = new TextBox { MaxLength = 80, Header = L("NaiWorkspaceName") };
				if (await new ContentDialog { XamlRoot = XamlRoot, Title = L("NaiWorkspaceSave"), Content = name, PrimaryButtonText = L("NaiWorkspaceSave"), CloseButtonText = Strings.Cancel.GetLocalizedResource() }.ShowAsync() != ContentDialogResult.Primary) return;
				var tabs = MainPageViewModel.AppInstances.Where(tab => tab.NavigationParameter is not null).Select(tab => tab.NavigationParameter!.Serialize()).ToArray();
				await Store.SaveWorkspaceAsync(new(name.Text.Trim(), tabs, Math.Clamp(App.AppModel.TabStripSelectedIndex, 0, Math.Max(0, tabs.Length - 1)), DateTimeOffset.UtcNow), lifetime.Token);
				await ReloadSavedAsync(); SavedBox.SelectedItem = name.Text.Trim();
			}
			catch (Exception ex) { Failure(ex); }
		}
		private async void RestoreWorkspace_Click(object sender, RoutedEventArgs e)
		{
			if (SavedBox.SelectedItem is not string name || saved.FirstOrDefault(item => item.Name == name) is not { } workspace) return;
			try
			{
				var tabs = workspace.Tabs.Select(TabBarItemParameter.Deserialize).ToArray();
				var start = MainPageViewModel.AppInstances.Count;
				foreach (var tab in tabs) await NavigationHelpers.AddNewTabByParamAsync(tab.InitialPageType, tab.NavigationParameter, switchToNewTab: false);
				App.AppModel.TabStripSelectedIndex = start + workspace.SelectedIndex;
			}
			catch (Exception ex) { Failure(ex); }
		}
		private async void ForgetWorkspace_Click(object sender, RoutedEventArgs e)
		{
			if (SavedBox.SelectedItem is not string name) return;
			try { await Store.DeleteWorkspaceAsync(name, lifetime.Token); await ReloadSavedAsync(); }
			catch (Exception ex) { Failure(ex); }
		}
		private async void Results_ItemClick(object sender, ItemClickEventArgs e)
		{
			if (e.ClickedItem is not NaiWorkspaceRow { Path: string path } || host is null) return;
			try { if (Path.GetDirectoryName(path) is string folder) await NavigationHelpers.OpenPath(folder, host, selectItems: (string[])[path]); }
			catch (Exception ex) { Failure(ex); }
		}
	}
}
