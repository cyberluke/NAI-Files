// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.WinUI;
using Files.App.Dialogs;
using Files.App.Services.Pulse;
using System.IO;
using Microsoft.Extensions.Logging;
using Files.App.UserControls.Pulse;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using WinRT;

namespace Files.App.Views.Layouts
{
	/// <summary>
	/// Pulse view: a chronological media feed (newest first). Each day is a
	/// <see cref="PulseSectionView"/> (header + <see cref="ItemsView"/> with <see cref="LinedFlowLayout"/>)
	/// stacked in a vertical repeater. A hidden ListView keeps the base layout's selection/rename
	/// plumbing (context menu, commands, keyboard, status bar) so no file operation is reimplemented.
	/// </summary>
	public sealed partial class PulseLayoutPage : BaseGroupableLayoutPage
	{
		// Fields

		private readonly PulseFeedBuilder feedBuilder;
		private readonly HoverPreviewCoordinator hoverPreview;
		private PulseFfmpeg? ffmpeg;
		private readonly PulseWaveformService waveforms;
		private readonly PulseAudioPlayer audioPlayer;
		private readonly Dictionary<ListedItem, CancellationTokenSource> markdownLoads = new(ReferenceEqualityComparer.Instance);
		private readonly List<PulseSectionView> sectionViews = [];

		// Realized card roots, registered from Card_Loaded. Lets rename/focus reach a card's elements without
		// walking and casting the native visual tree (which is fragile under NativeAOT).
		private readonly Dictionary<PulseCardViewModel, FrameworkElement> cardRoots = new(ReferenceEqualityComparer.Instance);

		private bool isSyncingSelection;

		/// <summary>Anchor of the last plain click; Shift+click extends the selection from it (Explorer semantics).</summary>
		private ListedItem? selectionAnchor;

		// Rubber-band selection state: a left press on empty space arms the marquee. It only activates
		// for a deliberate drag — the pointer must cross the movement threshold AND the button must stay
		// down for the activation delay — so a plain click (even with mouse jitter) can never draw a box.

		private const double MarqueeActivationThreshold = 4;
		private const double MarqueeEdgeScrollZone = 48;
		private const double MarqueeEdgeScrollStep = 24;

		private bool isMarqueeArmed;
		private bool isMarqueeActive;
		private bool isMarqueeCaptured;
		private bool marqueeCtrl;
		private Windows.Foundation.Point marqueeStartPoint;
		private Windows.Foundation.Point marqueeLastPoint;
		private Windows.Foundation.Rect marqueeRect;
		private HashSet<ListedItem>? marqueeSelection;
		private HashSet<ListedItem>? marqueeExistingSelection;
		private Microsoft.UI.Dispatching.DispatcherQueueTimer? marqueeScrollTimer;
		private int marqueeScrollDirection;

		// Properties

		protected override ListViewBase ListViewBase => FileList;
		protected override SemanticZoom RootZoom => RootPulseZoom;

		public ObservableCollection<PulseSection> Feed => feedBuilder.Feed;

		/// <summary>Target line height of the flow galleries; wide images grow horizontally to keep this height.</summary>
		public double LineHeight => 184;

		public bool IsAllFilter => feedBuilder.Filter is PulseFilter.All;
		public bool IsPhotosFilter => feedBuilder.Filter is PulseFilter.Photos;
		public bool IsVideosFilter => feedBuilder.Filter is PulseFilter.Videos;
		public bool IsAudioFilter => feedBuilder.Filter is PulseFilter.Audio;
		public bool IsMarkdownFilter => feedBuilder.Filter is PulseFilter.Markdown;
		public bool IsDocumentsFilter => feedBuilder.Filter is PulseFilter.Documents;
		public bool IsArchivesFilter => feedBuilder.Filter is PulseFilter.Archives;

		// Constructor

		public PulseLayoutPage() : base()
		{
			InitializeComponent();
			DataContext = this;

			// End the gesture even when a child (scroller, container) handled the event first:
			// a missed release used to leave the marquee armed and the next hover drew a box.
			AddHandler(PointerReleasedEvent, new PointerEventHandler(PageRoot_PointerReleased), true);
			AddHandler(PointerCanceledEvent, new PointerEventHandler(PageRoot_PointerCanceled), true);
			AddHandler(PointerCaptureLostEvent, new PointerEventHandler(PageRoot_PointerCaptureLost), true);

			feedBuilder = new PulseFeedBuilder(DispatcherQueue)
			{
				AutoGroupVersions = LayoutSettingsService.PulseAutoGroupVersions,
			};
			feedBuilder.FeedRebuilt += FeedBuilder_FeedRebuilt;
			hoverPreview = new HoverPreviewCoordinator(DispatcherQueue);
			waveforms = new PulseWaveformService(DispatcherQueue);
			audioPlayer = new PulseAudioPlayer(DispatcherQueue);
			audioPlayer.PlaybackFailed += AudioPlayer_PlaybackFailed;

			LayoutSettingsService.PropertyChanged += LayoutSettingsService_PropertyChanged;
			App.AppModel.PropertyChanging += AppModel_PropertyChanging;
		}

