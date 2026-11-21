// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Helpers.Application;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System.Windows.Input;
using Windows.Services.Store;
using Windows.System;
using WinRT.Interop;

namespace Files.App.ViewModels
{
	/// <summary>
	/// Represents ViewModel of <see cref="MainPage"/>.
	/// </summary>
	public sealed partial class MainPageViewModel : ObservableObject
	{
		// Dependency injections

		private IAppearanceSettingsService AppearanceSettingsService { get; } = Ioc.Default.GetRequiredService<IAppearanceSettingsService>();
		private IGeneralSettingsService GeneralSettingsService { get; } = Ioc.Default.GetRequiredService<IGeneralSettingsService>();
		private INetworkService NetworkService { get; } = Ioc.Default.GetRequiredService<INetworkService>();
		private IUserSettingsService UserSettingsService { get; } = Ioc.Default.GetRequiredService<IUserSettingsService>();
		private IResourcesService ResourcesService { get; } = Ioc.Default.GetRequiredService<IResourcesService>();
		private DrivesViewModel DrivesViewModel { get; } = Ioc.Default.GetRequiredService<DrivesViewModel>();
		public ShelfViewModel ShelfViewModel { get; } = Ioc.Default.GetRequiredService<ShelfViewModel>();

		private readonly IContentPageContext context = Ioc.Default.GetRequiredService<IContentPageContext>();

		public bool IsDialogMode => App.IsDialogMode;

		public bool IsDialogSaveMode => App.DialogMode == "save";

		public bool IsDialogFolderMode => App.DialogMode == "folder";

		public string DialogPathLabel =>
			!string.IsNullOrWhiteSpace(DialogSession.Current?.Request.NameLabel) ? DialogSession.Current.Request.NameLabel : IsDialogFolderMode
				? Strings.NaiChooseFolder.GetLocalizedResource()
				: Strings.Filename.GetLocalizedResource();

		public string DialogConfirmLabel =>
			!string.IsNullOrWhiteSpace(DialogSession.Current?.Request.OkLabel) ? DialogSession.Current.Request.OkLabel : IsDialogSaveMode
				? Strings.Save.GetLocalizedResource()
				: IsDialogFolderMode
					? Strings.NaiChooseFolder.GetLocalizedResource()
					: Strings.Open.GetLocalizedResource();

		public string DialogFileName
		{
			get => App.DialogFileName ?? string.Empty;
			set
			{
				if (App.DialogFileName == value)
					return;

				App.DialogFileName = value;
				nameFromSelection = false;
				DialogError = string.Empty;
				OnPropertyChanged(nameof(DialogFileName));
				QueueDialogValidation();
			}
		}

		private bool nameFromSelection;
		private string[] lastSelectionPaths = [];
		private string dialogError = string.Empty;
		private DialogSelectionResult validatedSelection = DialogSelectionResult.Failure(DialogSelectionError.Empty);
		private int validationGeneration;
		private bool isValidating;
		private bool isConfirming;
		public bool IsDialogBusy => isValidating || isConfirming;
		public IReadOnlyList<DialogFileType> DialogFileTypes => DialogSession.Current?.Request.FileTypes ?? [];
		public bool HasDialogFileTypes => !IsDialogFolderMode && DialogFileTypes.Count > 0;
		public int DialogFileTypeIndex
		{
			get => (DialogSession.Current?.SelectedTypeIndex ?? 1) - 1;
			set
			{
				if (DialogSession.Current is not { } session || value < 0 || value >= DialogFileTypes.Count) return;
				if (session.SelectedTypeIndex == value + 1) return;
				session.SelectedTypeIndex = value + 1;
				DialogError = string.Empty;
				OnPropertyChanged(nameof(DialogFileTypeIndex));
				if (nameFromSelection)
				{
					if (IsDialogSaveMode) nameFromSelection = false;
					else DialogFileName = string.Empty;
				}
				QueueDialogValidation();
				_ = RefreshDialogFilterAsync();
			}
		}
		public string DialogError
		{
			get => dialogError;
			private set
			{
				if (SetProperty(ref dialogError, value)) NotifyDialogValidation();
			}
		}
		public bool HasDialogError => App.DialogRequestError is not null || !string.IsNullOrEmpty(DialogError);
		public string DialogFeedback => App.DialogRequestError ?? (HasDialogError ? DialogError : isValidating ? Strings.Loading.GetLocalizedResource() : ValidationMessage(validatedSelection));
		public bool HasDialogFeedback => !string.IsNullOrEmpty(DialogFeedback);
		public Microsoft.UI.Xaml.Controls.InfoBarSeverity DialogFeedbackSeverity => HasDialogError
			? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error
			: validatedSelection.Error is DialogSelectionError.Empty or DialogSelectionError.None || isValidating
				? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning;
		internal void ReportDialogError(string error) => DialogError = error;

