// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Helpers.Application;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.IO;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Windows.Win32.Foundation;
using WinRT;
using IO = System.IO;

namespace Files.App
{
	public sealed partial class MainWindow : WindowEx
	{
		private static MainWindow? _Instance;
		public static MainWindow Instance => _Instance ??= new();

		private bool CanWindowToFront { get; set; } = true;
		private readonly Lock _canWindowToFrontLock = new();
		private bool _isWindowIconInitialized;

		protected override bool PersistPlacement => !DialogSession.IsDialogLaunch && !App.IsDialogMode;

		public MainWindow() : base(416, 316)
		{
			InitializeComponent();

			ExtendsContentIntoTitleBar = true;
			Title = "Files";
			AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
			AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
			AppWindow.TitleBar.ButtonPressedBackgroundColor = Colors.Transparent;
			AppWindow.TitleBar.ButtonHoverBackgroundColor = Colors.Transparent;
		}

		private void InitializeWindowIcon()
		{
			if (_isWindowIconInitialized)
				return;

			AppWindow.SetIcon(AppLifecycleHelper.AppIconPath);
			_isWindowIconInitialized = true;
		}

		internal async Task<bool> ActivateWithBackdropAsync()
		{
			var rootFrame = EnsureWindowIsInitialized();
			if (rootFrame is null)
				return false;

			// Submit the lightweight backdrop before showing the window with its normal animation.
			await CompositionTarget.GetCompositorForCurrentThread().RequestCommitAsync();

			var firstFrame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			CompositionTarget.Rendered += OnRendered;
			Closed += OnClosed;
			try
			{
				Activate();
				// Let the backdrop paint before main-page construction occupies the UI thread.
				return await firstFrame.Task;
			}
			finally
			{
				CompositionTarget.Rendered -= OnRendered;
				Closed -= OnClosed;
			}

			void OnRendered(object? sender, RenderedEventArgs args)
			{
				if (rootFrame.IsLoaded)
					firstFrame.TrySetResult(true);
			}

			void OnClosed(object sender, WindowEventArgs args)
				=> firstFrame.TrySetResult(false);
		}

		public void ShowSplashScreen()
		{
			var rootFrame = EnsureWindowIsInitialized();

			rootFrame?.Navigate(typeof(SplashScreenPage));
		}

