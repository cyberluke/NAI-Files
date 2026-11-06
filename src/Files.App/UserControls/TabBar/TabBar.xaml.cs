// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Win32;
using WinRT;

namespace Files.App.UserControls.TabBar
{
	public sealed partial class TabBar : BaseTabBar, INotifyPropertyChanged
	{
		// Dependency injections

		private readonly ICommandManager Commands = Ioc.Default.GetRequiredService<ICommandManager>();
		private readonly IAppearanceSettingsService AppearanceSettingsService = Ioc.Default.GetRequiredService<IAppearanceSettingsService>();
		private readonly IGeneralSettingsService GeneralSettingsService = Ioc.Default.GetRequiredService<IGeneralSettingsService>();
		private readonly IWindowContext WindowContext = Ioc.Default.GetRequiredService<IWindowContext>();

		// Fields

		private readonly DispatcherTimer tabHoverTimer = new();

		private TabViewItem? hoveredTabViewItem;

		// Tab that currently shows the drop indicator
		private TabViewItem? dropTargetTabViewItem;

		// Used to discard drag over results that resolved after the pointer already left the tab
		private int dragOverRevision;

		private bool _lockDropOperation = false;

		// Starting position when dragging a tab
		private System.Drawing.Point dragStartPoint;

		// Starting time when dragging a tab
		private DateTimeOffset dragStartTime;

		// Indicates if drag operation should be canceled.
		// This value gets reset at the start of the drag operation
		private bool isCancelingDragOperation;

		//private string[] _droppableArchiveTypes = { "zip", "rar", "7z", "tar" };

		// Properties

		public bool ShowTabActionsButton
			=> AppearanceSettingsService.ShowTabActions;

		public bool AllowTabsDrag
			=> WindowContext.CanDragAndDrop;

		public Rectangle DragArea
			=> DragAreaRectangle;

		// Events

		public static event EventHandler<TabBarItem?>? SelectedTabItemChanged;
		public static event EventHandler<TabBarItem?>? TabDragStarted;
		public static event EventHandler<TabBarItem?>? TabDragCompleted;

		// Constructor

		public TabBar()
		{
			InitializeComponent();

			tabHoverTimer.Interval = TimeSpan.FromMilliseconds(Constants.DragAndDrop.HoverToOpenTimespan);
			tabHoverTimer.Tick += TabHoverSelected;

			AppearanceSettingsService.PropertyChanged += (s, e) =>
			{
				switch (e.PropertyName)
				{
					case nameof(AppearanceSettingsService.ShowTabActions):
						NotifyPropertyChanged(nameof(ShowTabActionsButton));
						break;
				}
			};
		}

		private void TabView_TabItemsChanged(TabView sender, Windows.Foundation.Collections.IVectorChangedEventArgs args)
		{
			if (args.CollectionChange == Windows.Foundation.Collections.CollectionChange.ItemRemoved)
			{
				App.AppModel.TabStripSelectedIndex = HorizontalTabView.SelectedItem is TabBarItem selectedItem
					? Items.IndexOf(selectedItem)
					: -1;
			}

			if (App.AppModel.TabStripSelectedIndex >= 0 && App.AppModel.TabStripSelectedIndex < Items.Count)
			{
				CurrentSelectedAppInstance = GetCurrentSelectedTabInstance();

				if (CurrentSelectedAppInstance is not null)
				{
					OnCurrentInstanceChanged(new CurrentInstanceChangedEventArgs()
					{
						CurrentInstance = CurrentSelectedAppInstance,
						PageInstances = GetAllTabInstances()
					});
				}
			}

			HorizontalTabView.SelectedIndex = App.AppModel.TabStripSelectedIndex;
		}

		private async void TabViewItem_Drop(object sender, DragEventArgs e)
		{
			if (sender is not TabViewItem { DataContext: TabBarItem { TabItemContent: { } tabContent } })
				return;

			dragOverRevision++;
			SetDropTargetTabViewItem(null);

			await tabContent.TabItemDrop(sender, e);
			HorizontalTabView.CanReorderTabs = true;
			tabHoverTimer.Stop();
		}

