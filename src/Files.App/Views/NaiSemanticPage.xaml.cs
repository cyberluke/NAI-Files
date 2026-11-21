// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Files.App.AI.Semantic;
using System.IO;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace Files.App.Views
{
	public sealed partial class NaiSemanticPage : Page, IDisposable
	{
		private readonly CancellationTokenSource lifetime = new();
		private CancellationTokenSource? operation;
		private CancellationTokenSource? searchDebounce;
		private string? lastSearchedQuery;
		private IShellPage? host;
		private bool disposed;
		private bool indexing;
		private bool backendReady;
		private static string L(string key) => key.GetLocalizedResource();
		public NaiSemanticPage() => InitializeComponent();
		protected override async void OnNavigatedTo(NavigationEventArgs e)
		{
			base.OnNavigatedTo(e);
			if (e.Parameter is not NavigationArguments args || args.AssociatedTabInstance is not { } pane) return;
			host = pane;
			try
			{
				var folder = args.SelectItems?.FirstOrDefault();
				if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) folder = ApplicationData.Current.LocalSettings.Values.TryGetValue("NaiSemantic.Folder", out var saved) ? saved as string : null;
				FolderBox.Text = folder ?? string.Empty;
				await NaiWorkspaceRoutes.PrepareAsync(pane, NaiWorkspaceRoutes.Semantic);
				await RefreshStateAsync(lifetime.Token);
			}
			catch (Exception ex) { Failure(ex); }
		}
		protected override void OnNavigatedFrom(NavigationEventArgs e) { Dispose(); base.OnNavigatedFrom(e); }
		public void Dispose() { if (disposed) return; disposed = true; operation?.Cancel(); searchDebounce?.Cancel(); lifetime.Cancel(); lifetime.Dispose(); Results.ItemsSource = null; }
		private void Failure(Exception ex)
		{
			if (disposed) return;
			// A paired-but-unavailable retrieval backend is a known recoverable
			// state, not an opaque transport failure: present its guidance directly.
			StatusText.Text = ex is NaiRetrievalUnavailableException
				? ex.Message
				: string.Format(L("NaiSemanticSearchFailure"), ex.Message);
		}
		private void Controls()
		{
			if (disposed || IndexButton is null) return;
			var idle = operation is null;
			// Embeddings come from LUSI, vectors from NAI Lattice; no Kelvin pairing gate.
			IndexButton.IsEnabled = idle && ConsentBox.IsChecked == true && Directory.Exists(FolderBox.Text) && backendReady;
			FullResultsButton.IsEnabled = ForgetButton.IsEnabled = ChooseButton.IsEnabled = RecursiveBox.IsEnabled = idle;
			CancelButton.IsEnabled = !idle;
		}
		private async Task RunAsync(Func<CancellationToken, Task> work)
		{
			if (disposed || operation is not null) return;
			using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
			operation = request; Controls(); Progress.Visibility = Visibility.Visible; Progress.IsIndeterminate = true;
			try { await work(request.Token); }
			catch (OperationCanceledException) when (request.IsCancellationRequested) { if (!disposed) StatusText.Text = L("NaiSemanticCancelled"); }
			catch (Exception ex) { Failure(ex); }
			finally { operation = null; if (!disposed) { Progress.Visibility = Visibility.Collapsed; Controls(); } }
		}
		private async Task RefreshStateAsync(CancellationToken token)
		{
			backendReady = await LatticeSemanticClient.IsAvailableAsync(token) && await SemanticEmbedder.IsAvailableAsync(token);
			if (!backendReady)
			{
				IndexStateText.Text = L("NaiSemanticEngineUnavailable");
				Controls();
				return;
			}
			if (!Directory.Exists(FolderBox.Text)) { IndexStateText.Text = L("NaiSemanticChooseFolder"); Controls(); return; }
			var state = await NaiSemanticIndexService.Store.ReadAsync(FolderBox.Text, token); token.ThrowIfCancellationRequested();
			if (disposed) return;
			IndexStateText.Text = state is null ? L("NaiSemanticIndexRequired")
				: string.Format(L("NaiSemanticIndexState"), state.Documents.Length, state.UpdatedAt.LocalDateTime.ToString("g"), state.Partial ? L("NaiSemanticPartial") : L("NaiSemanticReady"), state.PendingDeleteIds.Length);
			Controls();
		}
		private async void Choose_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);
				if (await picker.PickSingleFolderAsync() is not { } selected || disposed) return;
				FolderBox.Text = selected.Path; ConsentBox.IsChecked = false; Results.ItemsSource = null;
				ApplicationData.Current.LocalSettings.Values["NaiSemantic.Folder"] = selected.Path;
				await RefreshStateAsync(lifetime.Token);
			}
			catch (Exception ex) { Failure(ex); }
		}
		private void Scope_Changed(object sender, RoutedEventArgs e) { if (ConsentBox is not null) ConsentBox.IsChecked = false; Controls(); }
		private void Consent_Changed(object sender, RoutedEventArgs e) { if (indexing && ConsentBox?.IsChecked != true) operation?.Cancel(); Controls(); }
		private void Cancel_Click(object sender, RoutedEventArgs e) => operation?.Cancel();
		// Kelvin pairing removed: semantic recall is LUSI embeddings + NAI Lattice vectors.
		private async void Index_Click(object sender, RoutedEventArgs e)
		{
			if (ConsentBox.IsChecked != true) return;
			indexing = true;
			try { await RunAsync(async token =>
			{
				var plan = await NaiSemanticIndexService.PreviewAsync(FolderBox.Text, RecursiveBox.IsChecked == true, token); token.ThrowIfCancellationRequested();
				var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 380,
					Text = string.Format(L("NaiSemanticReviewPlan"), plan.Documents.Length, plan.Skipped, plan.Limited ? L("NaiSemanticPartial") : string.Empty)
						+ "\n\n" + string.Join("\n", plan.Documents.Select(item => item.RelativePath)) };
				if (await new ContentDialog { XamlRoot = XamlRoot, Title = L("NaiSemanticIndex"), Content = preview, PrimaryButtonText = L("NaiSemanticApprove"), CloseButtonText = Strings.Cancel.GetLocalizedResource() }.ShowAsync() != ContentDialogResult.Primary) return;
				token.ThrowIfCancellationRequested();
				var progress = new Progress<NaiSemanticIndexProgress>(state =>
				{
					if (disposed || token.IsCancellationRequested) return;
					Progress.IsIndeterminate = false; Progress.Maximum = Math.Max(1, state.Total); Progress.Value = state.Completed;
					StatusText.Text = string.Format(L("NaiSemanticProgress"), state.Completed, state.Total, state.Indexed, state.Cached, state.Skipped, state.Failed);
				});
				await NaiSemanticIndexService.IndexAsync(plan, progress, token);
				await RefreshStateAsync(token);
			}); }
			finally { indexing = false; }
		}