		[DynamicWindowsRuntimeCast(typeof(OverlappedPresenter))]
		public async Task InitializeApplicationAsync(object? activatedEventArgs)
		{
			NaiLanguageBridge.StartupStage("init-app-start");
			var rootFrame = EnsureWindowIsInitialized();

			if (rootFrame is null)
				return;
			NaiLanguageBridge.StartupStage("init-app-before-nav");

			switch (activatedEventArgs)
			{
				case ILaunchActivatedEventArgs launchArgs:
					if (launchArgs.Arguments is not null &&
						(CommandLineParser.SplitArguments(launchArgs.Arguments, true)[0].EndsWith($"files-dev.exe", StringComparison.OrdinalIgnoreCase)
						|| CommandLineParser.SplitArguments(launchArgs.Arguments, true)[0].EndsWith($"files-dev", StringComparison.OrdinalIgnoreCase)
						|| CommandLineParser.SplitArguments(launchArgs.Arguments, true)[0].Equals(Path.Join(Package.Current.InstalledPath, "Files.exe"), StringComparison.OrdinalIgnoreCase)))
					{
						// WINUI3: When launching from commandline the argument is not ICommandLineActivatedEventArgs (#10370)
						var ppm = CommandLineParser.ParseUntrustedCommands(launchArgs.Arguments);
						ApplyDialogActivationState(ppm);
						if (ppm.IsEmpty())
							rootFrame.Navigate(typeof(MainPage), null, new SuppressNavigationTransitionInfo());
						else
							await InitializeFromCmdLineArgsAsync(rootFrame, ppm, Program.ConsumeLaunchCwd());
					}
					else if (rootFrame.Content is null || rootFrame.Content is SplashScreenPage || !MainPageViewModel.AppInstances.Any())
					{
						// When the navigation stack isn't restored navigate to the first page,
						// configuring the new page by passing required information as a navigation parameter
						rootFrame.Navigate(typeof(MainPage), launchArgs.Arguments, new SuppressNavigationTransitionInfo());
					}
					else if (!(string.IsNullOrEmpty(launchArgs.Arguments) && MainPageViewModel.AppInstances.Count > 0))
					{
						// Bring to foreground (#14730)
						Win32Helper.BringToForegroundEx(new(WindowHandle));

						await NavigationHelpers.AddNewTabByPathAsync(typeof(ShellPanesPage), launchArgs.Arguments, true);
					}
					else
					{
						rootFrame.Navigate(typeof(MainPage), null, new SuppressNavigationTransitionInfo());
					}
					break;

				case IProtocolActivatedEventArgs eventArgs:
					if (eventArgs.Uri.AbsoluteUri == "files-dev:")
					{
						rootFrame.Navigate(typeof(MainPage), null, new SuppressNavigationTransitionInfo());

						// Bring to foreground (#14730)
						Win32Helper.BringToForegroundEx(new(WindowHandle));

						// Ensure app-level keyboard shortcuts work immediately after Win+E activation.
						_ = EnsureContentHasKeyboardFocusAsync();
					}
					else
					{
						var parsedArgs = eventArgs.Uri.Query.TrimStart('?').Split('=');
						var unescapedValue = Program.DecodeDialogCommand(parsedArgs[1].Split('&')[0]);
						if (parsedArgs[0] == "tab" && parsedArgs.Length > 3 &&
							int.TryParse(parsedArgs[2].Split('&')[0], out var dx) &&
							int.TryParse(parsedArgs[3], out var dy))
							AppWindow?.Move(new(dx - 100, dy - 16));
						var folderResult = await FilesystemTasks.Wrap(() => StorageFolder.GetFolderFromPathAsync(unescapedValue).AsTask());
						if (folderResult.Result is { } folder && !string.IsNullOrEmpty(folder.Path))
						{
							// Convert short name to long name (#6190)
							unescapedValue = folder.Path;
						}
						switch (parsedArgs[0])
						{
							case "tab":
								rootFrame.Navigate(typeof(MainPage),
									new MainPageNavigationArguments() { Parameter = TabBarItemParameter.Deserialize(unescapedValue), IgnoreStartupSettings = true },
									new SuppressNavigationTransitionInfo());
								break;

							case "folder":
								rootFrame.Navigate(typeof(MainPage),
									new MainPageNavigationArguments() { Parameter = unescapedValue, IgnoreStartupSettings = true },
									new SuppressNavigationTransitionInfo());
								break;

							case "cmd":
								var ppm = CommandLineParser.ParseUntrustedCommands(unescapedValue);
								ApplyDialogActivationState(ppm);
								if (ppm.IsEmpty())
									rootFrame.Navigate(typeof(MainPage), null, new SuppressNavigationTransitionInfo());
								else
									await InitializeFromCmdLineArgsAsync(rootFrame, ppm, Environment.CurrentDirectory);
								break;
							default:
								rootFrame.Navigate(typeof(MainPage), null, new SuppressNavigationTransitionInfo());
								break;
						}
					}
					break;

				case ICommandLineActivatedEventArgs cmdLineArgs:
					var operation = cmdLineArgs.Operation;
					var cmdLineString = operation.Arguments;
					var activationPath = operation.CurrentDirectoryPath;

					var parsedCommands = CommandLineParser.ParseUntrustedCommands(cmdLineString);
					if (parsedCommands is not null && parsedCommands.Count > 0)
					{
						ApplyDialogActivationState(parsedCommands);
						await InitializeFromCmdLineArgsAsync(rootFrame, parsedCommands, activationPath);
					}
					else
					{
						rootFrame.Navigate(typeof(MainPage), null, new SuppressNavigationTransitionInfo());
					}
					break;

				case IFileActivatedEventArgs fileArgs:
					try
					{
						if (fileArgs.Files is null || fileArgs.Files.Count == 0)
						{
							break;
						}

						var index = 0;
						if (rootFrame.Content is null || rootFrame.Content is SplashScreenPage || !MainPageViewModel.AppInstances.Any())
						{
							// When the navigation stack isn't restored navigate to the first page,
							// configuring the new page by passing required information as a navigation parameter
							rootFrame.Navigate(typeof(MainPage), fileArgs.Files.First().Path, new SuppressNavigationTransitionInfo());
							index = 1;
						}
						else
						{
							// Bring to foreground (#14730)
							Win32Helper.BringToForegroundEx(new(WindowHandle));
						}

						for (; index < fileArgs.Files.Count; index++)
						{
							await NavigationHelpers.AddNewTabByPathAsync(typeof(ShellPanesPage), fileArgs.Files[index].Path, true);
						}
					}
					catch (Exception ex)
					{
						App.Logger.LogWarning(ex, "Failed to open files.");
						if (rootFrame.Content is null || rootFrame.Content is SplashScreenPage || !MainPageViewModel.AppInstances.Any())
							rootFrame.Navigate(typeof(MainPage), null, new SuppressNavigationTransitionInfo());
						else
							Win32Helper.BringToForegroundEx(new(WindowHandle));
					}
					break;

				case IStartupTaskActivatedEventArgs startupArgs:
					// Just launch the app with no arguments
					rootFrame.Navigate(typeof(MainPage), null, new SuppressNavigationTransitionInfo());
					break;

				default:
					NaiLanguageBridge.StartupStage("init-app-navigating-mainpage");
					// Just launch the app with no arguments
					rootFrame.Navigate(typeof(MainPage), null, new SuppressNavigationTransitionInfo());
					NaiLanguageBridge.StartupStage("init-app-navigated");
					break;
			}

			var appWindow = AppWindow;
			if (App.IsDialogMode)
				ConfigureDialogWindow(appWindow);
			if (appWindow is not null && !appWindow.IsVisible)
			{
				// When resuming the cached instance
				appWindow.Show();
				Activate();

				// Bring to foreground (#14730) in case Activate() doesn't
				Win32Helper.BringToForegroundEx(new(WindowHandle));
			}

			if (Windows.Win32.PInvoke.IsIconic(new(WindowHandle)) && appWindow?.Presenter is OverlappedPresenter overlapped)
				overlapped.Restore();
		}