		private async void TabViewItem_DragEnter(object sender, DragEventArgs e)
		{
			if (sender is not TabViewItem { DataContext: TabBarItem { TabItemContent: { } tabContent } } tabViewItem)
				return;

			var revision = ++dragOverRevision;

			await tabContent.TabItemDragOver(sender, e);

			if (revision != dragOverRevision)
				return;

			if (e.AcceptedOperation != DataPackageOperation.None)
			{
				HorizontalTabView.CanReorderTabs = false;
				tabHoverTimer.Start();
				hoveredTabViewItem = tabViewItem;
				SetDropTargetTabViewItem(tabViewItem);
			}
		}

		private void TabViewItem_DragLeave(object sender, DragEventArgs e)
		{
			dragOverRevision++;
			tabHoverTimer.Stop();
			hoveredTabViewItem = null;
			SetDropTargetTabViewItem(null);
		}

		private void SetDropTargetTabViewItem(TabViewItem? tabViewItem)
		{
			if (dropTargetTabViewItem == tabViewItem)
				return;

			if (dropTargetTabViewItem is not null)
				VisualStateManager.GoToState(dropTargetTabViewItem, "NoStorageItemDragOver", true);

			dropTargetTabViewItem = tabViewItem;

			if (tabViewItem is not null)
				VisualStateManager.GoToState(tabViewItem, "StorageItemDragOver", true);
		}

		// Select tab that is hovered over for a certain duration
		private void TabHoverSelected(object? sender, object e)
		{
			tabHoverTimer.Stop();
			if (hoveredTabViewItem?.DataContext is TabBarItem tabItem)
				App.AppModel.TabStripSelectedIndex = Items.IndexOf(tabItem);
		}

		private void TabView_TabDragStarting(TabView sender, TabViewTabDragStartingEventArgs args)
		{
			// Reset value
			isCancelingDragOperation = false;

			if (args.Item is not TabBarItem { NavigationParameter: { } tabViewItemArgs } tabItem)
				return;

			args.Data.Properties.Add(TabPathIdentifier, tabViewItemArgs.Serialize());
			args.Data.RequestedOperation = DataPackageOperation.Move;

			// Get cursor position & time to track how far the tab was dragged.
			PInvoke.GetCursorPos(out dragStartPoint);
			dragStartTime = DateTimeOffset.UtcNow;

			// Focus the UI Element, without this the focus sometimes changes
			// and the PreviewKeyDown event won't trigger.
			Focus(FocusState.Programmatic);
			PreviewKeyDown += TabDragging_PreviewKeyDown;

			TabDragStarted?.Invoke(this, tabItem);
		}

		private void TabDragging_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
		{
			// Pressing escape will automatically complete the drag event but we need to set the
			// isCancelingDragOperation field in order to detect if escape was pressed.
			if (e.Key is Windows.System.VirtualKey.Escape)
				isCancelingDragOperation = true;
		}

		private void TabView_TabStripDragOver(object sender, DragEventArgs e)
		{
			if (e.DataView.Properties.ContainsKey(TabPathIdentifier))
			{
				HorizontalTabView.CanReorderTabs = WindowContext.CanDragAndDrop;

				e.AcceptedOperation = DataPackageOperation.Move;
				e.DragUIOverride.Caption = Strings.TabStripDragAndDropUIOverrideCaption.GetLocalizedResource();
				e.DragUIOverride.IsCaptionVisible = true;
				e.DragUIOverride.IsGlyphVisible = false;
			}
			else
			{
				HorizontalTabView.CanReorderTabs = false;
			}
		}

		private void TabView_DragLeave(object sender, DragEventArgs e)
		{
			HorizontalTabView.CanReorderTabs = WindowContext.CanDragAndDrop;
			dragOverRevision++;
			SetDropTargetTabViewItem(null);
		}

		[DynamicWindowsRuntimeCast(typeof(TabView))]
		[DynamicWindowsRuntimeCast(typeof(TabViewItem))]
		private async void TabView_TabStripDrop(object sender, DragEventArgs e)
		{
			HorizontalTabView.CanReorderTabs = WindowContext.CanDragAndDrop;

			if (!(sender is TabView tabStrip))
				return;

			if (!e.DataView.Properties.TryGetValue(TabPathIdentifier, out object tabViewItemPathObj) ||
				tabViewItemPathObj is not string tabViewItemString)
				return;

			var index = -1;

			for (int i = 0; i < tabStrip.TabItems.Count; i++)
			{
				var item = (tabStrip.ContainerFromIndex(i) as TabViewItem)!;

				if (e.GetPosition(item).X - item.ActualWidth < 0)
				{
					index = i;
					break;
				}
			}

			TabBarItemParameter tabViewItemArgs;
			try
			{
				tabViewItemArgs = TabBarItemParameter.Deserialize(tabViewItemString);
			}
			catch (JsonException)
			{
				return;
			}

			ApplicationData.Current.LocalSettings.Values[TabDropHandledIdentifier] = true;
			await NavigationHelpers.AddNewTabByParamAsync(tabViewItemArgs.InitialPageType, tabViewItemArgs.NavigationParameter, index);
		}

