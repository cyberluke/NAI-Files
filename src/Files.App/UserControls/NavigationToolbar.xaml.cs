// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.WinUI;
using Files.App.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using Windows.UI.Core;
using WinRT;

namespace Files.App.UserControls
{
	public sealed partial class NavigationToolbar : UserControl
	{
		private bool naiAssistantOpen;
		private async void NaiAssistant_Click(object sender, RoutedEventArgs e)
		{
			if (naiAssistantOpen || XamlRoot is null) return;
			naiAssistantOpen = true;
			try { await Files.App.Dialogs.NaiAssistantDialog.ShowAsync(XamlRoot, ContentPageContext); }
			catch (Exception ex) { App.Logger.LogWarning(ex, "Unable to open NAI Files assistant"); }
			finally { naiAssistantOpen = false; }
		}

		// Dependency injections

		private readonly IUserSettingsService userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();
		private readonly MainPageViewModel MainPageViewModel = Ioc.Default.GetRequiredService<MainPageViewModel>();
		private readonly ICommandManager Commands = Ioc.Default.GetRequiredService<ICommandManager>();
		private readonly StatusCenterViewModel OngoingTasksViewModel = Ioc.Default.GetRequiredService<StatusCenterViewModel>();
		private readonly IContentPageContext ContentPageContext = Ioc.Default.GetRequiredService<IContentPageContext>();

		private (OmnibarMode Mode, string Text)? _pendingOmnibarText;

		// Debounced search-as-you-type: typing in the Omnibar search lane submits
		// automatically after a pause, so no search button is required. Cancelled
		// on every keystroke and on explicit submission (Enter or suggestion).
		private CancellationTokenSource? _autoSearchCTS;

		// Properties

		[GeneratedDependencyProperty]
		public partial bool IsSidebarPaneOpenToggleButtonVisible { get; set; }

		[GeneratedDependencyProperty]
		public partial bool ShowOngoingTasks { get; set; }

		[GeneratedDependencyProperty]
		public partial bool ShowSettingsButton { get; set; }

		[GeneratedDependencyProperty]
		public partial NavigationToolbarViewModel? ViewModel { get; set; }

		// Constructor

		public NavigationToolbar()
		{
			InitializeComponent();
		}

		// Methods

		private void NavToolbar_Loaded(object sender, RoutedEventArgs e)
			=> this.RunAfterNextRender(() => FindName(nameof(ListaryIntegration)));

		private void NavToolbar_Loading(FrameworkElement _, object e)
		{
			Loading -= NavToolbar_Loading;
			if (OngoingTasksViewModel is not null)
				OngoingTasksViewModel.NewItemAdded += OngoingTasksActions_ProgressBannerPosted;
		}

		private void OngoingTasksActions_ProgressBannerPosted(object? _, StatusCenterItem e)
		{
			if (OngoingTasksViewModel is not null)
				OngoingTasksViewModel.NewItemAdded -= OngoingTasksActions_ProgressBannerPosted;

			// Displays a teaching tip the first time a banner is posted
			if (userSettingsService.AppSettingsService.ShowStatusCenterTeachingTip)
			{
				StatusCenterTeachingTip.IsOpen = true;
				userSettingsService.AppSettingsService.ShowStatusCenterTeachingTip = false;
			}
		}

		private void Button_AccessKeyInvoked(UIElement sender, AccessKeyInvokedEventArgs args)
		{
			// Suppress access key invocation if any dialog is open
			if (VisualTreeHelper.GetOpenPopupsForXamlRoot(MainWindow.Instance.Content.XamlRoot).Any())
				args.Handled = true;
		}

		private async void BackHistoryFlyout_Opening(object? sender, object e)
		{
			var shellPage = Ioc.Default.GetRequiredService<IContentPageContext>().ShellPage;
			if (shellPage is null)
				return;

			await AddHistoryItemsAsync(shellPage.BackwardStack, BackHistoryFlyout.Items, true);
		}