		public bool CanConfirmDialog => IsDialogMode && App.DialogRequestError is null && !IsDialogBusy && validatedSelection.CanSubmit;

		private Func<DialogSelectionResult> CaptureDialogSelection()
		{
			var mode = App.DialogMode;
			var folder = context.ShellPage?.ShellViewModel?.WorkingDirectory;
			var input = nameFromSelection ? null : DialogFileName;
			var session = DialogSession.Current;
			var selected = context.SelectedItems.Where(item => mode == "save" || session?.Includes(item) != false)
				.Select(item => item.ItemPath).OfType<string>().ToArray();
			return () =>
			{
				var result = DialogSelectionResolver.Resolve(mode, folder, input, selected);
				if (result.NavigateTo is not null) return result;
				if (mode == "save" && !string.IsNullOrWhiteSpace(input) && session is not null)
					result = DialogSelectionResolver.Resolve(mode, folder, session.AddDefaultExtension(input), selected);
				if (mode == "save" && result.Paths.Any(path => FilesystemHelpers.ContainsRestrictedFileName(System.IO.Path.GetFileName(path))))
					return DialogSelectionResult.Failure(DialogSelectionError.InvalidName, input);
				if (session?.StrictTypes == true && result.Paths.Any(path => !session.MatchesFile(path)))
					return DialogSelectionResult.Failure(DialogSelectionError.UnsupportedType, session.SelectedType?.Name);
				return result;
			};
		}

		internal DialogSelectionResult ResolveDialogSelection() => CaptureDialogSelection()();

		private async Task RefreshDialogFilterAsync()
		{
			try { if (context.ShellPage?.ShellViewModel is { } model) await model.ApplyFilesAndFoldersChangesAsync(); }
			catch (Exception ex) { ReportDialogError(ex.Message); }
		}

		private void NotifyDialogValidation()
		{
			if (App.AppModel.IsMainWindowClosed) return;
			OnPropertyChanged(nameof(HasDialogError));
			OnPropertyChanged(nameof(DialogFeedback));
			OnPropertyChanged(nameof(HasDialogFeedback));
			OnPropertyChanged(nameof(DialogFeedbackSeverity));
			OnPropertyChanged(nameof(IsDialogBusy));
			OnPropertyChanged(nameof(CanConfirmDialog));
		}

		private void QueueDialogValidation()
		{
			if (!IsDialogMode || App.AppModel.IsMainWindowClosed) return;
			var revision = ++validationGeneration;
			var resolve = CaptureDialogSelection();
			isValidating = true;
			NotifyDialogValidation();
			_ = ValidateDialogAsync(revision, resolve);
		}

		internal void RefreshDialogValidation() => QueueDialogValidation();

		private async Task ValidateDialogAsync(int revision, Func<DialogSelectionResult> resolve)
		{
			try
			{
				await Task.Delay(120);
				if (revision != validationGeneration || App.AppModel.IsMainWindowClosed) return;
				var result = await Task.Run(resolve);
				if (revision != validationGeneration || App.AppModel.IsMainWindowClosed) return;
				validatedSelection = result;
			}
			catch (Exception ex) { if (revision == validationGeneration) ReportDialogError(ex.Message); }
			finally
			{
				if (revision == validationGeneration) { isValidating = false; NotifyDialogValidation(); }
			}
		}