		private void LayoutSettingsService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(ILayoutSettingsService.PulseAutoGroupVersions))
				feedBuilder.AutoGroupVersions = LayoutSettingsService.PulseAutoGroupVersions;
		}

		private void AppModel_PropertyChanging(object? sender, PropertyChangingEventArgs e)
		{
			// Raised before the selected tab changes, i.e. before MainPage swaps this tab's content out of the tree
			if (e.PropertyName is nameof(AppModel.TabStripSelectedIndex))
				RelocateFocusBeforeLeave();
		}

		/// <summary>
		/// Moves keyboard focus out of this page onto the current tab's header right before the page leaves the
		/// visual tree (tab switch, layout switch, tab close). XAML otherwise hands focus from the leaving control
		/// to its next focusable descendant and brings it into view while the subtree is already half detached;
		/// for elements under an ItemsView that scroller walk reaches a detached ancestor and faults
		/// (FocusProperties::IsScrollable). The incoming content takes focus back through FocusFileList.
		/// </summary>
		[DynamicWindowsRuntimeCast(typeof(Frame))]
		[DynamicWindowsRuntimeCast(typeof(Control))]
		private void RelocateFocusBeforeLeave()
		{
			try
			{
				if (App.AppModel.IsMainWindowClosed || !IsLoaded || XamlRoot is null)
					return;

				if (FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused || !IsInsideThisPage(focused))
					return;

				var index = App.AppModel.TabStripSelectedIndex;
				if (index < 0 || index >= MainPageViewModel.AppInstances.Count)
					return;

				if (MainWindow.Instance.Content is Frame { Content: MainPage mainPage } &&
					mainPage.ViewModel.MultitaskingControl is BaseTabBar tabBar &&
					tabBar.ContainerFromItem(MainPageViewModel.AppInstances[index]) is Control tabHeader)
				{
					tabHeader.Focus(FocusState.Programmatic);
				}
			}
			catch
			{
				// The window may be closing; losing this relocation only matters while the tree is alive
			}
		}

		private bool IsInsideThisPage(DependencyObject element)
		{
			for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
			{
				if (ReferenceEquals(current, this))
					return true;
			}

			return false;
		}

		// Navigation

		protected override void OnNavigatedTo(NavigationEventArgs eventArgs)
		{
			if (eventArgs.Parameter is NavigationArguments navArgs)
				navArgs.FocusOnNavigation = true;

			base.OnNavigatedTo(eventArgs);

			var parentShellPage = ParentShellPageInstance
				?? throw new InvalidOperationException("The pulse layout must be associated with a shell page.");
			var shellViewModel = parentShellPage.GetRequiredShellViewModel();

			// The hidden list mirrors the real collection so selection, rename and commands keep working
			FileList.ItemsSource ??= shellViewModel.FilesAndFolders;
			feedBuilder.SetSource(shellViewModel.FilesAndFolders);

			HookTimeRail();

			if (eventArgs.Parameter is NavigationArguments { IsLayoutSwitch: true })
				ReloadItemIcons();
		}

		private bool isTimeRailHooked;

		private void HookTimeRail()
		{
			if (isTimeRailHooked)
				return;

			FeedScroller.ViewChanged += FeedScroller_ViewChanged;
			isTimeRailHooked = true;
			UpdateTimeRailProgress();
		}

		private void FeedScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
		{
			UpdateTimeRailProgress();
		}

		private void UpdateTimeRailProgress()
		{
			if (TimeRail is null)
				return;

			// Map the feed's scroll position onto the rail: 0 at the top day, 1 at the newest scrolled-to day.
			var scrollable = FeedScroller.ScrollableHeight;
			var ratio = scrollable <= 0 ? 0 : FeedScroller.VerticalOffset / scrollable;
			TimeRail.SetActiveProgress(ratio);
		}

		protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
		{
			// Layout switch / navigation replaces this page in the frame
			RelocateFocusBeforeLeave();
			base.OnNavigatingFrom(e);
			hoverPreview.Reset();
		}

		public override void Dispose()
		{
			RelocateFocusBeforeLeave();
			StopMarqueeEdgeScroll();
			App.AppModel.PropertyChanging -= AppModel_PropertyChanging;
			Bindings.StopTracking();
			LayoutSettingsService.PropertyChanged -= LayoutSettingsService_PropertyChanged;
			feedBuilder.FeedRebuilt -= FeedBuilder_FeedRebuilt;
			hoverPreview.Dispose();
			waveforms.Dispose();
			audioPlayer.PlaybackFailed -= AudioPlayer_PlaybackFailed;
			audioPlayer.Dispose();
			CancelMarkdownLoads();
			feedBuilder.Dispose();
			SectionsRepeater.ItemsSource = null;
			if (isTimeRailHooked)
			{
				FeedScroller.ViewChanged -= FeedScroller_ViewChanged;
				isTimeRailHooked = false;
			}
			base.Dispose();
		}

		private void ReloadItemIcons()
		{
			if (ParentShellPageInstance is null)
				return;

			// A layout switch changes the thumbnail size (Pulse wants full previews): drop the cached state
			// so realized cards request thumbnails again at the new size
			var shellViewModel = ParentShellPageInstance.GetRequiredShellViewModel();
			shellViewModel.CancelExtendedPropertiesLoading();

			foreach (var listedItem in shellViewModel.FilesAndFolders.ToList())
				listedItem.ItemPropertiesInitialized = false;
		}

		// Feed

		private void FeedBuilder_FeedRebuilt(object? sender, EventArgs e)
		{
			hoverPreview.Reset();
			TimeRail.Sections = feedBuilder.Sections;
			SyncSelectionToFeed();

			// The playing track's file left the folder (navigation, deletion): nothing on screen represents it anymore
			if (audioPlayer.ActiveCard is { IsDetached: true })
				audioPlayer.Stop();
		}

		private void FilterChip_Click(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<PulseFilter>(tag, out var filter))
			{
				feedBuilder.Filter = filter;
				NotifyPropertyChanged(nameof(IsAllFilter));
				NotifyPropertyChanged(nameof(IsPhotosFilter));
				NotifyPropertyChanged(nameof(IsVideosFilter));
				NotifyPropertyChanged(nameof(IsAudioFilter));
				NotifyPropertyChanged(nameof(IsMarkdownFilter));
				NotifyPropertyChanged(nameof(IsDocumentsFilter));
				NotifyPropertyChanged(nameof(IsArchivesFilter));
			}
		}

		private void SectionsRepeater_Loaded(object sender, RoutedEventArgs e)
		{
			SectionsRepeater.ItemsSource ??= Feed;
			TimeRail.Sections = feedBuilder.Sections;
		}

		private void SectionsRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
		{
			if (args.Element is not PulseSectionView view)
				return;

			view.CardTemplate = Resources["PulseFeedTemplateSelector"];
			view.LineHeight = LineHeight;
			if (!sectionViews.Contains(view))
				sectionViews.Add(view);

			view.SyncSelection(new HashSet<ListedItem>(FileList.SelectedItems.OfType<ListedItem>(), ReferenceEqualityComparer.Instance));
		}

		private void SectionsRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
		{
			if (args.Element is not PulseSectionView view)
				return;

			sectionViews.Remove(view);
		}

		// Selection: the hidden list is the single source of truth. Every input gesture (pointer
		// press on a card, empty-space press, keyboard, commands) mutates FileList.SelectedItems,
		// whose SelectionChanged mirrors the state onto the cards through SyncSelectionToFeed.

		protected override void FileList_SelectionChanged(object sender, SelectionChangedEventArgs? e)
		{
			base.FileList_SelectionChanged(sender, e);

			// Selection changed through the hidden list (commands, keyboard, manipulation model): mirror it to the cards
			if (!isSyncingSelection)
				SyncSelectionToFeed();
		}

		protected override void OnSelectionChanged(SelectionChangedEventArgs e)
		{
		}

		private void SyncSelectionToFeed()
		{
			if (isSyncingSelection)
				return;

			isSyncingSelection = true;
			try
			{
				var selectedItems = new HashSet<ListedItem>(FileList.SelectedItems.OfType<ListedItem>(), ReferenceEqualityComparer.Instance);
				foreach (var view in sectionViews)
					view.SyncSelection(selectedItems);
			}
			finally
			{
				isSyncingSelection = false;
			}
		}

		protected override void ItemManipulationModel_AddSelectedItemInvoked(object? sender, ListedItem e)
		{
			if ((NextRenameIndex != 0 && TryStartRenameNextItem(e)) || !FileList.Items.Contains(e))
				return;

			FileList.SelectedItems.Add(e);
			SyncSelectionToFeed();
		}

		protected override void ItemManipulationModel_RemoveSelectedItemInvoked(object? sender, ListedItem e)
		{
			if (FileList.Items.Contains(e))
				FileList.SelectedItems.Remove(e);
			SyncSelectionToFeed();
		}

		protected override void ItemManipulationModel_FocusSelectedItemsInvoked(object? sender, EventArgs e)
		{
			if (SelectedItems.Count == 0)
				return;

			var (view, index) = Locate(SelectedItems[^1]);
			if (view is not null && index >= 0)
			{
				view.ItemsView.StartBringItemIntoView(index, new BringIntoViewOptions { AnimationDesired = true });
				view.ItemsView.Focus(FocusState.Keyboard);
			}
		}

		protected override void ItemManipulationModel_ScrollIntoViewInvoked(object? sender, ListedItem e)
		{
			var (view, index) = Locate(e);
			if (view is not null && index >= 0)
			{
				view.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
				view.ItemsView.StartBringItemIntoView(index, new BringIntoViewOptions { AnimationDesired = true });
			}
		}

		protected override void ItemManipulationModel_ScrollToTopInvoked(object? sender, EventArgs e)
		{
			FeedScroller.ChangeView(null, 0, null, true);
		}

		private (PulseSectionView? View, int Index) Locate(ListedItem item)
		{
			foreach (var view in sectionViews)
			{
				var index = view.IndexOf(item);
				if (index >= 0)
					return (view, index);
			}
			return (null, -1);
		}

		protected override void ItemManipulationModel_SelectAllItemsInvoked(object? sender, EventArgs e)
		{
			base.ItemManipulationModel_SelectAllItemsInvoked(sender, e);
			SyncSelectionToFeed();
		}

		protected override void ItemManipulationModel_ClearSelectionInvoked(object? sender, EventArgs e)
		{
			base.ItemManipulationModel_ClearSelectionInvoked(sender, e);
			SyncSelectionToFeed();
		}

		[DynamicWindowsRuntimeCast(typeof(ItemContainer))]
		protected override bool CanGetItemFromElement(object element)
			=> element is ItemContainer;

		[DynamicWindowsRuntimeCast(typeof(ItemContainer))]
		protected override ListedItem? GetItemFromElement(object element)
		{
			if (element is ItemContainer container)
			{
				if ((container.Child as FrameworkElement)?.DataContext is PulseCardViewModel card)
					return card.Item;

				if (container.DataContext is PulseCardViewModel containerCard)
					return containerCard.Item;
			}
			else if (element is FrameworkElement { DataContext: PulseCardViewModel viewModel })
			{
				return viewModel.Item;
			}

			return base.GetItemFromElement(element);
		}

		// Containers

		[DynamicWindowsRuntimeCast(typeof(ItemContainer))]
		private void Card_Loaded(object sender, RoutedEventArgs e)
		{
			if (sender is not FrameworkElement root || root.DataContext is not PulseCardViewModel card)
				return;

			cardRoots[card] = root;

			var container = root.FindAscendant<ItemContainer>();
			if (container is not null)
				container.ContextFlyout = ItemContextMenuFlyout;

			// Drag out: CanDrag on the item container, falling back to the card root itself so a
			// missing/redirected container can never silently disable dragging. DropCompleted must
			// always be wired on the same element DragStarting is, or the window-front state leaks.
			var dragSurface = container ?? (UIElement)root;
			dragSurface.CanDrag = AllowItemDrag;
			dragSurface.DragStarting -= Container_DragStarting;
			dragSurface.DragStarting += Container_DragStarting;
			dragSurface.DropCompleted -= Container_DropCompleted;
			dragSurface.DropCompleted += Container_DropCompleted;
			if (container is not null)
				InitializeDrag(container, card.Item);
			else
				InitializeDrag(root, card.Item);

			// Same rich tooltip (name, type, size, dates) the other layouts put on their containers
			ToolTipService.SetToolTip(root, card.Item.ItemTooltipText);

			var shellViewModel = ParentShellPageInstance?.ShellViewModel;
			if (shellViewModel is null)
				return;

			card.Item.PropertyChanged -= Card_ItemPropertyChanged;
			card.Item.PropertyChanged += Card_ItemPropertyChanged;
			card.PropertyChanged -= Card_PropertyChanged;
			card.PropertyChanged += Card_PropertyChanged;

			if (card.Item.PreloadedIconData is not null && card.Item.FileImage is null)
				_ = shellViewModel.ApplyCachedThumbnailOrPreloadedIconAsync(card.Item);

			if (!card.Item.ItemPropertiesInitialized)
				_ = SafetyExtensions.IgnoreExceptions(() => shellViewModel.LoadExtendedItemPropertiesAsync(card.Item));

			if (card.IsMarkdown && card.MarkdownExcerpt is null)
				_ = LoadMarkdownExcerptAsync(card);

			if (card.IsAudio)
				waveforms.Request(card);
		}

		[DynamicWindowsRuntimeCast(typeof(ItemContainer))]
		private void Card_Unloaded(object sender, RoutedEventArgs e)
		{
			if (sender is not FrameworkElement root)
				return;

			if (root.DataContext is PulseCardViewModel card)
			{
				if (cardRoots.TryGetValue(card, out var registered) && ReferenceEquals(registered, root))
					cardRoots.Remove(card);

				card.Item.PropertyChanged -= Card_ItemPropertyChanged;
				card.PropertyChanged -= Card_PropertyChanged;
				hoverPreview.Leave(card);
				waveforms.Cancel(card);
				if (markdownLoads.Remove(card.Item, out var cts))
					cts.Cancel();
			}

			ToolTipService.SetToolTip(root, null);

			var container = root.FindAscendant<ItemContainer>();
			if (container is not null)
			{
				UninitializeDrag(container);
				container.DragStarting -= Container_DragStarting;
				container.DropCompleted -= Container_DropCompleted;
			}

			// The drag fallback surface mirrors the Loaded wiring (see Card_Loaded).
			if (container is null)
			{
				UninitializeDrag(root);
				root.DragStarting -= Container_DragStarting;
				root.DropCompleted -= Container_DropCompleted;
			}
		}

		private void Card_ItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			// Thumbnails arrive after cards are realized: the owning gallery re-asks for aspect ratios
			if (e.PropertyName is nameof(ListedItem.FileImage) && sender is ListedItem item)
				Locate(item).View?.QueueInvalidate();
		}

		private void Card_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			// The decoded picture size decides portrait/landscape boxes; it can land after the FileImage assignment
			if (e.PropertyName is nameof(PulseCardViewModel.ImageAspectRatio) && sender is PulseCardViewModel card)
				Locate(card.Item).View?.QueueInvalidate();
		}

		private void Container_DragStarting(UIElement sender, DragStartingEventArgs args)
		{
			var dragged = GetItemFromElement(sender);
			if (dragged is null)
			{
				args.Cancel = true;
				return;
			}

			// Dragging an unselected card drags just that card; otherwise the whole selection goes along
			var items = SelectedItems.Contains(dragged) ? SelectedItems.ToList() : [dragged];
			if (!TryStartItemsDrag(items, args.Data))
				args.Cancel = true;
		}

		private void Container_DropCompleted(UIElement sender, DropCompletedEventArgs args)
		{
			OnItemsDragCompleted();
		}

		private async Task LoadMarkdownExcerptAsync(PulseCardViewModel card)
		{
			if (markdownLoads.Remove(card.Item, out var previous))
				previous.Cancel();

			var cts = new CancellationTokenSource();
			markdownLoads[card.Item] = cts;
			try
			{
				var excerpt = await PulseMarkdownExcerptReader.ReadAsync(card.Item.GetRequiredPath(), cts.Token);
				if (!cts.IsCancellationRequested)
					card.MarkdownExcerpt = excerpt;
			}
			catch (OperationCanceledException)
			{
			}
			finally
			{
				if (markdownLoads.TryGetValue(card.Item, out var current) && ReferenceEquals(current, cts))
					markdownLoads.Remove(card.Item);
				cts.Dispose();
			}
		}

		private void CancelMarkdownLoads()
		{
			foreach (var cts in markdownLoads.Values)
				cts.Cancel();
			markdownLoads.Clear();
		}

		// Pointer / activation

		private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
		{
			if (sender is FrameworkElement root && root.DataContext is PulseCardViewModel { IsVideo: true } card
				&& root.FindDescendant<MediaPlayerElement>() is { } host)
			{
				hoverPreview.Enter(card, host);
			}
		}

		private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
		{
			if (sender is FrameworkElement root && root.DataContext is PulseCardViewModel card)
				hoverPreview.Leave(card);
		}

		private void Card_PointerMoved(object sender, PointerRoutedEventArgs e)
		{
			if (sender is FrameworkElement root && root.DataContext is PulseCardViewModel { IsHoverPreviewActive: true } card && root.ActualWidth > 0)
			{
				var point = e.GetCurrentPoint(root).Position;
				hoverPreview.Scrub(card, point.X / root.ActualWidth);
			}
		}

		/// <summary>
		/// Single tap on a card: the same contract as the grid layout. Ctrl/Shift already applied their
		/// selection change on pointer press (see <see cref="Card_PointerPressed"/>) and only reset the
		/// rename debounce here; a tap on the name of the already-selected item starts the debounced
		/// rename (one double-click interval, so a double click never flashes an editor); a tap elsewhere
		/// while renaming commits; single-click-to-open honors the folder settings.
		/// </summary>
		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		[DynamicWindowsRuntimeCast(typeof(TextBlock))]
		[DynamicWindowsRuntimeCast(typeof(TextBox))]
		private async void Card_Tapped(object sender, TappedRoutedEventArgs e)
		{
			if (sender is not FrameworkElement root || root.DataContext is not PulseCardViewModel card)
				return;

			// Buttons on the card (stack badge) own their clicks
			var source = e.OriginalSource as DependencyObject;
			if (source?.FindAscendantOrSelf<ButtonBase>() is not null || source?.FindAscendantOrSelf<TextBox>() is not null)
				return;

			var item = card.Item;
			var ctrlPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
			var shiftPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
			if (ctrlPressed || shiftPressed)
			{
				ResetRenameDoubleClick();
				return;
			}

			if ((item.PrimaryItemAttribute is StorageItemTypes.File && UserSettingsService.FoldersSettingsService.OpenFilesWithSingleClick.ShouldOpenWithSingleClick(e.PointerDeviceType)) ||
				(item.PrimaryItemAttribute is StorageItemTypes.Folder && UserSettingsService.FoldersSettingsService.OpenFoldersWithSingleClick.ShouldOpenWithSingleClick(e.PointerDeviceType)))
			{
				ResetRenameDoubleClick();
				ItemManipulationModel.SetSelectedItem(item);
				await Commands.OpenItem.ExecuteAsync();
				return;
			}

			if (IsWithinRenameDoubleClickWindow && item == RenamingItem)
			{
				// A tap this soon after the tap that started renaming is the second click of a double click
				CancelRenameOnDoubleClick(item);
				ResetRenameDoubleClick();
				await Commands.OpenItem.ExecuteAsync();
				return;
			}

			if (IsRenamingItem && item != RenamingItem && renameTextBox is { } activeTextBox)
			{
				// Tapping another card ends the edit. LostFocus performs the (single) commit; only force the
				// focus move when the tap landed on something that did not take focus itself.
				if (ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), activeTextBox))
					root.FindAscendant<ItemContainer>()?.Focus(FocusState.Pointer);
				return;
			}

			// Tap on the name of the sole selected item: arm the rename debounce (base tracks the pre-renaming item)
			var tappedName = (e.OriginalSource as FrameworkElement)?.FindAscendantOrSelf<TextBlock>() is { Name: "ItemName" };
			if (tappedName && SelectedItems.Count == 1 && SelectedItems[0] == item)
			{
				CheckRenameDoubleClick(item);
				return;
			}

			ResetRenameDoubleClick();
		}

		/// <summary>Double tap opens the card's item (or steps into the folder) and never leaves a pending rename behind.</summary>
		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private async void Card_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
		{
			if (sender is not FrameworkElement root || root.DataContext is not PulseCardViewModel card)
				return;

			var source = e.OriginalSource as DependencyObject;
			if (source?.FindAscendantOrSelf<ButtonBase>() is not null || source?.FindAscendantOrSelf<TextBox>() is not null)
				return;

			var item = card.Item;
			CancelRenameOnDoubleClick(item);
			ResetRenameDoubleClick();

			if ((item.PrimaryItemAttribute == StorageItemTypes.File && UserSettingsService.FoldersSettingsService.OpenFilesWithSingleClick.ShouldOpenWithSingleClick(e.PointerDeviceType)) ||
				(item.PrimaryItemAttribute == StorageItemTypes.Folder && UserSettingsService.FoldersSettingsService.OpenFoldersWithSingleClick.ShouldOpenWithSingleClick(e.PointerDeviceType)))
				return; // already opened by the single tap

			e.Handled = true;
			if (!SelectedItems.Contains(item))
				ItemManipulationModel.SetSelectedItem(item);
			await Commands.OpenItem.ExecuteAsync();
		}

		/// <summary>
		/// Pressing on a card drives the whole selection model — the page owns it, the hidden list
		/// stores it, and the card chrome renders it. Plain press replaces the selection, Ctrl toggles
		/// the card, Shift extends from the anchor across the whole feed (all sections, in feed order).
		/// This matches the other layouts (Explorer semantics) for every card family.
		/// </summary>
		private void Card_PointerPressed(object sender, PointerRoutedEventArgs e)
		{
			if (sender is not FrameworkElement root || root.DataContext is not PulseCardViewModel card)
				return;

			var properties = e.GetCurrentPoint(root).Properties;
			if (!properties.IsLeftButtonPressed && !properties.IsRightButtonPressed)
				return;

			var source = e.OriginalSource as DependencyObject;
			if (source?.FindAscendantOrSelf<TextBox>() is not null)
				return;

			var ctrlPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
			var shiftPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

			if (ctrlPressed)
			{
				// Ctrl+click toggles the card without opening it; a rename must never arm from a toggle.
				ResetRenameDoubleClick();
				if (FileList.SelectedItems.Contains(card.Item))
					ItemManipulationModel.RemoveSelectedItem(card.Item);
				else
					ItemManipulationModel.AddSelectedItem(card.Item);
				selectionAnchor = card.Item;
				return;
			}

			if (shiftPressed)
			{
				// Shift+click selects everything from the anchor to this card (feed order).
				ResetRenameDoubleClick();
				SelectRangeFromAnchor(card.Item);
				return;
			}

			if (!SelectedItems.Contains(card.Item) || SelectedItems.Count > 1)
				ItemManipulationModel.SetSelectedItem(card.Item);
			selectionAnchor = card.Item;
		}

		/// <summary>
		/// Selects the feed range from <see cref="selectionAnchor"/> to <paramref name="target"/>.
		/// The anchor stays put so repeated Shift+clicks extend from the same origin.
		/// </summary>
		private void SelectRangeFromAnchor(ListedItem target)
		{
			var cards = feedBuilder.VisibleCards.ToList();
			var anchor = selectionAnchor;
			int anchorIndex = -1;
			int targetIndex = -1;
			for (int i = 0; i < cards.Count; i++)
			{
				if (ReferenceEquals(cards[i].Item, anchor))
					anchorIndex = i;
				if (ReferenceEquals(cards[i].Item, target))
					targetIndex = i;
			}

			// No anchor (or the anchor left the feed): a Shift+click selects just the target.
			if (anchorIndex < 0 || targetIndex < 0)
			{
				ItemManipulationModel.SetSelectedItem(target);
				selectionAnchor = target;
				return;
			}

			int start = Math.Min(anchorIndex, targetIndex);
			int end = Math.Max(anchorIndex, targetIndex);
			var range = new List<ListedItem>(end - start + 1);
			for (int i = start; i <= end; i++)
				range.Add(cards[i].Item);
			ItemManipulationModel.SetSelectedItems(range);
		}

		/// <summary>
		/// Pressing empty space (no card, no button, no edit field) clears the selection like every
		/// other layout and arms the rubber-band selection. The base handler keeps the middle-click
		/// folder behavior. A plain left press on a card is handled by <see cref="Card_PointerPressed"/>.
		/// </summary>
		private void PageRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
		{
			base.LayoutPage_PointerPressed(sender, e);

			// A missed release (the pointer ended over a child that handled it) can leave the
			// previous gesture armed; finish it before arming the new one instead of ignoring
			// the press and resurrecting the old box on the next move.
			if (isMarqueeArmed || isMarqueeActive)
				EndMarquee(commit: true);

			var source = e.OriginalSource as DependencyObject;
			if (source?.FindAscendantOrSelf<ItemContainer>() is not null)
				return;
			if (source?.FindAscendantOrSelf<ButtonBase>() is not null || source?.FindAscendantOrSelf<TextBox>() is not null)
				return;
			if (source?.FindAscendantOrSelf<ScrollBar>() is not null || source?.FindAscendantOrSelf<PulseTimeRail>() is not null)
				return;
			if (source?.FindAscendantOrSelf<StackPanel>() == FilterChips)
				return;

			var point = e.GetCurrentPoint(this);
			if (!point.Properties.IsLeftButtonPressed)
				return;
			if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
				return; // touch keeps its native pan/scroll gestures; the marquee is mouse/pen only (same as the other layouts)

			var ctrlPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);

			// Explorer semantics: a plain press on empty space clears the selection immediately;
			// Ctrl preserves it so a Ctrl+drag marquee adds to the selection.
			if (SelectedItems.Count > 0 && !ctrlPressed)
				ItemManipulationModel.ClearSelection();

			marqueeCtrl = ctrlPressed;
			marqueeStartPoint = e.GetCurrentPoint(MarqueeCanvas).Position;
			marqueeLastPoint = marqueeStartPoint;
			isMarqueeCaptured = false;
			marqueeExistingSelection = ctrlPressed
				? new HashSet<ListedItem>(FileList.SelectedItems.OfType<ListedItem>(), ReferenceEqualityComparer.Instance)
				: null;
			marqueeSelection = null;
			isMarqueeArmed = true;
		}

		private void PageRoot_PointerMoved(object sender, PointerRoutedEventArgs e)
		{
			if (!isMarqueeArmed && !isMarqueeActive)
				return;

			// The button state is authoritative: moves keep arriving while hovering, so a lost
			// release must end the gesture here instead of letting a plain hover draw the box.
			if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
			{
				EndMarquee(commit: true);
				return;
			}

			marqueeLastPoint = e.GetCurrentPoint(MarqueeCanvas).Position;

			if (!isMarqueeActive)
			{
				// True drag only: a click (or jitter below the threshold) never draws the box.
				if (Math.Abs(marqueeLastPoint.X - marqueeStartPoint.X) < MarqueeActivationThreshold &&
					Math.Abs(marqueeLastPoint.Y - marqueeStartPoint.Y) < MarqueeActivationThreshold)
					return;

				if (!isMarqueeCaptured)
				{
					isMarqueeCaptured = true;
					CapturePointer(e.Pointer);
				}

				// The pointer really moved: engage the rubber band immediately and track it.
				isMarqueeActive = true;
				MarqueeRect.Visibility = Visibility.Visible;
				marqueeSelection = new HashSet<ListedItem>(ReferenceEqualityComparer.Instance);
			}

			UpdateMarqueeRect(marqueeLastPoint);
			UpdateMarqueeSelection();
			UpdateMarqueeEdgeScroll(e.GetCurrentPoint(FeedScroller).Position);
		}

		private void PageRoot_PointerReleased(object sender, PointerRoutedEventArgs e)
		{
			if (!isMarqueeArmed && !isMarqueeActive)
				return;

			// Releasing another button (e.g. right) must not end a left-button marquee.
			if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
				return;

			EndMarquee(commit: true);
		}

		private void PageRoot_PointerCanceled(object sender, PointerRoutedEventArgs e)
		{
			if (!isMarqueeArmed && !isMarqueeActive)
				return;

			EndMarquee(commit: false);
		}

		private void PageRoot_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
		{
			// A lost capture (window deactivated mid-gesture) behaves like a release: commit what is under the box.
			if (!isMarqueeArmed && !isMarqueeActive)
				return;

			EndMarquee(commit: true);
		}

		/// <summary>Finishes the rubber-band gesture: commits the tentative selection to the hidden list or discards it.</summary>
		private void EndMarquee(bool commit)
		{
			var wasActive = isMarqueeActive;
			isMarqueeArmed = false;
			isMarqueeActive = false;
			StopMarqueeEdgeScroll();
			MarqueeRect.Visibility = Visibility.Collapsed;

			// The pointer may have been captured as soon as the drag threshold was
			// crossed (before activation): always release it, not only after a box.
			if (isMarqueeCaptured)
			{
				isMarqueeCaptured = false;
				ReleasePointerCaptures();
			}

			if (wasActive)
			{
				if (commit && marqueeSelection is { } selection)
				{
					// Only items the hidden list actually owns can be selected; adding a card
					// that is not in the list throws and used to abort the rest of the box.
					var available = new HashSet<ListedItem>(FileList.Items.OfType<ListedItem>(), ReferenceEqualityComparer.Instance);
					if (!marqueeCtrl)
						FileList.SelectedItems.Clear();

					foreach (var item in selection)
					{
						if (available.Contains(item) && !FileList.SelectedItems.Contains(item))
							FileList.SelectedItems.Add(item);
					}

					selectionAnchor = selection.FirstOrDefault();
					SyncSelectionToFeed();
				}
				else
				{
					// Cancelled: restore the visuals from the hidden list (the live tentative state is stale).
					SyncSelectionToFeed();
				}
			}

			marqueeStartPoint = default;
			marqueeLastPoint = default;
			marqueeRect = default;
			marqueeSelection = null;
			marqueeExistingSelection = null;
		}

		private void UpdateMarqueeRect(Windows.Foundation.Point current)
		{
			double x = Math.Min(marqueeStartPoint.X, current.X);
			double y = Math.Min(marqueeStartPoint.Y, current.Y);
			double width = Math.Abs(current.X - marqueeStartPoint.X);
			double height = Math.Abs(current.Y - marqueeStartPoint.Y);

			marqueeRect = new Windows.Foundation.Rect(x, y, width, height);
			Canvas.SetLeft(MarqueeRect, x);
			Canvas.SetTop(MarqueeRect, y);
			MarqueeRect.Width = width;
			MarqueeRect.Height = height;
		}

		/// <summary>
		/// Recomputes which realized cards intersect the marquee and updates their selection visuals
		/// live. Virtualized-away cards cannot be hit-tested; the commit pass mirrors the final set
		/// onto the hidden list, which re-syncs every section anyway.
		/// </summary>
		private void UpdateMarqueeSelection()
		{
			if (marqueeSelection is not { } selection)
				return;

			// Scan the realized containers live. ItemsView recycles containers without always
			// raising Unloaded/Loaded, so the Loaded-time registry went stale and silently
			// skipped cards; the container's current DataContext is always authoritative.
			var visible = new HashSet<ListedItem>(ReferenceEqualityComparer.Instance);
			var intersected = new HashSet<ListedItem>(ReferenceEqualityComparer.Instance);
			foreach (var view in sectionViews)
			{
				foreach (var container in view.ItemsView.FindDescendants().OfType<ItemContainer>())
				{
					if (container.Child is not FrameworkElement root || root.DataContext is not PulseCardViewModel card)
						continue;
					if (root.ActualWidth <= 0 || root.ActualHeight <= 0)
						continue;

					var bounds = root.TransformToVisual(MarqueeCanvas)
						.TransformBounds(new Windows.Foundation.Rect(0, 0, root.ActualWidth, root.ActualHeight));

					visible.Add(card.Item);
					if (bounds.IntersectsWith(marqueeRect))
						intersected.Add(card.Item);
				}
			}

			// Drop only cards still on screen that left the box. Cards the box swept while
			// edge-scrolling moved them out of the realized range stay selected.
			foreach (var item in selection.ToList())
			{
				if (visible.Contains(item) && !intersected.Contains(item))
					selection.Remove(item);
			}
			foreach (var item in intersected)
				selection.Add(item);

			// Live visuals: the tentative state is page-local until the commit writes it to the list.
			var visualSelection = marqueeCtrl && marqueeExistingSelection is { } existing
				? new HashSet<ListedItem>(existing.Concat(selection), ReferenceEqualityComparer.Instance)
				: selection;
			foreach (var view in sectionViews)
				view.SyncSelection(visualSelection);
		}

		// Edge auto-scroll: while the marquee is near the top/bottom of the feed viewport, a timer
		// scrolls the content and the selection re-tracks on every tick (cards move under the box).

		private void UpdateMarqueeEdgeScroll(Windows.Foundation.Point viewportPoint)
		{
			int direction = 0;
			if (viewportPoint.Y < MarqueeEdgeScrollZone)
				direction = -1;
			else if (viewportPoint.Y > FeedScroller.ViewportHeight - MarqueeEdgeScrollZone)
				direction = 1;

			if (direction == 0)
			{
				StopMarqueeEdgeScroll();
				return;
			}

			marqueeScrollDirection = direction;
			if (marqueeScrollTimer is null)
			{
				marqueeScrollTimer = DispatcherQueue.CreateTimer();
				marqueeScrollTimer.Interval = TimeSpan.FromMilliseconds(16);
				marqueeScrollTimer.IsRepeating = true;
				marqueeScrollTimer.Tick += (_, _) =>
				{
					if (!isMarqueeActive)
						return;

					FeedScroller.ChangeView(null, FeedScroller.VerticalOffset + MarqueeEdgeScrollStep * marqueeScrollDirection, null, true);
					UpdateMarqueeSelection();
				};
			}

			if (!marqueeScrollTimer.IsRunning)
				marqueeScrollTimer.Start();
		}

		private void StopMarqueeEdgeScroll()
		{
			marqueeScrollDirection = 0;
			if (marqueeScrollTimer is { IsRunning: true })
				marqueeScrollTimer.Stop();
		}

		/// <summary>
		/// Keyboard focus belongs to the feed, not to the hidden list. The focus target is the page-level
		/// scroller, never a virtualized card container: FocusFileList is raised from tab/pane switches
		/// while the previous tab's tree is being torn down, and a focused element inside a recycled
		/// ItemsView container makes XAML's focus hand-off dereference a detached scroller (AV in
		/// FocusProperties::IsScrollable). Cards still receive focus through pointer and keyboard navigation.
		/// </summary>
		protected override void ItemManipulationModel_FocusFileListInvoked(object? sender, EventArgs e)
		{
			try
			{
				if (App.AppModel.IsMainWindowClosed || !IsLoaded || FeedScroller.XamlRoot is null)
					return;

				if (FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused && focused.FindAscendantOrSelf<ScrollViewer>() == FeedScroller)
					return;

				FeedScroller.Focus(FocusState.Programmatic);
			}
			catch
			{
				// The window may be closing while a pane switch is still propagating focus
			}
		}

		private void StackToggle_Click(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement { DataContext: PulseCardViewModel card })
			{
				ResetRenameDoubleClick();
				feedBuilder.ToggleStack(card);
			}
		}

		// Audio transport (buttons on the audio card; Card_Tapped ignores clicks that land on buttons)

		private async void AudioPlayPause_Click(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement { DataContext: PulseCardViewModel card })
			{
				ResetRenameDoubleClick();
				await RunMediaActionAsync(() => audioPlayer.TogglePlayPauseAsync(card));
			}
		}

		private void AudioStop_Click(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement { DataContext: PulseCardViewModel card })
			{
				ResetRenameDoubleClick();
				audioPlayer.Stop(card);
			}
		}

		// Audio trim (audio + video cards; the dialog extracts the audio track of videos)

		private async void AudioTrim_Click(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement { DataContext: PulseCardViewModel card })
			{
				ResetRenameDoubleClick();
				await OpenTrimDialogAsync(card);
			}
		}

		private async void VideoTrim_Click(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement { DataContext: PulseCardViewModel card })
			{
				ResetRenameDoubleClick();
				hoverPreview.Leave(card);
				await OpenTrimDialogAsync(card);
			}
		}

		private async Task OpenTrimDialogAsync(PulseCardViewModel card)
		{
			await RunMediaActionAsync(async () =>
			{
				audioPlayer.Stop();
				hoverPreview.Reset();
				var dialog = new PulseTrimDialog(card) { XamlRoot = XamlRoot };
				await dialog.ShowAsync();
			});
		}

		// Media tools: convert (audio/video/image), image edit, video tools

		private async void AudioConvert_Click(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement { DataContext: PulseCardViewModel card })
			{
				ResetRenameDoubleClick();
				audioPlayer.Stop(card);
				await OpenConvertDialogAsync(card);
			}
		}

		private async void VideoConvert_Click(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement { DataContext: PulseCardViewModel card })
			{
				ResetRenameDoubleClick();
				hoverPreview.Leave(card);
				await OpenConvertDialogAsync(card);
			}
		}

		private async void ImageConvert_Click(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement { DataContext: PulseCardViewModel card })
			{
				ResetRenameDoubleClick();
				await OpenConvertDialogAsync(card);
			}
		}

		private async void ImageEdit_Click(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement { DataContext: PulseCardViewModel card })
			{
				ResetRenameDoubleClick();
				await RunMediaActionAsync(async () =>
				{
					var dialog = new PulseImageToolDialog(card) { XamlRoot = XamlRoot };
					await dialog.ShowAsync();
				});
			}
		}

		// NAI Media Lab (capability-aware FFmpeg operations)

		private async void MediaLab_Click(object sender, RoutedEventArgs e)
		{
			if (sender is not FrameworkElement { DataContext: PulseCardViewModel card })
				return;

			ResetRenameDoubleClick();
			audioPlayer.Stop(card);
			hoverPreview.Reset();

			await RunMediaActionAsync(async () =>
			{
				var tools = ffmpeg ??= await PulseFfmpeg.RequireAsync();
				var dialog = new PulseMediaLabDialog(card, tools) { XamlRoot = XamlRoot };
				await dialog.ShowAsync();
			});
		}

		// Quick FFmpeg actions (the custom "more actions" menu on video cards)

		private async void VideoExtractAudio_Click(object sender, RoutedEventArgs e)
			=> await RunVideoQuickActionAsync(sender, Strings.PulseQuickExtractAudio.GetLocalizedResource(), "audio", ".mp3",
				(tools, path, output, token, progress) => tools.ExtractAudioAsync(path, output, token, progress));

		private async void VideoMute_Click(object sender, RoutedEventArgs e)
			=> await RunVideoQuickActionAsync(sender, Strings.PulseQuickMute.GetLocalizedResource(), "muted", ".mp4",
				(tools, path, output, token, _) => tools.MuteVideoAsync(path, output, token));

		private async void VideoRotateRight_Click(object sender, RoutedEventArgs e)
			=> await RunVideoQuickActionAsync(sender, Strings.PulseQuickRotateRight.GetLocalizedResource(), "rotated", ".mp4",
				(tools, path, output, token, _) => tools.RotateVideoAsync(path, output, 1, token));

		private async void VideoFlipHorizontal_Click(object sender, RoutedEventArgs e)
			=> await RunVideoQuickActionAsync(sender, Strings.PulseQuickFlip.GetLocalizedResource(), "flipped", ".mp4",
				(tools, path, output, token, _) => tools.FlipVideoAsync(path, output, true, token));

		private async Task RunVideoQuickActionAsync(
			object sender, string title, string suffix, string extension,
			Func<PulseFfmpeg, string, string, CancellationToken, IProgress<double>, Task<bool>> action)
		{
			if (sender is not FrameworkElement { DataContext: PulseCardViewModel card })
				return;

			ResetRenameDoubleClick();
			audioPlayer.Stop(card);
			hoverPreview.Reset();

			var sourcePath = card.Item.GetRequiredPath();
			var outputPath = ResolveQuickOutputPath(sourcePath, suffix, extension);

			await RunMediaActionAsync(async () =>
			{
				var tools = ffmpeg ??= await PulseFfmpeg.RequireAsync();
				var dialog = new PulseQuickActionDialog(title, Path.GetFileName(sourcePath), async (progress, token) =>
				{
					var succeeded = await action(tools, sourcePath, outputPath, token, progress);
					return succeeded ? outputPath : null;
				})
				{ XamlRoot = XamlRoot };

				await dialog.ShowAsync();
			});
		}

		private static string ResolveQuickOutputPath(string sourcePath, string suffix, string extension)
		{
			var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
			var stem = Path.GetFileNameWithoutExtension(sourcePath);
			if (string.IsNullOrWhiteSpace(stem))
				stem = "media";

			var candidate = Path.Combine(directory, $"{stem} ({suffix}).{extension}");
			var counter = 2;
			while (File.Exists(candidate))
				candidate = Path.Combine(directory, $"{stem} ({suffix}) ({counter++}).{extension}");
			return candidate;
		}

		private async void VideoTools_Click(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement { DataContext: PulseCardViewModel card })
			{
				ResetRenameDoubleClick();
				await RunMediaActionAsync(async () =>
				{
					audioPlayer.Stop();
					hoverPreview.Reset();
					var dialog = new PulseVideoToolDialog(card) { XamlRoot = XamlRoot };
					await dialog.ShowAsync();
				});
			}
		}

		// Audio split (audio + video cards; Demucs separates the audio track into stems)

		private async void AudioSplit_Click(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement { DataContext: PulseCardViewModel card })
			{
				ResetRenameDoubleClick();
				audioPlayer.Stop(card);
				await OpenSplitDialogAsync(card);
			}
		}

		private async void VideoSplit_Click(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement { DataContext: PulseCardViewModel card })
			{
				ResetRenameDoubleClick();
				hoverPreview.Leave(card);
				await OpenSplitDialogAsync(card);
			}
		}

		private async Task OpenSplitDialogAsync(PulseCardViewModel card)
		{
			await RunMediaActionAsync(async () =>
			{
				audioPlayer.Stop();
				hoverPreview.Reset();
				var dialog = new PulseSplitDialog(card) { XamlRoot = XamlRoot };
				await dialog.ShowAsync();
			});
		}

		private async Task OpenConvertDialogAsync(PulseCardViewModel card)
		{
			await RunMediaActionAsync(async () =>
			{
				audioPlayer.Stop();
				hoverPreview.Reset();
				var dialog = new PulseConvertDialog(card) { XamlRoot = XamlRoot };
				await dialog.ShowAsync();
			});
		}

		private async Task RunMediaActionAsync(Func<Task> action)
		{
			try { await action(); }
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				App.Logger.LogError(ex, "Pulse media action failed");
				if (IsLoaded && XamlRoot is not null)
					await DialogDisplayHelper.ShowDialogAsync(Strings.PulseMediaActionFailed.GetLocalizedResource(), ex.Message, Strings.OK.GetLocalizedResource());
			}
		}

		private async void AudioPlayer_PlaybackFailed(object? sender, string error)
		{
			if (IsLoaded && XamlRoot is not null)
				await DialogDisplayHelper.ShowDialogAsync(Strings.PulseMediaActionFailed.GetLocalizedResource(), error, Strings.OK.GetLocalizedResource());
		}

		/// <summary>A click in the waveform of a loaded track seeks to that position instead of selecting the card.</summary>
		private void AudioStrip_Tapped(object sender, TappedRoutedEventArgs e)
		{
			if (sender is not FrameworkElement strip || strip.DataContext is not PulseCardViewModel { IsPlaybackActive: true } card)
				return;

			// The transport buttons sit in the strip and own their clicks
			if ((e.OriginalSource as DependencyObject)?.FindAscendantOrSelf<ButtonBase>() is not null || strip.ActualWidth <= 0)
				return;

			audioPlayer.Seek(card, e.GetPosition(strip).X / strip.ActualWidth);
			ResetRenameDoubleClick();
			e.Handled = true;
		}

		private void AudioStrip_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
		{
			// Two quick seeks must not also open the file
			if (sender is FrameworkElement { DataContext: PulseCardViewModel { IsPlaybackActive: true } }
				&& (e.OriginalSource as DependencyObject)?.FindAscendantOrSelf<ButtonBase>() is null)
				e.Handled = true;
		}

		private void Card_RightTapped(object sender, RightTappedRoutedEventArgs e)
		{
			if (sender is FrameworkElement { DataContext: PulseCardViewModel card } && !SelectedItems.Contains(card.Item))
				ItemManipulationModel.SetSelectedItem(card.Item);
		}

		private void Feed_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
			=> FileList_PreviewKeyDown(sender, e);

		protected override async void FileList_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (ParentShellPageInstance is null || IsRenamingItem)
				return;

			var ctrlPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
			var shiftPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

			if (ctrlPressed && e.Key is VirtualKey.A)
			{
				e.Handled = true;
				ItemManipulationModel.SelectAllItems();
			}
			else if (e.Key == VirtualKey.Enter && !e.KeyStatus.IsMenuKeyDown)
			{
				e.Handled = true;

				if (ctrlPressed && !shiftPressed)
				{
					foreach (var folder in SelectedItems.Where(file => file.PrimaryItemAttribute == StorageItemTypes.Folder))
						await NavigationHelpers.OpenPathInNewTab(folder.ItemPath);
				}
				else if (ctrlPressed && shiftPressed)
				{
					if (SelectedItems.FirstOrDefault(item => item.PrimaryItemAttribute == StorageItemTypes.Folder) is { } folder)
						NavigationHelpers.OpenInSecondaryPane(ParentShellPageInstance, folder);
				}
				else
				{
					await Commands.OpenItem.ExecuteAsync();
				}
			}
			else if (e.Key == VirtualKey.Enter && e.KeyStatus.IsMenuKeyDown)
			{
				FilePropertiesHelpers.OpenPropertiesWindow(ParentShellPageInstance);
				e.Handled = true;
			}
			else if (e.KeyStatus.IsMenuKeyDown && (e.Key == VirtualKey.Left || e.Key == VirtualKey.Right || e.Key == VirtualKey.Up))
			{
				// Unfocus the feed so the navigation shortcuts are handled
				Focus(FocusState.Pointer);
			}
		}

		// Rename: inline text box in the card footer

		[DynamicWindowsRuntimeCast(typeof(TextBlock))]
		[DynamicWindowsRuntimeCast(typeof(TextBox))]
		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		public override void StartRenameItem()
		{
			if (IsRenamingItem)
				return;

			RenamingItem = SelectedItem;
			if (RenamingItem is null)
				return;

			var (view, index) = Locate(RenamingItem);
			if (view is null || !feedBuilder.TryGetCard(RenamingItem, out var card))
			{
				RenamingItem = null;
				return;
			}

			// The card root registered by Card_Loaded is a managed-typed element; fall back to the visual tree only
			// when the card is virtualized away (F2 after keyboard navigation or a time-rail jump)
			if (!cardRoots.TryGetValue(card, out var root))
			{
				view.ItemsView.StartBringItemIntoView(index, new BringIntoViewOptions { AnimationDesired = false });
				view.ItemsView.UpdateLayout();
				if (!cardRoots.TryGetValue(card, out root))
					root = view.FindContainer(card)?.Child as FrameworkElement;
			}

			var textBlock = root?.FindDescendant("ItemName") as TextBlock;
			var textBox = root?.FindDescendant("PulseRenameTextBox") as TextBox;
			if (textBlock is null || textBox is null)
			{
				RenamingItem = null;
				return;
			}

			int extensionLength = RenamingItem.FileExtension?.Length ?? 0;
			string editText = ShouldShowExtensionInRename(RenamingItem) ? RenamingItem.ItemNameRaw! : textBlock.Text;

			textBox.Text = editText;
			OldItemName = editText;
			textBlock.Visibility = Visibility.Collapsed;
			textBox.Visibility = Visibility.Visible;

			textBox.Focus(FocusState.Pointer);
			textBox.LostFocus += RenameTextBox_LostFocus;
			textBox.KeyDown += RenameTextBox_KeyDown;

			int selectedTextLength = editText.Length;
			if (!RenamingItem.IsShortcut && (ShouldShowExtensionInRename(RenamingItem) || UserSettingsService.FoldersSettingsService.ShowFileExtensions))
				selectedTextLength -= extensionLength;

			textBox.Select(0, selectedTextLength);
			IsRenamingItem = true;
			renameTextBox = textBox;
		}

		[DynamicWindowsRuntimeCast(typeof(TextBlock))]
		protected override void EndRename(TextBox textBox)
		{
			if (textBox.FindAscendant<Grid>()?.FindDescendant("ItemName") is TextBlock textBlock)
				textBlock.Visibility = Visibility.Visible;

			textBox.Visibility = Visibility.Collapsed;
			textBox.LostFocus -= RenameTextBox_LostFocus;
			textBox.KeyDown -= RenameTextBox_KeyDown;

			FileNameTeachingTip.IsOpen = false;
			IsRenamingItem = false;
			renameTextBox = null;
		}

		private void RenameTextBox_BeforeTextChanging(TextBox textBox, TextBoxBeforeTextChangingEventArgs args)
		{
			if (!IsRenamingItem)
				return;

			_ = ValidateItemNameInputTextAsync(textBox, args, showError =>
			{
				FileNameTeachingTip.Visibility = showError ? Visibility.Visible : Visibility.Collapsed;
				FileNameTeachingTip.IsOpen = showError;
			});
		}

		// Time rail

		private void TimeRail_DayRequested(object? sender, DateTime day)
		{
			for (int i = 0; i < Feed.Count; i++)
			{
				if (Feed[i].Day != day)
					continue;

				JumpToSection(i);
				return;
			}
		}

		/// <summary>
		/// Scrolls the feed to a section with an eased animation. The section is realized first so a
		/// virtualized-away day can still be targeted; its offset inside the scroller content is
		/// measured and animated with <see cref="ScrollViewer.ChangeView(double?, double?, float?, bool)"/>,
		/// falling back to BringIntoView when the transform is not ready yet.
		/// </summary>
		private void JumpToSection(int index)
		{
			if (index < 0 || index >= Feed.Count)
				return;

			var element = SectionsRepeater.GetOrCreateElement(index);
			element.UpdateLayout();

			if (element is FrameworkElement framework && FeedScroller.Content is UIElement content)
			{
				try
				{
					var offset = framework.TransformToVisual(content).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
					if (!double.IsNaN(offset) && !double.IsInfinity(offset) && offset >= 0)
					{
						// disableAnimation: false keeps the ScrollViewer's eased animation.
						FeedScroller.ChangeView(null, offset, null, disableAnimation: false);
						return;
					}
				}
				catch
				{
					// Element not connected yet; fall back to BringIntoView.
				}
			}

			element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0 });
		}
	}
}