		private async void ForwardHistoryFlyout_Opening(object? sender, object e)
		{
			var shellPage = Ioc.Default.GetRequiredService<IContentPageContext>().ShellPage;
			if (shellPage is null)
				return;

			await AddHistoryItemsAsync(shellPage.ForwardStack, ForwardHistoryFlyout.Items, false);
		}

		private async Task AddHistoryItemsAsync(IEnumerable<PageStackEntry> items, IList<MenuFlyoutItemBase> flyoutItems, bool isBackMode)
		{
			// This may not seem performant, however it's the most viable trade-off to make.
			// Instead of constantly keeping track of back/forward stack and performing lookups
			// (which may degrade performance), we only add items in bulk when it's needed.
			// There's also a high chance the user might not use the feature at all in which case
			// the former approach would just waste extra performance gain

			flyoutItems.Clear();
			foreach (var item in items.Reverse())
			{
				if (item.Parameter is not NavigationArguments args || args.NavPathParam is null)
					continue;

				var fileName = SystemIO.Path.GetFileName(args.NavPathParam);

				// The fileName is empty if the path is (root) drive path
				if (string.IsNullOrEmpty(fileName))
					fileName = args.NavPathParam;

				var flyoutItem = new MenuFlyoutItem
				{
					Icon = new FontIcon() { Glyph = "\uE8B7" }, // Placeholder icon
					Text = fileName,
					Command = new RelayCommand<ToolbarHistoryItemModel?>(HistoryItemClicked),
					CommandParameter = new ToolbarHistoryItemModel(item, isBackMode)
				};

				flyoutItems?.Add(flyoutItem);

				// Start loading the thumbnail in the background
				_ = LoadFlyoutItemIconAsync(flyoutItem, args.NavPathParam);

				async Task LoadFlyoutItemIconAsync(MenuFlyoutItem flyoutItem, string path)
				{
					var imageSource = await NavigationHelpers.GetIconForPathAsync(path);

					if (imageSource is not null)
						flyoutItem.Icon = new ImageIcon() { Source = imageSource };
				}

				void HistoryItemClicked(ToolbarHistoryItemModel? itemModel)
				{
					if (itemModel is null)
						return;

					var shellPage = Ioc.Default.GetRequiredService<IContentPageContext>().ShellPage;
					if (shellPage is null)
						return;

					if (itemModel.IsBackMode)
					{
						// Remove all entries after the target entry in the BackwardStack
						while (shellPage.BackwardStack.Last() != itemModel.PageStackEntry)
						{
							shellPage.BackwardStack.RemoveAt(shellPage.BackwardStack.Count - 1);
						}

						// Navigate back
						shellPage.Back_Click();
					}
					else
					{
						// Remove all entries before the target entry in the ForwardStack
						while (shellPage.ForwardStack.First() != itemModel.PageStackEntry)
						{
							shellPage.ForwardStack.RemoveAt(0);
						}

						// Navigate forward
						shellPage.Forward_Click();
					}
				}
			}
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private async void Omnibar_QuerySubmitted(Omnibar sender, OmnibarQuerySubmittedEventArgs args)
		{
			if (ViewModel is not { } viewModel)
				return;

			var mode = Omnibar.CurrentSelectedMode;

			// A directory entered into the search editor is navigation, regardless of search mode.
			if (mode != OmnibarCommandPaletteMode)
			{
				string? directory = null;
				try
				{
					directory = DialogSelectionResolver.ResolvePath(args.Text, ContentPageContext.ShellPage?.ShellViewModel?.WorkingDirectory);
				}
				catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or NotSupportedException)
				{
					App.Logger.LogDebug(ex, "Invalid directory submitted in Omnibar");
				}
				if (directory is not null && System.IO.Directory.Exists(directory))
				{
					_autoSearchCTS?.Cancel();
					await viewModel.HandleItemNavigationAsync(directory);
					return;
				}
			}

			// Path mode
			if (mode == OmnibarPathMode)
			{
				var submittedText = args.Text;
				if (!await viewModel.HandleItemNavigationAsync(submittedText))
				{
					RestoreOmnibarText(viewModel, OmnibarPathMode, submittedText);
					return;
				}

				ContentPageContext.ShellPage?.PaneHolder?.FocusActivePane();
				return;
			}

			// Command palette mode
			else if (mode == OmnibarCommandPaletteMode)
			{
				var item = args.Item as NavigationBarSuggestionItem;

				// Try invoking built-in command
				foreach (var command in Commands)
				{
					if (command == Commands.None)
						continue;

					if (!string.Equals(command.Description, item?.Text, StringComparison.OrdinalIgnoreCase) &&
						!string.Equals(command.Description, args.Text, StringComparison.OrdinalIgnoreCase))
						continue;

					await command.ExecuteAsync();
					var paneHolder = ContentPageContext.ShellPage.GetRequiredPaneHolder();
					paneHolder.FocusActivePane();
					return;
				}

				var submittedCommand = args.Text;
				await DialogDisplayHelper.ShowDialogAsync(Strings.InvalidCommand.GetLocalizedResource(),
					string.Format(Strings.InvalidCommandContent.GetLocalizedResource(), submittedCommand));

				RestoreOmnibarText(viewModel, OmnibarCommandPaletteMode, submittedCommand);
				return;
			}

			// Search mode
			else if (mode == OmnibarSearchMode)
			{
				_autoSearchCTS?.Cancel();
				var shellPage = ContentPageContext.ShellPage;

				// Settings search: jump to the matching card, or apply the query in-page.
				if (NaiImageSearchIntent.Parse(args.Text) is { } imageSearch)
				{
					var folder = ContentPageContext.ShellPage?.ShellViewModel?.WorkingDirectory;
					NaiWorkspaceRoutes.OpenVision(folder, imageSearch.AllCached ? "vision: cache:" + imageSearch.Query : imageSearch.Query);
					return;
				}
				if (ContentPageContext.ShellPage?.ShellViewModel?.WorkingDirectory == NaiWorkspaceRoutes.Vision)
				{
					var workspace = (Ioc.Default.GetRequiredService<IMultiPanesContext>().ActivePane as FrameworkElement)?.FindDescendant<Files.App.Views.NaiVisionPage>();
					workspace?.ApplySearchQuery(args.Text);
					return;
				}
				if (NaiWorkspaceRoutes.IsRoute(shellPage?.ShellViewModel?.WorkingDirectory))
				{
					viewModel.OmnibarSearchModeText = string.Empty;
					Omnibar.IsFocused = false;
					return;
				}
				if (viewModel.InstanceViewModel.IsPageTypeSettings)
				{
					var settingsPage = (MainWindow.Instance.Content as FrameworkElement)?.FindDescendant<Files.App.Views.SettingsPage>();

					if (args.Item is SuggestionModel { SettingsResult: { } settingsResult } && settingsPage is not null)
					{
						await settingsPage.JumpToSearchResultAsync(settingsResult);
					}
					else
					{
						settingsPage?.ApplySearchQuery(args.Text);
					}

					viewModel.OmnibarSearchModeText = string.Empty;
					Omnibar.IsFocused = false;
					if (shellPage is not null)
					{
						var paneHolder = shellPage.GetRequiredPaneHolder();
						paneHolder.FocusActivePane();
					}
					return;
				}

				// An open tab matched by name: switch to it instead of searching or navigating
				if (args.Item is SuggestionModel { TabItem: { } tabItem })
				{
					var index = MainPageViewModel.AppInstances.IndexOf(tabItem);
					if (index >= 0)
						App.AppModel.TabStripSelectedIndex = index;

					viewModel.OmnibarSearchModeText = string.Empty;
					Omnibar.IsFocused = false;
					ContentPageContext.ShellPage?.PaneHolder?.FocusActivePane();
					return;
				}

				if (args.Item is SuggestionModel item && !string.IsNullOrWhiteSpace(item.ItemPath) && shellPage is not null)
					await NavigationHelpers.OpenPath(item.ItemPath, shellPage);
				else
				{
					var searchQuery = args.Item is SuggestionModel { IsSearchQuery: false } x && !string.IsNullOrWhiteSpace(x.Name)
						? x.Name
						: args.Text;

					if (viewModel.NaiSemanticMode && !NaiSemanticProtocol.TryParse(searchQuery, out _)) searchQuery = NaiSemanticProtocol.Prefix + " " + searchQuery;
					shellPage?.SubmitSearch(searchQuery); // use the resolved shellPage for consistency
					viewModel.SaveSearchQueryToList(searchQuery);
				}

				// The shell page can be torn down during the awaited navigation above; skip focusing if its pane holder is gone
				ContentPageContext.ShellPage?.PaneHolder?.FocusActivePane();
				return;
			}
		}