		private string ValidationMessage(DialogSelectionResult result) => result.Error switch
		{
			DialogSelectionError.None => string.Empty,
			DialogSelectionError.Empty => (IsDialogSaveMode ? Strings.DialogEnterFileName : Strings.DialogChooseFile).GetLocalizedResource(),
			DialogSelectionError.MissingFile => string.Format(Strings.DialogFileMissing.GetLocalizedResource(), result.Detail ?? DialogFileName),
			DialogSelectionError.MissingFolder => string.Format(Strings.DialogFolderMissing.GetLocalizedResource(), result.Detail ?? DialogFileName),
			DialogSelectionError.AccessDenied => string.Format(Strings.DialogAccessDenied.GetLocalizedResource(), result.Detail),
			DialogSelectionError.UnsupportedType => string.Format(Strings.DialogTypeMismatch.GetLocalizedResource(), result.Detail),
			DialogSelectionError.PathTooLong => Strings.RenameErrorTooLongText.GetLocalizedResource(),
			_ => Strings.FileNameTeachingTip_Subtitle.GetLocalizedResource(),
		};

		public async void ConfirmDialog() => await ConfirmDialogAsync();

		public async Task ConfirmDialogAsync()
		{
			if (!IsDialogMode || isConfirming) return;
			if (App.DialogRequestError is { } requestError) { ReportDialogError(requestError); return; }
			isConfirming = true;
			NotifyDialogValidation();
			var revision = validationGeneration;
			try
			{
			var selection = await Task.Run(CaptureDialogSelection());
			if (App.AppModel.IsMainWindowClosed || revision != validationGeneration) return;
			validatedSelection = selection;
			if (selection.NavigateTo is { } folder)
			{
				var preserveSaveName = IsDialogSaveMode && nameFromSelection;
				context.ShellPage?.NavigateToPath(folder);
				if (!preserveSaveName)
					DialogFileName = string.Empty;
				nameFromSelection = false;
				return;
			}
			if (selection.Paths.Count == 0)
			{
				DialogError = ValidationMessage(selection);
				return;
			}
			if (IsDialogSaveMode && (DialogSession.Current?.RequiresOverwritePrompt ?? true)
				&& await Task.Run(() => System.IO.File.Exists(selection.Paths[0])))
			{
				var replace = await DialogDisplayHelper.ShowDialogAsync(Strings.ItemAlreadyExistsDialogTitle.GetLocalizedResource(),
					Strings.ItemAlreadyExistsDialogContent.GetLocalizedResource() + Environment.NewLine + selection.Paths[0],
					Strings.ItemAlreadyExistsDialogSecondaryButtonText.GetLocalizedResource(), Strings.Cancel.GetLocalizedResource());
				if (!replace || revision != validationGeneration || App.AppModel.IsMainWindowClosed) return;
			}
			if (App.TryCompleteDialog(selection.Paths, out var error))
				MainWindow.Instance?.Close();
			else
				DialogError = error;
			}
			catch (Exception ex) { ReportDialogError(ex.Message); }
			finally { isConfirming = false; NotifyDialogValidation(); }
		}

		internal void ConfirmSelection()
		{
			nameFromSelection = true;
			ConfirmDialog();
		}

		public void CancelDialog()
		{
			if (!IsDialogMode)
				return;

			App.DialogConfirmRequested = false;
			MainWindow.Instance?.Close();
		}

		private void SyncDialogFileNameFromSelection()
		{
			if (!IsDialogMode)
				return;
			var paths = context.SelectedItems.Select(item => item.ItemPath).OfType<string>().ToArray();
			if (paths.SequenceEqual(lastSelectionPaths, StringComparer.OrdinalIgnoreCase))
			{
				QueueDialogValidation();
				return;
			}
			lastSelectionPaths = paths;
			if (paths.Length > 0)
			{
				// Selecting a folder must not erase the suggested/typed save filename.
				if (!(IsDialogSaveMode && paths.All(System.IO.Directory.Exists)))
					App.DialogFileName = IsDialogSaveMode
						? System.IO.Path.GetFileName(paths[0])
						: paths.Length == 1 ? paths[0] : string.Join(" ", paths.Select(path => $"\"{path}\""));
				DialogError = string.Empty;
				OnPropertyChanged(nameof(DialogFileName));
			}
			else if (nameFromSelection && !IsDialogSaveMode)
			{
				App.DialogFileName = string.Empty;
				OnPropertyChanged(nameof(DialogFileName));
			}
			nameFromSelection = paths.Length > 0;
			QueueDialogValidation();
		}