		private void TabView_TabDragCompleted(TabView sender, TabViewTabDragCompletedEventArgs args)
		{
			// Unsubscribe from the key down event, it's only needed when a tab is actively being dragged
			PreviewKeyDown -= TabDragging_PreviewKeyDown;

			TabDragCompleted?.Invoke(this, args.Item as TabBarItem);

			if (ApplicationData.Current.LocalSettings.Values.ContainsKey(TabDropHandledIdentifier) &&
				(bool)ApplicationData.Current.LocalSettings.Values[TabDropHandledIdentifier])
				CloseTab(args.Item as TabBarItem);
			else
				HorizontalTabView.SelectedItem = args.Tab;

			ApplicationData.Current.LocalSettings.Values.Remove(TabDropHandledIdentifier);
		}

		private async void TabView_TabDroppedOutside(TabView sender, TabViewTabDroppedOutsideEventArgs args)
		{
			// Unsubscribe from the key down event, it's only needed when a tab is actively being dragged
			PreviewKeyDown -= TabDragging_PreviewKeyDown;

			if (isCancelingDragOperation)
				return;

			PInvoke.GetCursorPos(out var droppedPoint);
			var droppedTime = DateTimeOffset.UtcNow;
			var dragTime = droppedTime - dragStartTime;
			var dragDistance = Math.Sqrt(Math.Pow(dragStartPoint.X - droppedPoint.X, 2) + Math.Pow(dragStartPoint.Y - droppedPoint.Y, 2));

			if (sender.TabItems.Count == 1 ||
				(dragTime.TotalSeconds < 1 &&
				dragDistance < 100))
				return;

			var indexOfTabViewItem = sender.TabItems.IndexOf(args.Item);
			if (args.Item is not TabBarItem { NavigationParameter: { } tabViewItemArgs } tabItem)
				return;

			var selectedTabViewItemIndex = sender.SelectedIndex;

			Items.Remove(tabItem);
			if (!await NavigationHelpers.OpenTabInNewWindowAsync(tabViewItemArgs.Serialize(), droppedPoint.X, droppedPoint.Y))
			{
				// Other tabs can close while the new window opens, so the remembered positions may no longer exist
				Items.Insert(Math.Clamp(indexOfTabViewItem, 0, Items.Count), tabItem);
				sender.SelectedIndex = Math.Clamp(selectedTabViewItemIndex, 0, Items.Count - 1);
			}
			else
				// Dispose tab arguments
				tabItem.Unload();
		}

		private void TabView_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
		{
			var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
			var ctrlPressed = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control);

			// An overflowing strip scrolls under the wheel (Ctrl+wheel still switches tabs); a strip that fits switches tabs
			if (!ctrlPressed && TryGetTabStripScrollViewer() is { ScrollableWidth: > 0 } scrollViewer)
			{
				ScrollTabStripBy(scrollViewer, -delta * TabStripWheelPixelsPerNotch / 120d);
				e.Handled = true;
				return;
			}

			var scrollUpSelectsNextTab = !GeneralSettingsService.ReverseTabScrollDirection;

			if ((delta > 0) == scrollUpSelectsNextTab)
			{
				if (App.AppModel.TabStripSelectedIndex < Items.Count - 1)
					App.AppModel.TabStripSelectedIndex++;
			}
			else
			{
				if (App.AppModel.TabStripSelectedIndex > 0)
					App.AppModel.TabStripSelectedIndex--;
			}

			e.Handled = true;
		}

		// One wheel notch slides roughly one tab
		private const double TabStripWheelPixelsPerNotch = 160;

		private ScrollViewer? tabStripScrollViewer;
		private double tabStripScrollTarget = double.NaN;