		private void NaiSearchMode_Changed(object sender, SelectionChangedEventArgs e)
		{
			if (ViewModel is not { } model || NaiSearchModeBox is null) return;
			model.NaiSemanticMode = NaiSearchModeBox.SelectedIndex == 1;
		}
		private void NaiSemanticIndex_Click(object sender, RoutedEventArgs e)
		{
			if (ContentPageContext.ShellPage is { } host) NaiWorkspaceRoutes.OpenInPane(host, NaiWorkspaceRoutes.Semantic);
		}

		private async void Omnibar_TextChanged(Omnibar sender, OmnibarTextChangedEventArgs args)
		{
			if (args.Reason is not OmnibarTextChangeReason.UserInput)
				return;

			if (ViewModel is not { } viewModel)
				return;

			if (Omnibar.CurrentSelectedMode == OmnibarPathMode)
			{
				await DispatcherQueue.EnqueueOrInvokeAsync(viewModel.PopulateOmnibarSuggestionsForPathMode);
			}
			else if (Omnibar.CurrentSelectedMode == OmnibarCommandPaletteMode)
			{
				await DispatcherQueue.EnqueueOrInvokeAsync(viewModel.PopulateOmnibarSuggestionsForCommandPaletteMode);
			}
			else if (Omnibar.CurrentSelectedMode == OmnibarSearchMode)
			{
				await DispatcherQueue.EnqueueOrInvokeAsync(viewModel.PopulateOmnibarSuggestionsForSearchMode);
				ScheduleAutoSearch(viewModel);
			}
		}