		// Properties

		public static ObservableCollection<TabBarItem> AppInstances { get; private set; } = [];

		private static volatile TaskCompletionSource startupTabsTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

		/// <summary>
		/// Gets a task that completes once the startup tabs have been added.
		/// </summary>
		public static Task StartupTabsLoadedTask => startupTabsTcs.Task;

		public List<ITabBar> MultitaskingControls { get; } = [];

		public ITabBar? MultitaskingControl { get; set; }

		private bool _IsSidebarPaneOpen;
		public bool IsSidebarPaneOpen
		{
			get => _IsSidebarPaneOpen;
			set => SetProperty(ref _IsSidebarPaneOpen, value);
		}

		private bool _IsSidebarPaneOpenToggleButtonVisible;
		public bool IsSidebarPaneOpenToggleButtonVisible
		{
			get => _IsSidebarPaneOpenToggleButtonVisible;
			set => SetProperty(ref _IsSidebarPaneOpenToggleButtonVisible, value);
		}

		private TabBarItem? selectedTabItem;
		public TabBarItem? SelectedTabItem
		{
			get => selectedTabItem;
			set => SetProperty(ref selectedTabItem, value);
		}

		private bool shouldPreviewPaneBeActive;
		public bool ShouldPreviewPaneBeActive
		{
			get => shouldPreviewPaneBeActive;
			set => SetProperty(ref shouldPreviewPaneBeActive, value);
		}

		private bool shouldPreviewPaneBeDisplayed;
		public bool ShouldPreviewPaneBeDisplayed
		{
			get => shouldPreviewPaneBeDisplayed;
			set => SetProperty(ref shouldPreviewPaneBeDisplayed, value);
		}

		public bool ShowShelfPane
			=> GeneralSettingsService.ShowShelfPane && AppLifecycleHelper.AppEnvironment is AppEnvironment.Dev;

		public Stretch AppThemeBackgroundImageFit
			=> AppearanceSettingsService.AppThemeBackgroundImageFit;

		public float AppThemeBackgroundImageOpacity
			=> AppearanceSettingsService.AppThemeBackgroundImageOpacity;

		public ImageSource? AppThemeBackgroundImageSource
		{
			get
			{
				if (string.IsNullOrWhiteSpace(AppearanceSettingsService.AppThemeBackgroundImageSource))
					return null;

				if (!Uri.TryCreate(AppearanceSettingsService.AppThemeBackgroundImageSource, UriKind.RelativeOrAbsolute, out Uri? validUri))
					return null;

				try
				{
					return new BitmapImage(validUri);
				}
				catch (Exception)
				{
					// Catch potential errors
					return null;
				}
			}
		}

		public VerticalAlignment AppThemeBackgroundImageVerticalAlignment
			=> AppearanceSettingsService.AppThemeBackgroundImageVerticalAlignment;

		public HorizontalAlignment AppThemeBackgroundImageHorizontalAlignment
			=> AppearanceSettingsService.AppThemeBackgroundImageHorizontalAlignment;

		public bool ShowToolbar =>
			AppearanceSettingsService.ShowToolbar &&
			!IsDialogMode &&
			context.PageType is not ContentPageTypes.Home &&
			context.PageType is not ContentPageTypes.ReleaseNotes &&
			context.PageType is not ContentPageTypes.Settings;

		private bool canShowPrompts;

		public void OnPageLoaded()
		{
			if (canShowPrompts)
				return;

			canShowPrompts = true;
			OnPropertyChanged(nameof(ShowReviewPrompt));
			OnPropertyChanged(nameof(ShowSponsorPrompt));
		}

		private static bool hasShownReviewPrompt;