		[DynamicWindowsRuntimeCast(typeof(ScrollViewer))]
		private ScrollViewer? TryGetTabStripScrollViewer()
		{
			if (tabStripScrollViewer is { } cached && cached.IsLoaded)
				return cached;

			tabStripScrollViewer = HorizontalTabView.FindDescendant<ScrollViewer>(sv => sv.Name == "ScrollViewer")
				?? HorizontalTabView.FindDescendant<ScrollViewer>();
			if (tabStripScrollViewer is not null)
			{
				tabStripScrollViewer.ViewChanged -= TabStripScrollViewer_ViewChanged;
				tabStripScrollViewer.ViewChanged += TabStripScrollViewer_ViewChanged;
			}

			return tabStripScrollViewer;
		}

		private void TabStripScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
		{
			// The chained animation finished (or the user dragged the strip): the next wheel notch starts from the real offset
			if (!e.IsIntermediate)
				tabStripScrollTarget = double.NaN;
		}

		/// <summary>
		/// Slides the tab strip by <paramref name="pixels"/> with the ScrollViewer's own inertia animation.
		/// Successive notches accumulate on the pending target so fast wheeling glides instead of stuttering.
		/// </summary>
		private void ScrollTabStripBy(ScrollViewer scrollViewer, double pixels)
		{
			var from = double.IsNaN(tabStripScrollTarget) ? scrollViewer.HorizontalOffset : tabStripScrollTarget;
			tabStripScrollTarget = Math.Clamp(from + pixels, 0, scrollViewer.ScrollableWidth);
			scrollViewer.ChangeView(tabStripScrollTarget, null, null, disableAnimation: false);
		}

		/// <summary>Scrolls the strip so the selected tab is fully visible (animated).</summary>
		[DynamicWindowsRuntimeCast(typeof(TabViewItem))]
		private void BringSelectedTabIntoView()
		{
			if (HorizontalTabView.SelectedItem is not TabBarItem selected ||
				HorizontalTabView.ContainerFromItem(selected) is not TabViewItem container ||
				TryGetTabStripScrollViewer() is not { ScrollableWidth: > 0 } scrollViewer)
				return;

			var transform = container.TransformToVisual(scrollViewer);
			var left = transform.TransformPoint(new Windows.Foundation.Point(0, 0)).X;
			var right = left + container.ActualWidth;

			double? target = null;
			if (left < 0)
				target = scrollViewer.HorizontalOffset + left - 8;
			else if (right > scrollViewer.ViewportWidth)
				target = scrollViewer.HorizontalOffset + (right - scrollViewer.ViewportWidth) + 8;

			if (target is { } offset)
			{
				tabStripScrollTarget = Math.Clamp(offset, 0, scrollViewer.ScrollableWidth);
				scrollViewer.ChangeView(tabStripScrollTarget, null, null, disableAnimation: false);
			}
		}

		[DynamicWindowsRuntimeCast(typeof(MenuFlyout))]
		private void TabItemContextMenu_Opening(object sender, object e)
		{
			MenuItemMoveTabToNewWindow.IsEnabled = Items.Count > 1;
			SelectedTabItemChanged?.Invoke(null, ((MenuFlyout)sender).Target.DataContext as TabBarItem);
		}

		private void TabItemContextMenu_Closing(object sender, object e)
		{
			SelectedTabItemChanged?.Invoke(null, null);
		}

		private async void TabBarAddNewTabButton_Drop(object sender, DragEventArgs e)
		{
			if (_lockDropOperation || !FilesystemHelpers.HasDraggedStorageItems(e.DataView))
				return;

			_lockDropOperation = true;

			//|| _droppableArchiveTypes.Contains(x.Name.Split('.').Last().ToLower())
			var items = (await FilesystemHelpers.GetDraggedStorageItems(e.DataView))
				.Where(x => x.ItemType is FilesystemItemType.Directory);

			var deferral = e.GetDeferral();
			try
			{
				foreach (var item in items)
					await NavigationHelpers.OpenPathInNewTab(item.Path, true);

				deferral.Complete();
			}
			catch { }

			_lockDropOperation = false;
		}