		private void ScheduleAutoSearch(NavigationToolbarViewModel viewModel)
		{
			// Auto-submit after typing pauses, for both fuzzy and semantic modes.
			// The paired engine (LUSI first, Kelvin fallback) routes the semantic
			// query to whatever execution device it scores; the client only
			// submits, never pre-diagnoses placement.
			_autoSearchCTS?.Cancel();
			_autoSearchCTS = new CancellationTokenSource();
			var token = _autoSearchCTS.Token;
			var text = viewModel.OmnibarSearchModeText;
			var instance = viewModel.InstanceViewModel;
			_ = Task.Run(async () =>
			{
				try { await Task.Delay(TimeSpan.FromMilliseconds(450), token); }
				catch (OperationCanceledException) { return; }
				if (token.IsCancellationRequested) return;
				var query = text?.Trim();
				if (string.IsNullOrWhiteSpace(query)) return;
				if (viewModel.NaiSemanticMode && !NaiSemanticProtocol.TryParse(query, out _)) query = NaiSemanticProtocol.Prefix + " " + query;
				if (string.Equals(query, instance?.CurrentSearchQuery, StringComparison.Ordinal)) return;
				await DispatcherQueue.EnqueueOrInvokeAsync(async () =>
				{
					if (token.IsCancellationRequested) return;
					if (string.Equals(query, instance?.CurrentSearchQuery, StringComparison.Ordinal)) return;
					ContentPageContext.ShellPage?.SubmitSearch(query);
				});
			}, token);
		}