		public bool ShowReviewPrompt
		{
			get
			{
				if (!canShowPrompts || hasShownReviewPrompt)
					return false;

				var isTargetEnvironment = AppLifecycleHelper.AppEnvironment is AppEnvironment.StoreStable or AppEnvironment.StorePreview;
				var hasClickedReviewPrompt = UserSettingsService.ApplicationSettingsService.HasClickedReviewPrompt;
				var launchCountReached = AppLifecycleHelper.TotalLaunchCount % 30 == 0;

				hasShownReviewPrompt = isTargetEnvironment && !hasClickedReviewPrompt && launchCountReached;
				return hasShownReviewPrompt;
			}
		}

		// Ensures the sponsor prompt is only displayed once per app session
		private static bool hasShownSponsorPrompt;

		public bool ShowSponsorPrompt
		{
			get
			{
				// Sponsor prompt removed (NAI_Files fork).
				return false;
			}
		}

		// Commands

		public ICommand NavigateToNumberedTabKeyboardAcceleratorCommand { get; }
		public ICommand ReviewAppCommand { get; }
		public ICommand SponsorCommand { get; }
		public ICommand OpenNetworkSharingSettingsCommand { get; }

		// Constructor

		public MainPageViewModel()
		{
			NaiLanguageBridge.StartupStage("mainpagevm-ctor-start");
			NavigateToNumberedTabKeyboardAcceleratorCommand = new RelayCommand<KeyboardAcceleratorInvokedEventArgs>(ExecuteNavigateToNumberedTabKeyboardAcceleratorCommand);
			ReviewAppCommand = new RelayCommand(ExecuteReviewAppCommand);
			SponsorCommand = new RelayCommand(ExecuteSponsorCommand);
			OpenNetworkSharingSettingsCommand = new AsyncRelayCommand(ExecuteOpenNetworkSharingSettingsCommand);

			AppearanceSettingsService.PropertyChanged += (s, e) =>
			{
				switch (e.PropertyName)
				{
					case nameof(AppearanceSettingsService.AppThemeBackgroundImageSource):
						OnPropertyChanged(nameof(AppThemeBackgroundImageSource));
						break;
					case nameof(AppearanceSettingsService.AppThemeBackgroundImageOpacity):
						OnPropertyChanged(nameof(AppThemeBackgroundImageOpacity));
						break;
					case nameof(AppearanceSettingsService.AppThemeBackgroundImageFit):
						OnPropertyChanged(nameof(AppThemeBackgroundImageFit));
						break;
					case nameof(AppearanceSettingsService.AppThemeBackgroundImageVerticalAlignment):
						OnPropertyChanged(nameof(AppThemeBackgroundImageVerticalAlignment));
						break;
					case nameof(AppearanceSettingsService.AppThemeBackgroundImageHorizontalAlignment):
						OnPropertyChanged(nameof(AppThemeBackgroundImageHorizontalAlignment));
						break;
					case nameof(AppearanceSettingsService.ShowToolbar):
						OnPropertyChanged(nameof(ShowToolbar));
						break;
				}
			};

			context.PropertyChanged += (s, e) =>
			{
				switch (e.PropertyName)
				{
					case nameof(context.PageType):
						OnPropertyChanged(nameof(ShowToolbar));
						OnPropertyChanged(nameof(CanConfirmDialog));
						break;
					case nameof(context.HasSelection):
					case nameof(context.SelectedItem):
					case nameof(context.SelectedItems):
					case nameof(context.Folder):
					case nameof(context.ShellPage):
						OnPropertyChanged(nameof(CanConfirmDialog));
						SyncDialogFileNameFromSelection();
						break;
				}
			};

			GeneralSettingsService.PropertyChanged += (s, e) =>
			{
				switch (e.PropertyName)
				{
					case nameof(GeneralSettingsService.ShowShelfPane):
						OnPropertyChanged(nameof(ShowShelfPane));
						break;
				}
			};
			NaiLanguageBridge.StartupStage("mainpagevm-ctor-done");
		}

		// Methods