		private async Task EnsureContentHasKeyboardFocusAsync()
		{
			await Task.Delay(100);
			var shellPage = Ioc.Default.GetService<IContentPageContext>()?.ShellPage;
			if (shellPage is not null)
			{
				var paneHolder = shellPage.GetRequiredPaneHolder();
				paneHolder.FocusActivePane();
			}
		}

		/// <summary>
		/// Open/save dialogs get a compact, centered, non-maximizable window — the
		/// native dialog footprint instead of the full workspace.
		/// </summary>
		private static void ConfigureDialogWindow(AppWindow? appWindow)
		{
			if (appWindow is null)
				return;

			// A native open/save dialog must not extend its content into the title bar: the OS caption
			// buttons (close/min) stay in their own row so Files' own top-right buttons (queue/status)
			// never collide with them. Normal windows keep the custom title bar + drag regions.
			Instance.ExtendsContentIntoTitleBar = false;
			appWindow.TitleBar.ExtendsContentIntoTitleBar = false;

// Classic 5:4 ("multisync") footprint, sized to the display's work area so the
			// picker stays a comfortable, perfectly centered window on any resolution/DPI
			// (4K included). The ratio is a hard identity — persisted per-caller sizes are
			// snapped back to 5:4, never honored width-blind.
			const float DialogAspect = 5f / 4f;
			// Default height ≈ 72% of the work area; width follows at 5:4.
			const double DialogHeightFraction = 0.72;
			var dialogWidth = 0;
			var dialogHeight = 0;
			var title = DialogSession.Current?.Request.Title;
			appWindow.Title = !string.IsNullOrWhiteSpace(title) ? title
				: App.DialogMode == "save" ? Strings.Save.GetLocalizedResource()
				: App.DialogMode == "folder" ? Strings.NaiChooseFolder.GetLocalizedResource() : Strings.Open.GetLocalizedResource();

			if (Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(appWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest) is { } area)
			{
				var workArea = area.WorkArea;
				// Start from the persisted/remembered size, then snap it onto the 5:4 line.
				var rememberedWidth = Math.Max(0, DialogSession.Current?.State.Width ?? 0);
				var rememberedHeight = Math.Max(0, DialogSession.Current?.State.Height ?? 0);
				var hasRemembered = rememberedWidth > 0 && rememberedHeight > 0;

				if (hasRemembered)
				{
					// Snap the remembered footprint onto 5:4, anchored by the smaller dimension.
					if (rememberedWidth >= rememberedHeight)
					{
						dialogWidth = rememberedWidth;
						dialogHeight = (int)Math.Round(rememberedWidth / DialogAspect);
					}
					else
					{
						dialogHeight = rememberedHeight;
						dialogWidth = (int)Math.Round(rememberedHeight * DialogAspect);
					}
				}
				else
				{
					// Fresh footprint: 72% of work-area height at exactly 5:4.
					dialogHeight = (int)Math.Round(workArea.Height * DialogHeightFraction);
					dialogWidth = (int)Math.Round(dialogHeight * DialogAspect);
				}

				// Guard against absurdly small remembered sizes and clamp to the work area.
				dialogHeight = Math.Clamp(dialogHeight, 480, workArea.Height);
				dialogWidth = Math.Clamp(dialogWidth, 600, workArea.Width);

				appWindow.Resize(new Windows.Graphics.SizeInt32(dialogWidth, dialogHeight));
				appWindow.Move(new Windows.Graphics.PointInt32(
					workArea.X + (workArea.Width - dialogWidth) / 2,
					workArea.Y + (workArea.Height - dialogHeight) / 2));
			}

			if (appWindow.Presenter is OverlappedPresenter overlapped)
			{
				overlapped.IsMaximizable = false;
				overlapped.IsMinimizable = false;
			}
		}