		private async void TabBarAddNewTabButton_DragOver(object sender, DragEventArgs e)
		{
			if (!FilesystemHelpers.HasDraggedStorageItems(e.DataView))
			{
				e.AcceptedOperation = DataPackageOperation.None;
				return;
			}

			//|| _droppableArchiveTypes.Contains(x.Name.Split('.').Last().ToLower())
			bool hasValidDraggedItems =
				(await FilesystemHelpers.GetDraggedStorageItems(e.DataView)).Any(x => x.ItemType is FilesystemItemType.Directory);

			if (!hasValidDraggedItems)
			{
				e.AcceptedOperation = DataPackageOperation.None;
				return;
			}

			try
			{
				e.Handled = true;
				var deferral = e.GetDeferral();
				e.DragUIOverride.IsCaptionVisible = true;
				e.DragUIOverride.Caption = string.Format(Strings.OpenInNewTab.GetLocalizedResource());
				e.AcceptedOperation = DataPackageOperation.Link;
				deferral.Complete();
			}
			catch { }
		}

		public override DependencyObject ContainerFromItem(ITabBarItem item)
		{
			return HorizontalTabView.ContainerFromItem(item);
		}

		[DynamicWindowsRuntimeCast(typeof(TabViewItem))]
		[DynamicWindowsRuntimeCast(typeof(ContentControl))]
		private void TabViewItem_Loaded(object sender, RoutedEventArgs e)
		{
			if (sender is TabViewItem tvi && tvi.FindDescendant("IconControl") is ContentControl control)
			{
				control.Content = tvi.IconSource?.CreateIconElement();
				tvi.RegisterPropertyChangedCallback(TabViewItem.IconSourceProperty, (s, args) =>
				{
					if (s is TabViewItem tabViewItem && tabViewItem.FindDescendant("IconControl") is ContentControl iconControl)
						iconControl.Content = tabViewItem.IconSource?.CreateIconElement();
				});
			}
		}

		private void DragAreaRectangle_Loaded(object sender, RoutedEventArgs e)
		{
			this.RunAfterNextRender(UpdateTitleBarInsets);

			// The caption-button inset changes with DPI, maximize/restore and RTL; the strip must follow every time
			HorizontalTabView.SizeChanged -= HorizontalTabView_SizeChanged;
			HorizontalTabView.SizeChanged += HorizontalTabView_SizeChanged;
			MainWindow.Instance.AppWindow.Changed -= AppWindow_Changed;
			MainWindow.Instance.AppWindow.Changed += AppWindow_Changed;
		}

		private void HorizontalTabView_SizeChanged(object sender, SizeChangedEventArgs e)
			=> UpdateTitleBarInsets();

		private void AppWindow_Changed(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
		{
			if (args.DidSizeChange || args.DidPresenterChange)
				DispatcherQueue.TryEnqueue(UpdateTitleBarInsets);
		}

		/// <summary>
		/// Reserves the caption-button area and keeps the tab strip inside the remaining width. TabView lays the
		/// strip out as an Auto column, so without a hard limit a long row of tabs grows under the window's
		/// minimize/maximize/close buttons; clamping the strip presenter instead makes TabView show its scroll
		/// buttons and lets the strip scroll.
		/// </summary>
		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private void UpdateTitleBarInsets()
		{
			if (DragAreaRectangle.XamlRoot is null)
				return;

			var titleBarInset = ((AppLanguageHelper.IsPreferredLanguageRtl
				? MainWindow.Instance.AppWindow.TitleBar.LeftInset
				: MainWindow.Instance.AppWindow.TitleBar.RightInset) / DragAreaRectangle.XamlRoot.RasterizationScale) + 40;

			var reserved = titleBarInset > 40 ? titleBarInset : 138;
			RightPaddingColumn.Width = new(reserved);

			// Everything to the left of the strip (tab actions header) plus everything that must stay to its right
			var headerWidth = HorizontalTabView.FindDescendant<FrameworkElement>(x => x.Name == "LeftContentPresenter")?.ActualWidth ?? 0;
			var available = HorizontalTabView.ActualWidth - headerWidth - TabBarAddNewTabButton.Width - reserved - 8;

			if (HorizontalTabView.FindDescendant<FrameworkElement>(x => x.Name == "TabListView") is { } tabList)
				tabList.MaxWidth = Math.Max(0, available);
		}

		private void HorizontalTabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			TabView_SelectionChanged(sender, e);

			// Keyboard/wheel/command switches may land on a tab that is scrolled out of the strip
			DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, BringSelectedTabIntoView);
		}
	}
}