		private async void BreadcrumbBar_ItemClicked(Controls.BreadcrumbBar sender, Controls.BreadcrumbBarItemClickedEventArgs args)
		{
			var viewModel = ViewModel
				?? throw new InvalidOperationException("The navigation toolbar does not have a view model.");

			if (args.IsRootItem)
			{
				await viewModel.HandleItemNavigationAsync("Home");
				return;
			}

			// Validate index before accessing the collection
			if (args.Index < 0 || args.Index >= viewModel.PathComponents.Count)
				return;

			// Navigation to the current folder should not happen
			if (args.Index == viewModel.PathComponents.Count - 1 ||
				viewModel.PathComponents[args.Index].Path is not { } path)
				return;

			// If user clicked the item with middle mouse button, open it in new tab
			var openInNewTab = args.PointerRoutedEventArgs?.GetCurrentPoint(null).Properties.PointerUpdateKind is PointerUpdateKind.MiddleButtonReleased;
			await viewModel.HandleFolderNavigationAsync(path, openInNewTab);
		}

		private async void BreadcrumbBar_ItemDropDownFlyoutOpening(object sender, BreadcrumbBarItemDropDownFlyoutEventArgs e)
		{
			if (e.IsRootItem)
			{
				IHomeFolder homeFolder = new HomeFolder();
				IContentPageContext contentPageContext = Ioc.Default.GetRequiredService<IContentPageContext>();

				e.Flyout.Items.Add(new MenuFlyoutHeaderItem() { Text = Strings.QuickAccess.GetLocalizedResource() });

				await foreach (var storable in homeFolder.GetQuickAccessFolderAsync())
				{
					if (storable is not IWindowsStorable windowsStorable)
						continue;

					var flyoutItem = new MenuFlyoutItem()
					{
						Text = windowsStorable.GetDisplayName(Windows.Win32.UI.Shell.SIGDN.SIGDN_PARENTRELATIVEFORUI),
						DataContext = windowsStorable.GetDisplayName(Windows.Win32.UI.Shell.SIGDN.SIGDN_DESKTOPABSOLUTEPARSING),
						Icon = new FontIcon() { Glyph = "\uE8B7" }, // As a placeholder
					};

					e.Flyout.Items.Add(flyoutItem);

					windowsStorable.TryGetThumbnail((int)(16f * App.AppModel.AppWindowDPI), Windows.Win32.UI.Shell.SIIGBF.SIIGBF_ICONONLY, out var thumbnailData);
					flyoutItem.Icon = new ImageIcon() { Source = await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() => thumbnailData.ToBitmapAsync(), Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal) };

					windowsStorable.Dispose();

					flyoutItem.Click += (sender, args) =>
					{
						// NOTE: We should not pass a path string but pass the storable object itself in the future.
						var path = flyoutItem.DataContext as string
							?? throw new InvalidOperationException("The breadcrumb item does not have a navigation path.");
						var shellPage = contentPageContext.ShellPage
							?? throw new InvalidOperationException("There is no active shell page for breadcrumb navigation.");
						shellPage.NavigateToPath(path);
					};
				}

				e.Flyout.Items.Add(new MenuFlyoutHeaderItem() { Text = Strings.Drives.GetLocalizedResource() });

				await foreach (var storable in homeFolder.GetLogicalDrivesAsync())
				{
					if (storable is not IWindowsStorable windowsStorable)
						continue;

					var flyoutItem = new MenuFlyoutItem()
					{
						Text = windowsStorable.GetDisplayName(Windows.Win32.UI.Shell.SIGDN.SIGDN_PARENTRELATIVEFORUI),
						DataContext = windowsStorable.GetDisplayName(Windows.Win32.UI.Shell.SIGDN.SIGDN_DESKTOPABSOLUTEPARSING),
						Icon = new FontIcon() { Glyph = "\uE8B7" }, // As a placeholder
					};

					e.Flyout.Items.Add(flyoutItem);

					windowsStorable.TryGetThumbnail((int)(16f * App.AppModel.AppWindowDPI), Windows.Win32.UI.Shell.SIIGBF.SIIGBF_ICONONLY, out var thumbnailData);
					flyoutItem.Icon = new ImageIcon() { Source = await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() => thumbnailData.ToBitmapAsync(), Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal) };

					windowsStorable.Dispose();

					flyoutItem.Click += (sender, args) =>
					{
						// NOTE: We should not pass a path string but pass the storable object itself in the future.
						var path = flyoutItem.DataContext as string
							?? throw new InvalidOperationException("The breadcrumb item does not have a navigation path.");
						var shellPage = contentPageContext.ShellPage
							?? throw new InvalidOperationException("There is no active shell page for breadcrumb navigation.");
						shellPage.NavigateToPath(path);
					};
				}