		public async Task OnNavigatedToAsync(NavigationEventArgs e)
		{
			if (e.NavigationMode == NavigationMode.Back)
				return;

			NaiLanguageBridge.StartupStage("mainpage-nav-start");
			var parameter = e.Parameter;
			var ignoreStartupSettings = false;
			if (parameter is MainPageNavigationArguments mainPageNavigationArguments)
			{
				parameter = mainPageNavigationArguments.Parameter;
				ignoreStartupSettings = mainPageNavigationArguments.IgnoreStartupSettings;
			}

			// Re-arm the signal when tabs load again after closing to the background
			if (startupTabsTcs.Task.IsCompleted)
				startupTabsTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

			try
			{
				if (parameter is null || (parameter is string eventStr && string.IsNullOrEmpty(eventStr)))
				{
					try
					{
						// add last session tabs to closed tabs stack if those tabs are not about to be opened
						if (!UserSettingsService.AppSettingsService.RestoreTabsOnStartup && !UserSettingsService.GeneralSettingsService.ContinueLastSessionOnStartUp && UserSettingsService.GeneralSettingsService.LastSessionTabList != null)
						{
							var items = UserSettingsService.GeneralSettingsService.LastSessionTabList
								.Where(tab => !string.IsNullOrEmpty(tab))
								.Select(tab => TabBarItemParameter.Deserialize(tab)).ToArray();

							BaseTabBar.PushRecentTab(items);
						}

						if (UserSettingsService.AppSettingsService.RestoreTabsOnStartup)
						{
							UserSettingsService.AppSettingsService.RestoreTabsOnStartup = false;
							if (UserSettingsService.GeneralSettingsService.LastSessionTabList is not null)
							{
								await RestoreSessionTabsAsync(UserSettingsService.GeneralSettingsService.LastSessionTabList);

								if (!UserSettingsService.GeneralSettingsService.ContinueLastSessionOnStartUp)
									UserSettingsService.GeneralSettingsService.LastSessionTabList = null;
							}
						}
						else if (UserSettingsService.GeneralSettingsService.OpenSpecificPageOnStartup &&
							UserSettingsService.GeneralSettingsService.TabsOnStartupList is not null)
						{
							foreach (string path in UserSettingsService.GeneralSettingsService.TabsOnStartupList)
								await NavigationHelpers.AddNewTabByPathAsync(typeof(ShellPanesPage), path, true);
						}
						else if (UserSettingsService.GeneralSettingsService.ContinueLastSessionOnStartUp &&
							UserSettingsService.GeneralSettingsService.LastSessionTabList is not null)
						{
							if (AppInstances.Count == 0)
								await RestoreSessionTabsAsync(UserSettingsService.GeneralSettingsService.LastSessionTabList);
						}
						else
						{
							NaiLanguageBridge.StartupStage("before-first-tab");
							await NavigationHelpers.AddNewTabAsync();
							NaiLanguageBridge.StartupStage("after-first-tab");
						}
					}
					catch
					{
						await NavigationHelpers.AddNewTabAsync();
					}
				}
				else
				{
					if (!ignoreStartupSettings)
					{
						try
						{
							if (UserSettingsService.GeneralSettingsService.OpenSpecificPageOnStartup &&
									UserSettingsService.GeneralSettingsService.TabsOnStartupList is not null)
							{
								foreach (string path in UserSettingsService.GeneralSettingsService.TabsOnStartupList)
									await NavigationHelpers.AddNewTabByPathAsync(typeof(ShellPanesPage), path, true);
							}
							else if (UserSettingsService.GeneralSettingsService.ContinueLastSessionOnStartUp &&
								UserSettingsService.GeneralSettingsService.LastSessionTabList is not null &&
								AppInstances.Count == 0)
							{
								await RestoreSessionTabsAsync(UserSettingsService.GeneralSettingsService.LastSessionTabList);
							}
						}
						catch { }
					}

					if (parameter is string navArgs)
						await NavigationHelpers.AddNewTabByPathAsync(typeof(ShellPanesPage), navArgs, true);
					else if (parameter is PaneNavigationArguments paneArgs)
						await NavigationHelpers.AddNewTabByParamAsync(typeof(ShellPanesPage), paneArgs);
					else if (parameter is TabBarItemParameter tabArgs)
						await NavigationHelpers.AddNewTabByParamAsync(tabArgs.InitialPageType, tabArgs.NavigationParameter);
				}
			}
			finally
			{
				startupTabsTcs.TrySetResult();
				NaiLanguageBridge.StartupStage("tabs-loaded");
			}

			// Load the app theme resources
			ResourcesService.LoadAppResources(AppearanceSettingsService);

			await Task.WhenAll(
				DrivesViewModel.UpdateDrivesAsync(),
				NetworkService.UpdateComputersAsync(),
				NetworkService.UpdateShortcutsAsync());
			NaiLanguageBridge.StartupStage("startup-drives-done");
		}