private async void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
		{
			// Search as you type: no submit button. The debounce keeps every
			// keystroke from enqueueing retrieval work, and repeated identical
			// queries never re-run while the query has not changed.
			searchDebounce?.Cancel();
			searchDebounce = new CancellationTokenSource();
			var token = searchDebounce.Token;
			var query = QueryBox.Text;
			_ = Task.Run(async () =>
			{
				try { await Task.Delay(TimeSpan.FromMilliseconds(400), token); }
				catch (OperationCanceledException) { return; }
				if (token.IsCancellationRequested || disposed) return;
				if (string.IsNullOrWhiteSpace(query)) return;
				var effective = NaiSemanticProtocol.TryParse(query, out var parsed) ? parsed : query;
				if (string.Equals(effective, lastSearchedQuery, StringComparison.Ordinal)) return;
				lastSearchedQuery = effective;
				await DispatcherQueue.EnqueueOrInvokeAsync(async () =>
				{
					if (token.IsCancellationRequested || disposed) return;
					await RunSearchAsync(effective, token);
				});
			}, token);
		}
		private void QueryBox_KeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (e.Key is not VirtualKey.Enter) return;
			e.Handled = true;
			searchDebounce?.Cancel();
			var query = NaiSemanticProtocol.TryParse(QueryBox.Text, out var parsed) ? parsed : QueryBox.Text;
			if (string.IsNullOrWhiteSpace(query)) return;
			lastSearchedQuery = query;
			_ = RunSearchAsync(query, lifetime.Token);
		}
		private async Task RunSearchAsync(string query, CancellationToken token) => await RunAsync(async operationToken =>
		{
			var result = await NaiSemanticIndexService.SearchAsync(FolderBox.Text, query, operationToken);
			if (token.IsCancellationRequested || disposed) return; // superseded by newer typing: drop stale results silently
			operationToken.ThrowIfCancellationRequested();
			Results.ItemsSource = result.Matches.Select(match => new NaiWorkspaceRow(Path.GetFileName(match.Path),
				string.Format(L("NaiSemanticEvidence"), match.Score, match.IndexedAt.LocalDateTime.ToString("g")) + "\n" + match.Evidence, match.Path)).ToArray();
			StatusText.Text = string.Format(L("NaiSemanticResults"), result.Matches.Count, result.CurrentDocuments, result.StaleDocuments);
		});
		private void FullResults_Click(object sender, RoutedEventArgs e)
		{
			if (disposed || operation is not null || host is null || string.IsNullOrWhiteSpace(QueryBox.Text)) return;
			try
			{
				var query = NaiSemanticProtocol.TryParse(QueryBox.Text, out var parsed) ? parsed : QueryBox.Text;
				NaiWorkspaceRoutes.SearchInPane(host, FolderBox.Text, NaiSemanticProtocol.Prefix + " " + query);
			}
			catch (Exception ex) { Failure(ex); }
		}
		private async void Forget_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
		{
			if (await new ContentDialog { XamlRoot = XamlRoot, Title = L("NaiSemanticForget"), Content = new TextBlock { Text = L("NaiSemanticForgetConfirm"), TextWrapping = TextWrapping.Wrap },
				PrimaryButtonText = L("NaiSemanticForget"), CloseButtonText = Strings.Cancel.GetLocalizedResource() }.ShowAsync() != ContentDialogResult.Primary) return;
			await NaiSemanticIndexService.ForgetAsync(FolderBox.Text, token); token.ThrowIfCancellationRequested();
			if (!disposed) { Results.ItemsSource = null; ConsentBox.IsChecked = false; }
			await RefreshStateAsync(token);
		});
		private async void Result_Click(object sender, ItemClickEventArgs e)
		{
			if (disposed || host is null || e.ClickedItem is not NaiWorkspaceRow { Path: string path }) return;
			try { if (Path.GetDirectoryName(path) is string folder) await NavigationHelpers.OpenPath(folder, host, selectItems: (string[])[path]); }
			catch (Exception ex) { Failure(ex); }
		}
	}
}