				return;
			}

			var viewModel = ViewModel
				?? throw new InvalidOperationException("The navigation toolbar does not have a view model.");
			if (e.Index < 0 || e.Index >= viewModel.PathComponents.Count)
				return;

			await viewModel.SetPathBoxDropDownFlyoutAsync(e.Flyout, viewModel.PathComponents[e.Index]);
		}

		private void BreadcrumbBar_ItemDropDownFlyoutClosed(object sender, BreadcrumbBarItemDropDownFlyoutEventArgs e)
		{
			// Clear the flyout items to save memory
			e.Flyout.Items.Clear();
		}

		/// <summary>
		/// Handles mode changes in the Omnibar control. This event can fire even when the Omnibar
		/// already has focus (e.g., user switching from Command Palette to Search mode).
		/// Updates the appropriate text property and populates suggestions based on the new mode.
		/// </summary>
		private async void Omnibar_ModeChanged(object sender, OmnibarModeChangedEventArgs e)
		{
			if (ViewModel is not { } viewModel)
				return;

			var pendingText = TakePendingOmnibarText(e.NewMode);

			if (e.NewMode == OmnibarPathMode)
			{
				// Initialize with current working directory or fallback to home path
				viewModel.PathText = pendingText ?? GetDefaultPathText();

				await DispatcherQueue.EnqueueOrInvokeAsync(viewModel.PopulateOmnibarSuggestionsForPathMode);
			}
			else if (e.NewMode == OmnibarCommandPaletteMode)
			{
				// Restore rejected input or clear text, then load command suggestions
				viewModel.OmnibarCommandPaletteModeText = pendingText ?? string.Empty;

				await DispatcherQueue.EnqueueOrInvokeAsync(viewModel.PopulateOmnibarSuggestionsForCommandPaletteMode);
			}
			else if (e.NewMode == OmnibarSearchMode)
			{
				// Preserve existing search query or clear for new search
				if (!viewModel.InstanceViewModel.IsPageTypeSearchResults)
					viewModel.OmnibarSearchModeText = string.Empty;
				else
					viewModel.OmnibarSearchModeText = viewModel.InstanceViewModel.CurrentSearchQuery;

				await DispatcherQueue.EnqueueOrInvokeAsync(viewModel.PopulateOmnibarSuggestionsForSearchMode);
			}
		}

		/// <summary>
		/// Handles focus state changes for the Omnibar control.
		/// When focused: Updates Path Mode content (Path Mode has both focused/unfocused states).
		/// When unfocused: Automatically switches back to Path Mode to display the BreadcrumbBar.
		/// </summary>
		private async void Omnibar_IsFocusedChanged(Omnibar sender, OmnibarIsFocusedChangedEventArgs args)
		{
			if (args.IsFocused)
			{
				if (ViewModel is not { } viewModel)
					return;

				// Path Mode needs special handling when gaining focus since it has an unfocused state
				if (Omnibar.CurrentSelectedMode == OmnibarPathMode)
				{
					viewModel.PathText = TakePendingOmnibarText(OmnibarPathMode) ?? GetDefaultPathText();

					await DispatcherQueue.EnqueueOrInvokeAsync(viewModel.PopulateOmnibarSuggestionsForPathMode);
				}
			}
			else
			{
				_pendingOmnibarText = null;

				if (Omnibar.CurrentSelectedMode == OmnibarSearchMode)
				{
					ViewModel?.CancelSuggestionSearch();
				}

				// When Omnibar loses focus, revert to Path Mode to display BreadcrumbBar
				Omnibar.CurrentSelectedMode = OmnibarPathMode;
			}
		}

		private void RestoreOmnibarText(NavigationToolbarViewModel viewModel, OmnibarMode mode, string text)
		{
			_pendingOmnibarText = (mode, text);

			if (Omnibar.CurrentSelectedMode != mode)
				Omnibar.CurrentSelectedMode = mode;
			else if (mode == OmnibarPathMode)
				viewModel.PathText = text;
			else if (mode == OmnibarCommandPaletteMode)
				viewModel.OmnibarCommandPaletteModeText = text;

			Omnibar.FocusWithCaretAtEnd();
		}

		private string? TakePendingOmnibarText(OmnibarMode mode)
		{
			var pending = _pendingOmnibarText;
			_pendingOmnibarText = null;
			return pending?.Mode == mode ? pending.Value.Text : null;
		}

		private string GetDefaultPathText()
		{
			var workingDirectory = ContentPageContext.ShellPage?.ShellViewModel?.WorkingDirectory;
			return string.IsNullOrEmpty(workingDirectory)
				? Constants.UserEnvironmentPaths.HomePath
				: workingDirectory;
		}

		private void Omnibar_FocusRedirectRequested(Omnibar sender, EventArgs args)
		{
			// The omnibar TextBox regained focus on window reactivation; move focus to the active pane so we don't land in
			// edit mode. Deferred so it runs after the in-progress focus change settles.
			_pendingOmnibarText = null;
			DispatcherQueue.TryEnqueue(() => ContentPageContext.ShellPage?.PaneHolder?.FocusActivePane());
		}

		private async void Omnibar_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (e.Key is VirtualKey.Escape)
			{
				_autoSearchCTS?.Cancel();
				Omnibar.IsFocused = false;
				var paneHolder = ContentPageContext.ShellPage?.PaneHolder;
				paneHolder?.FocusActivePane();
			}
			else if (e.Key is VirtualKey.Tab && Omnibar.IsFocused && !InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down))
			{
				var currentSelectedMode = Omnibar.CurrentSelectedMode;
				Omnibar.IsFocused = false;
				await Task.Delay(15);

				if (currentSelectedMode == OmnibarPathMode)
					BreadcrumbBar.Focus(FocusState.Keyboard);
				else if (currentSelectedMode == OmnibarCommandPaletteMode)
					OmnibarCommandPaletteMode.Focus(FocusState.Keyboard);
				else if (currentSelectedMode == OmnibarSearchMode)
					OmnibarSearchMode.Focus(FocusState.Keyboard);
			}
		}

		[DynamicWindowsRuntimeCast(typeof(TextBox))]
		private void NavigationButtonOverflowFlyoutButton_LosingFocus(UIElement sender, LosingFocusEventArgs args)
		{
			// Prevent the Omnibar from taking focus if the overflow button is hidden while the button is focused
			if (args.NewFocusedElement is TextBox)
				args.Cancel = true;
		}

		private void BreadcrumbBarItem_DragLeave(object sender, DragEventArgs e)
		{
			var viewModel = ViewModel
				?? throw new InvalidOperationException("The navigation toolbar does not have a view model.");
			viewModel.PathBoxItem_DragLeave(sender, e);
		}

		private async void BreadcrumbBarItem_DragOver(object sender, DragEventArgs e)
		{
			var viewModel = ViewModel
				?? throw new InvalidOperationException("The navigation toolbar does not have a view model.");
			await viewModel.PathBoxItem_DragOver(sender, e);
		}

		private async void BreadcrumbBarItem_Drop(object sender, DragEventArgs e)
		{
			var viewModel = ViewModel
				?? throw new InvalidOperationException("The navigation toolbar does not have a view model.");
			await viewModel.PathBoxItem_Drop(sender, e);
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		[DynamicWindowsRuntimeCast(typeof(Style))]
		private void BreadcrumbBarItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
		{
			if (sender is not FrameworkElement element || element.DataContext is not PathBoxItem pathBoxItem)
				return;

			var path = pathBoxItem.Path;
			if (string.IsNullOrEmpty(path))
				return;

			var flyout = new MenuFlyout();

			var openInNewTabItem = new MenuFlyoutItemWithThemedIcon
			{
				Text = Strings.OpenInNewTab.GetLocalizedResource(),
				ThemedIconStyle = (Style)Application.Current.Resources["App.ThemedIcons.OpenInTab"],
			};
			openInNewTabItem.Click += async (_, _) =>
				await NavigationHelpers.AddNewTabByPathAsync(typeof(ShellPanesPage), path, true);

			var openInNewWindowItem = new MenuFlyoutItemWithThemedIcon
			{
				Text = Strings.OpenInNewWindow.GetLocalizedResource(),
				ThemedIconStyle = (Style)Application.Current.Resources["App.ThemedIcons.OpenInWindow"],
			};
			openInNewWindowItem.Click += async (_, _) =>
				await NavigationHelpers.OpenPathInNewWindowAsync(path);

			flyout.Items.Add(openInNewTabItem);
			flyout.Items.Add(openInNewWindowItem);

			var paneHolder = ContentPageContext.ShellPage?.PaneHolder;
			if (userSettingsService.GeneralSettingsService.ShowOpenInNewPane && paneHolder is not null)
			{
				if (ContentPageContext.IsMultiPaneActive)
				{
					var openInOtherPaneItem = new MenuFlyoutItem
					{
						Text = Strings.OpenInOtherPane.GetLocalizedResource(),
					};
					openInOtherPaneItem.Click += (_, _) => paneHolder.OpenInOtherPane(path);
					flyout.Items.Add(openInOtherPaneItem);
				}
				else if (ContentPageContext.IsMultiPaneAvailable)
				{
					var openInNewPaneSubItem = new MenuFlyoutSubItem
					{
						Text = Strings.OpenInNewPane.GetLocalizedResource(),
					};

					MenuFlyoutItemWithThemedIcon CreateOpenInPaneItem(string text, string iconStyle, ShellPaneArrangement arrangement)
					{
						var item = new MenuFlyoutItemWithThemedIcon
						{
							Text = text,
							ThemedIconStyle = (Style)Application.Current.Resources[iconStyle],
						};
						item.Click += (_, _) => paneHolder.OpenSecondaryPane(path, arrangement);
						return item;
					}

					openInNewPaneSubItem.Items.Add(CreateOpenInPaneItem(Strings.SplitPaneVertically.GetLocalizedResource(), "App.ThemedIcons.OpenInPaneVertical", ShellPaneArrangement.Vertical));
					openInNewPaneSubItem.Items.Add(CreateOpenInPaneItem(Strings.SplitPaneHorizontally.GetLocalizedResource(), "App.ThemedIcons.OpenInPaneHorizontal", ShellPaneArrangement.Horizontal));
					flyout.Items.Add(openInNewPaneSubItem);
				}
			}

			flyout.ShowAt(element, new FlyoutShowOptions { Position = e.GetPosition(element) });
			e.Handled = true;
		}
	}
}