		private async Task RestoreSessionTabsAsync(List<string> sessionTabs)
		{
			if (sessionTabs is null || sessionTabs.Count == 0)
				return;

			var savedIndex = UserSettingsService.GeneralSettingsService.LastSessionSelectedTabIndex;
			if (savedIndex < 0 || savedIndex >= sessionTabs.Count)
				savedIndex = sessionTabs.Count - 1;

			// Load the previously focused tab first so the user can interact with it while the rest load.
			var focusedArgs = TabBarItemParameter.Deserialize(sessionTabs[savedIndex]);
			await NavigationHelpers.AddNewTabByParamAsync(focusedArgs.InitialPageType, focusedArgs.NavigationParameter);

			// Append the remaining tabs in their original order without changing the selection.
			for (int i = 0; i < sessionTabs.Count; i++)
			{
				if (i == savedIndex)
					continue;

				var args = TabBarItemParameter.Deserialize(sessionTabs[i]);
				await NavigationHelpers.AddNewTabByParamAsync(args.InitialPageType, args.NavigationParameter, switchToNewTab: false);
			}

			// Move the focused tab from position 0 to its original index so the tab order matches the saved session.
			if (savedIndex > 0 && savedIndex < AppInstances.Count)
			{
				AppInstances.Move(0, savedIndex);
				App.AppModel.TabStripSelectedIndex = savedIndex;
			}
		}

		// Command methods

		private async void ExecuteReviewAppCommand()
		{
			OnPropertyChanged(nameof(ShowReviewPrompt));

			try
			{
				var storeContext = StoreContext.GetDefault();
				InitializeWithWindow.Initialize(storeContext, MainWindow.Instance.WindowHandle);
				var result = await storeContext.RequestRateAndReviewAppAsync();
				if (result.Status is StoreRateAndReviewStatus.Succeeded)
					UserSettingsService.ApplicationSettingsService.HasClickedReviewPrompt = true;
			}
			catch (Exception) { }
		}

		private void ExecuteSponsorCommand()
		{
			// Sponsor link removed (NAI_Files fork).
			UserSettingsService.ApplicationSettingsService.HasClickedSponsorPrompt = true;
			OnPropertyChanged(nameof(ShowSponsorPrompt));
		}

		private async Task ExecuteOpenNetworkSharingSettingsCommand()
		{
			await NetworkService.OpenNetworkSharingSettingsAsync();
		}

		private async void ExecuteNavigateToNumberedTabKeyboardAcceleratorCommand(KeyboardAcceleratorInvokedEventArgs? e)
		{
			if (e is null)
				return;

			var indexToSelect = e.KeyboardAccelerator.Key switch
			{
				VirtualKey.Number1 => 0,
				VirtualKey.Number2 => 1,
				VirtualKey.Number3 => 2,
				VirtualKey.Number4 => 3,
				VirtualKey.Number5 => 4,
				VirtualKey.Number6 => 5,
				VirtualKey.Number7 => 6,
				VirtualKey.Number8 => 7,
				VirtualKey.Number9 => AppInstances.Count - 1,
				_ => AppInstances.Count - 1,
			};

			// Only select the tab if it is in the list
			if (indexToSelect < AppInstances.Count)
			{
				App.AppModel.TabStripSelectedIndex = indexToSelect;

				// Small delay for the UI to load
				await Task.Delay(500);

				// Focus the content of the selected tab item (needed for keyboard navigation)
				var paneHolder = context.ShellPage.GetRequiredPaneHolder();
				paneHolder.FocusActivePane();
			}

			e.Handled = true;
		}

	}
}