		[DynamicWindowsRuntimeCast(typeof(Frame))]
		private Frame? EnsureWindowIsInitialized()
		{
			try
			{
				// NOTE:
				//  Do not repeat app initialization when the Window already has content,
				//  just ensure that the window is active
				if (Instance.Content is not Frame rootFrame)
				{
					// Create a Frame to act as the navigation context and navigate to the first page
					rootFrame = new() { CacheSize = 1 };
					rootFrame.Loaded += (_, _) =>
					{
						if (!_isWindowIconInitialized)
							rootFrame.RunAfterNextRender(InitializeWindowIcon);
					};
					rootFrame.NavigationFailed += (s, e) =>
					{
						throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
					};

					// Place the frame in the current Window
					Instance.Content = rootFrame;
					Ioc.Default.GetRequiredService<IAppThemeModeService>().SetAppThemeMode(this, callThemeModeChangedEvent: false);
				}

				if (SystemBackdrop is null)
				{
					// Initialize the active material directly instead of transitioning from its inactive fallback.
					var backdrop = new AppSystemBackdrop();
					backdrop.SetInputActive(true);
					SystemBackdrop = backdrop;
				}

				return rootFrame;
			}
			catch (COMException)
			{
				return null;
			}
		}

		/// <summary>
		/// Applies the dialog activation state (mode / output path / suggested file
		/// name) as early as possible — before the MainPage is navigated to — so the
		/// dialog layout (compact window, bottom action bar, tab-bar gating) and the
		/// toolbar CTA bindings see <see cref="App.IsDialogMode"/> from the start.
		/// The regular command apply pass re-runs these cases idempotently.
		/// </summary>
		private static void ApplyDialogActivationState(ParsedCommands commands)
		{
			if (commands.Any(command => command.Type == ParsedCommandType.OutputPath))
			{
				App.DialogConfirmRequested = false;
				App.DialogFileName = null;
				App.DialogRequestError = null;
				App.DialogMode = "open";
				App.DialogTraceRequested = commands.Any(command => command.Type == ParsedCommandType.DialogTrace);
				App.DialogTraceForWriter = commands.Any(command => command.Type == ParsedCommandType.DialogTrace && command.Payload == "writer");
			}
			foreach (var command in commands)
			{
				switch (command.Type)
				{
					case ParsedCommandType.DialogMode:
						App.DialogMode = command.Payload;
						break;
					case ParsedCommandType.OutputPath:
						App.OutputPath = command.Payload;
						DialogResultWriter.Started(command.Payload);
						break;
				}
			}

			foreach (var command in commands)
			{
				if (command.Type == ParsedCommandType.SelectItem)
					App.DialogFileName = System.IO.Path.GetFileName(command.Payload) ?? string.Empty;
				else if (command.Type == ParsedCommandType.OpenDirectory && App.DialogMode == "folder")
					App.DialogFileName = command.Payload;
			}
			if (App.OutputPath is { } output && commands.FirstOrDefault(command => command.Type == ParsedCommandType.DialogRequest) is { } request)
			{
				try { DialogSession.Load(output, request.Payload); }
				catch (Exception ex)
				{
					App.Logger.LogError(ex, "Dialog request could not be read");
					App.DialogRequestError = ex.Message;
				}
				if (DialogSession.Current is { } session)
				{
					App.DialogMode = session.Request.Mode;
					var name = session.Request.SuggestedName;
					App.DialogFileName = name;
				}
			}

			App.Logger.LogInformation("DIALOGSTATE early-apply mode={Mode} output={Output} save={Save}", App.DialogMode, App.OutputPath, App.DialogFileName);
		}

		private async Task InitializeFromCmdLineArgsAsync(Frame rootFrame, ParsedCommands parsedCommands, string activationPath = "")
		{
			async Task PerformNavigationAsync(string? payload, string? selectItem = null)
			{
				if (!string.IsNullOrEmpty(payload))
				{
					payload = ShellHelpers.ResolveShellPath(payload);
					var folderResult = await FilesystemTasks.Wrap(() => StorageFolder.GetFolderFromPathAsync(payload).AsTask());
					if (folderResult.Result is { } folder && !string.IsNullOrEmpty(folder.Path))
						payload = folder.Path; // Convert short name to long name (#6190)
				}

				var generalSettingsService = Ioc.Default.GetService<IGeneralSettingsService>();

				double boundsWidth = 0;
				try
				{
					boundsWidth = Bounds.Width;
				}
				catch (Exception ex)
				{
					// Handle exception in case WinUI Windows is closed
					// (see upstream issue #15599)

					App.Logger.LogWarning(ex, ex.Message);
					return;
				}

				var paneNavigationArgs = new PaneNavigationArguments
				{
					LeftPaneNavPathParam = payload,
					LeftPaneSelectItemParam = selectItem,
					RightPaneNavPathParam = boundsWidth > Constants.UI.MultiplePaneWidthThreshold && (generalSettingsService?.AlwaysOpenDualPaneInNewTab ?? false) ? "Home" : null,
				};

				if (rootFrame.Content is MainPage && MainPageViewModel.AppInstances.Any())
				{
					// Bring to foreground (#14730)
					Win32Helper.BringToForegroundEx(new(WindowHandle));

					var existingTabIndex = MainPageViewModel.AppInstances
						.Select((tabItem, idx) => new { tabItem, idx })
						.FirstOrDefault(x =>
							x.tabItem.NavigationParameter!.NavigationParameter is PaneNavigationArguments paneArgs &&
							(paneNavigationArgs.LeftPaneNavPathParam == paneArgs.LeftPaneNavPathParam ||
							paneNavigationArgs.LeftPaneNavPathParam == paneArgs.RightPaneNavPathParam))?.idx ?? -1;

					if (existingTabIndex >= 0)
						App.AppModel.TabStripSelectedIndex = existingTabIndex;
					else
						await NavigationHelpers.AddNewTabByParamAsync(typeof(ShellPanesPage), paneNavigationArgs);
				}
				else
					rootFrame.Navigate(typeof(MainPage), paneNavigationArgs, new SuppressNavigationTransitionInfo());
			}
			if (DialogSession.Current is { } dialog && !App.DialogTraceRequested)
			{
				var initialFolder = await Task.Run(dialog.InitialFolder);
				await PerformNavigationAsync(initialFolder, IO.Path.GetFileName(App.DialogFileName));
				return;
			}
			foreach (var command in parsedCommands)
			{
				switch (command.Type)
				{
					case ParsedCommandType.OpenDirectory:
					case ParsedCommandType.OpenPath:
					case ParsedCommandType.ExplorerShellCommand:
						var selectItemCommand = parsedCommands.FirstOrDefault(x => x.Type == ParsedCommandType.SelectItem);
						await PerformNavigationAsync(command.Payload, selectItemCommand?.Payload);
						break;

					case ParsedCommandType.SelectItem:
						if (IO.Path.IsPathRooted(command.Payload))
						{
							App.DialogFileName = IO.Path.GetFileName(command.Payload) ?? string.Empty;
							await PerformNavigationAsync(IO.Path.GetDirectoryName(command.Payload), IO.Path.GetFileName(command.Payload));
						}
						else if (!string.IsNullOrWhiteSpace(command.Payload))
						{
							App.DialogFileName = command.Payload;
						}
						break;

					case ParsedCommandType.DialogMode:
						App.DialogMode = command.Payload;
						break;

					case ParsedCommandType.TagFiles:
						var tagService = Ioc.Default.GetRequiredService<IFileTagsSettingsService>();
						var tag = tagService.GetTagsByName(command.Payload).FirstOrDefault();
						foreach (var file in command.Args.Skip(1))
						{
							var fileFRN = await FilesystemTasks.Wrap(() => StorageHelpers.ToStorageItem<IStorageItem>(file))
								.OnSuccess(item => FileTagsHelper.GetFileFRN(item));
							if (fileFRN is not null)
							{
								var tagUid = tag is not null ? new[] { tag.Uid } : [];
								var dbInstance = FileTagsHelper.GetDbInstance();
								dbInstance.SetTags(file, fileFRN, tagUid);
								await FileTagsHelper.WriteFileTagAsync(file, tagUid);
							}
						}
						break;

					case ParsedCommandType.Unknown:
						if (command.Payload.Equals("."))
						{
							await PerformNavigationAsync(activationPath);
						}
						else
						{
							if (!string.IsNullOrEmpty(command.Payload))
							{
								var target = IO.Path.GetFullPath(IO.Path.Combine(activationPath, command.Payload));
								await PerformNavigationAsync(target);
							}
							else
							{
								await PerformNavigationAsync(null);
							}
						}
						break;

					case ParsedCommandType.OutputPath:
						App.OutputPath = command.Payload;
						break;
				}
			}
			if (App.IsDialogMode && rootFrame.Content is not MainPage)
			{
				// OutputPath/mode alone still represent a complete picker request.
				// Without this, activation leaves an empty window and no shell context.
				await PerformNavigationAsync(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), App.DialogFileName);
			}
		}

		public bool SetCanWindowToFront(bool canWindowToFront)
		{
			lock (_canWindowToFrontLock)
			{
				if (CanWindowToFront != canWindowToFront)
				{
					CanWindowToFront = canWindowToFront;
					return true;
				}
				return false;
			}
		}

		protected override bool OnWindowMessageReceived(uint message, WPARAM wParam, LPARAM lParam, ref LRESULT result)
		{
			if ((!CanWindowToFront) && message == Windows.Win32.PInvoke.WM_WINDOWPOSCHANGING)
			{
				Win32Helper.ForceWindowPosition(lParam.Value);
				return true;
			}
			else if (message == Windows.Win32.PInvoke.WM_MENUCHAR && (wParam.Value & 0xFFFF) == '\r')
			{
				result = new(Win32PInvoke.MNC_CLOSE << 16);
				return true;
			}

			return false;
		}
	}
}
