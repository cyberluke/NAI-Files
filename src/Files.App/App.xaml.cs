// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Commands;
using Files.App.Helpers.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Windows.AppLifecycle;
using System.Runtime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Win32;
using WinRT;

namespace Files.App
{
	/// <summary>
	/// Represents the entry point of UI for Files app.
	/// </summary>
	public partial class App : Application
	{
		public static SystemTrayIcon? SystemTrayIcon { get; private set; }

		public static TaskCompletionSource? SplashScreenLoadingTCS { get; private set; }
		public static string? OutputPath { get; set; }

		/// <summary>External file-dialog activation mode: "open", "multi", "folder" or "save".</summary>
		public static string? DialogMode { get; set; }

		/// <summary>Suggested file name for save-dialog activations (from -select).</summary>
		public static string? DialogFileName { get; set; }

		/// <summary>True when this instance was launched as a native open/save dialog.</summary>
		public static bool IsDialogMode => DialogMode is not null;

		/// <summary>Set by the Open/Save action button before the window closes; the
		/// close handler only writes the result file after a confirmation, so
		/// Cancel and the X button cancel the dialog (the COM host treats a missing
		/// output file as ERROR_CANCELLED).</summary>
		public static bool DialogConfirmRequested;

		private static FlyoutBase? _LastOpenedFlyout;
		private static bool _isWindowTeardownCompleted;
		public static FlyoutBase? LastOpenedFlyout
		{
			set
			{
				_LastOpenedFlyout = value;

				if (_LastOpenedFlyout is not null)
					_LastOpenedFlyout.Closed += LastOpenedFlyout_Closed;
			}
		}

		// TODO: Replace with DI
		public static QuickAccessManager QuickAccessManager { get; private set; } = null!;
		public static StorageHistoryWrapper HistoryWrapper { get; private set; } = null!;
		public static FileTagsManager FileTagsManager { get; private set; } = null!;
		public static LibraryManager LibraryManager { get; private set; } = null!;
		public static AppModel AppModel { get; private set; } = null!;
		public static ILogger Logger { get; private set; } = NullLogger.Instance;

		public static Microsoft.UI.Dispatching.DispatcherQueue? UiDispatcher { get; private set; }

		/// <summary>
		/// Initializes an instance of <see cref="App"/>.
		/// </summary>
		public App()
		{
			NaiLanguageBridge.StartupStage("language-start");
			NaiLanguageBridge.Initialize();
			NaiLanguageBridge.StartupStage("language-ready");
			InitializeComponent();
			NaiLanguageBridge.StartupStage("resources-ready");

			// Configure exception handlers
			AppLifecycleHelper.RecordFirstChanceExceptions();
			UnhandledException += (sender, e) => AppLifecycleHelper.HandleAppUnhandledException(e.Exception, true, "Application.UnhandledException", e.Message);
			AppDomain.CurrentDomain.UnhandledException += (sender, e) => AppLifecycleHelper.HandleAppUnhandledException(e.ExceptionObject as Exception, false, "AppDomain.UnhandledException");
			TaskScheduler.UnobservedTaskException += (sender, e) => AppLifecycleHelper.HandleAppUnhandledException(e.Exception, false, "TaskScheduler.UnobservedTaskException");
			AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
				SafetyExtensions.IgnoreExceptions(() => Ioc.Default.GetService<FileLoggerProvider>()?.TryCompleteAndFlush(TimeSpan.FromSeconds(2)));
		}

		/// <summary>
		/// Gets invoked when the application is launched normally by the end user.
		/// </summary>
		protected override void OnLaunched(LaunchActivatedEventArgs e)
		{
			NaiLanguageBridge.StartupStage("on-launched");
			UiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

			// Constructed on the UI thread: the ctor subscribes the UI-thread-only Clipboard.ContentChanged
			AppModel = new AppModel();
			NaiLanguageBridge.StartupStage("app-model-ready");

			_ = ActivateAsync().ContinueWith(task =>
			{
				if (task.Exception is { } exception)
				{
					NaiLanguageBridge.StartupStage("activation-failed: " + exception);
					Logger?.LogError(exception, "Application activation failed");
				}
			}, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

			async Task ActivateAsync()
			{
				// Service/action constructors can reach MainWindow and WinUI resources.
				// Resolving them on a worker races MainWindow.Instance and can deadlock
				// the dispatcher against a DI singleton lock before the first window.
				// Configure the provider once and keep UI-affine resolution on this thread.
				var serviceProvider = AppLifecycleHelper.ConfigureHost(AppModel);
				Ioc.Default.ConfigureServices(serviceProvider);
				NaiLanguageBridge.StartupStage("services-configured");

				// TODO: Replace with DI
				QuickAccessManager = Ioc.Default.GetRequiredService<QuickAccessManager>();
				HistoryWrapper = Ioc.Default.GetRequiredService<StorageHistoryWrapper>();
				FileTagsManager = Ioc.Default.GetRequiredService<FileTagsManager>();
				LibraryManager = Ioc.Default.GetRequiredService<LibraryManager>();
				Logger = Ioc.Default.GetRequiredService<ILogger<App>>();
				AppModel = Ioc.Default.GetRequiredService<AppModel>();
				NaiLanguageBridge.StartupStage("managers-resolved");

				// Construct the command graph (200+ action classes + their DI singletons,
				// ~1.4 s normally, far more right after a fresh install while the OS is
				// still scanning the package payload) off the UI thread. Context ctors
				// already defer UI-thread-only work (WindowContext, MultitaskingContext)
				// when constructed off-thread, so the first MainPage frame is never
				// stalled by command creation. On failure the UI thread constructs the
				// graph the usual way, so this is a strict improvement.
				_ = Task.Run(() =>
				{
					try
					{
						_ = Ioc.Default.GetRequiredService<ICommandManager>();
						NaiLanguageBridge.StartupStage("command-prewarm-done");
					}
					catch (Exception ex)
					{
						var text = ex.ToString() + "\n--captured--\n" + Environment.StackTrace;
						NaiLanguageBridge.StartupStage("command-prewarm-failed: " + text[..Math.Min(text.Length, 1600)]);
					}
				});

				// Get AppActivationArguments
				var appActivationArguments = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
				NaiLanguageBridge.StartupStage("activation-args-ready");
				var isStartupTask = appActivationArguments.Data is Windows.ApplicationModel.Activation.IStartupTaskActivatedEventArgs;
				NaiLanguageBridge.StartupStage("startup-task=" + isStartupTask);

				// IsDynamicCodeSupported is false on Native AOT, where startup is fast enough to skip the splash screen
				var showSplashScreen = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;

				if (!isStartupTask)
				{
					// The main window is owned exclusively by the UI thread.
					NaiLanguageBridge.StartupStage("window-constructing");
					_ = MainWindow.Instance;
					NaiLanguageBridge.StartupStage("window-constructed");
				}

				var userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();
				var isLeaveAppRunning = userSettingsService.GeneralSettingsService.CloseToBackground;

				if (!isStartupTask || !isLeaveAppRunning)
				{
					if (showSplashScreen)
					{
						SplashScreenLoadingTCS = new TaskCompletionSource();
						MainWindow.Instance.ShowSplashScreen();
					}

					if (!await MainWindow.Instance.ActivateWithBackdropAsync())
						return;
					NaiLanguageBridge.StartupStage("window-activated");
				}

				// Configure Sentry
				if (AppLifecycleHelper.AppEnvironment is not AppEnvironment.Dev)
					AppLifecycleHelper.ConfigureSentry();

				// Hook events for the window
				MainWindow.Instance.Closed += Window_Closed;
				MainWindow.Instance.Activated += Window_Activated;

				Logger.LogInformation($"App launched. Launch args type: {appActivationArguments.Data.GetType().Name}");
				NaiLanguageBridge.StartupStage("app-launched-logged");

				if (!(isStartupTask && isLeaveAppRunning))
				{
					if (SplashScreenLoadingTCS is not null)
					{
						// Wait for the UI to update
						await SplashScreenLoadingTCS.Task.WithTimeoutAsync(TimeSpan.FromMilliseconds(500));
						SplashScreenLoadingTCS = null;
					}
					NaiLanguageBridge.StartupStage("splash-done");

					// Deferred so the first frame renders first
					MainWindow.Instance.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
					{
						SystemTrayIcon = new SystemTrayIcon();
						if (userSettingsService.GeneralSettingsService.ShowSystemTrayIcon)
							SystemTrayIcon.Show();
					});

					_ = MainWindow.Instance.InitializeApplicationAsync(appActivationArguments.Data);
					NaiLanguageBridge.StartupStage("initialize-application-started");
				}
				else
				{
					// Create a system tray icon
					SystemTrayIcon = new SystemTrayIcon();
					if (userSettingsService.GeneralSettingsService.ShowSystemTrayIcon)
						SystemTrayIcon.Show();

					// Sleep current instance
					var pool = new Semaphore(0, 1, $"Files-{AppLifecycleHelper.AppEnvironment}-Instance");
					Program.Pool = pool;

					var cts = new CancellationTokenSource();
					TryEmptyWorkingSetWhenIdle(cts.Token);

					await WaitOneAsync(pool);

					cts.Cancel();
					// Resume the instance; a rapid close-reopen-close may have already replaced the semaphore
					pool.Dispose();
					if (ReferenceEquals(Program.Pool, pool))
						Program.Pool = null;
				}

				await AppLifecycleHelper.InitializeAppComponentsAsync();
			}
		}

		/// <summary>
		/// Gets invoked when the application is activated.
		/// </summary>
		public async Task OnActivatedAsync(AppActivationArguments activatedEventArgs)
		{
			var activatedEventArgsData = activatedEventArgs.Data;

			Logger.LogInformation($"The app is being activated. Activation type: {activatedEventArgsData?.GetType().Name ?? "Unknown"}");

			// InitializeApplication accesses UI, needs to be called on UI thread
			await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(()
				=> MainWindow.Instance.InitializeApplicationAsync(activatedEventArgsData));
		}

		/// <summary>
		/// Gets invoked when the main window is activated.
		/// </summary>
		private void Window_Activated(object sender, WindowActivatedEventArgs args)
		{
			Logger.LogInformation($"Window_Activated: State={args.WindowActivationState}");

			ActiveSessionTracker.OnActivationChanged(args.WindowActivationState != WindowActivationState.Deactivated);

			if (args.WindowActivationState != WindowActivationState.Deactivated)
				AppModel.IsMainWindowClosed = false;

			if (args.WindowActivationState != WindowActivationState.CodeActivated &&
				args.WindowActivationState != WindowActivationState.PointerActivated)
				return;

			ApplicationData.Current.LocalSettings.Values["INSTANCE_ACTIVE"] = -Environment.ProcessId;

			// Reclaim the tray icon if a sibling instance's exit removed the shared-GUID icon
			SystemTrayIcon?.EnsureCreated();
		}

		private bool _isDialogTeardownStarted;

		/// <summary>
		/// Gets invoked when the application execution is closed.
		/// </summary>
		/// <remarks>
		/// Saves the current state of the app such as opened tabs, and disposes all cached resources.
		/// </remarks>
		private async void Window_Closed(object sender, WindowEventArgs args)
		{
			// Complete before flyout teardown or any await. A confirmed writer is already
			// sealed, so this can neither replace success nor wake the caller twice.
			if (IsDialogMode && !TryCompleteDialog(Array.Empty<string>(), out var error))
				Logger.LogError("Dialog cancellation delivery failed: {Error}", error);

			// Let the final close after background teardown proceed
			if (_isWindowTeardownCompleted)
				return;
			if (IsDialogMode)
			{
				// Keep XAML alive until its panes/watchers have been detached. Letting
				// Closed proceed first races background collection notifications with destruction.
				args.Handled = true;
				if (_isDialogTeardownStarted)
					return;
				_isDialogTeardownStarted = true;
			}

			// Stop dispatcher timers before the close handler yields and window teardown begins.
			AppModel.IsMainWindowClosed = true;

			// Save application state and stop any background activity
			IUserSettingsService userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();
			StatusCenterViewModel statusCenterViewModel = Ioc.Default.GetRequiredService<StatusCenterViewModel>();
			ICommandManager commandManager = Ioc.Default.GetRequiredService<ICommandManager>();

			// A Workaround for the crash (#10110)
			if (!IsDialogMode && (_LastOpenedFlyout?.IsOpen ?? false))
			{
				args.Handled = true;
				_LastOpenedFlyout.Closed += (sender, e) => App.Current.Exit();
				_LastOpenedFlyout.Hide();
				return;
			}

			// Persist the final active stretch; it is reported on the next launch
			ActiveSessionTracker.OnActivationChanged(false);

			// Save the current tab list in case it was overwriten by another instance
			if (!IsDialogMode)
			{
				if (userSettingsService.GeneralSettingsService.ContinueLastSessionOnStartUp || userSettingsService.AppSettingsService.RestoreTabsOnStartup)
					AppLifecycleHelper.SaveSessionTabs();
				else
					await commandManager.CloseAllTabs.ExecuteAsync();
			}

			// Dev, preview and stable all run as "Files"; only this channel's other instances block parking
			static bool IsSameChannelInstance(Process p)
			{
				if (p.Id == Environment.ProcessId)
					return false;

				try
				{
					return p.MainModule?.FileName.StartsWith(Package.Current.EffectivePath, StringComparison.OrdinalIgnoreCase) ?? false;
				}
				catch
				{
					// Access is denied reading another channel's MainModule
					return false;
				}
			}

			// Continue running the app on the background
			var needsFinalClose = IsDialogMode;
			if (IsDialogMode)
			{
				_LastOpenedFlyout?.Hide();
				UIHelpers.CloseAllDialogs();
				SafetyExtensions.IgnoreExceptions(() => Ioc.Default.GetRequiredService<InfoPaneViewModel>().UnloadPreview());
				MainPageViewModel.AppInstances.ForEach(tab => tab.Unload());
				MainPageViewModel.AppInstances.Clear();
			}
			else if (
				userSettingsService.GeneralSettingsService.CloseToBackground &&
				!AppModel.ForceProcessTermination &&
				!Process.GetProcessesByName("Files").Any(IsSameChannelInstance))
			{
				// Handled set after an await is read too late to cancel the close
				args.Handled = true;
				needsFinalClose = true;

				// Close open content dialogs
				UIHelpers.CloseAllDialogs();

				// Tear down the shell preview host (prevhost.exe) while the dispatcher still pumps; parking must not keep it attached
				SafetyExtensions.IgnoreExceptions(() => Ioc.Default.GetRequiredService<InfoPaneViewModel>().UnloadPreview());

				// Close all notification banners except in progress
				statusCenterViewModel.RemoveAllCompletedItems();

				// Cache the window instead of closing it
				MainWindow.Instance.AppWindow.Hide();

				// Close all tabs
				MainPageViewModel.AppInstances.ForEach(tabItem => tabItem.Unload());
				MainPageViewModel.AppInstances.Clear();

				// Wait for all properties windows to close
				await FilePropertiesHelpers.WaitClosingAll();

				// Claim INSTANCE_ACTIVE before parking; it may still name an already-exited sibling
				ApplicationData.Current.LocalSettings.Values["INSTANCE_ACTIVE"] = -Environment.ProcessId;

				// Sleep current instance
				var pool = new Semaphore(0, 1, $"Files-{AppLifecycleHelper.AppEnvironment}-Instance");
				Program.Pool = pool;

				// Displays a notification the first time the app goes to the background
				if (userSettingsService.AppSettingsService.ShowBackgroundRunningNotification)
				{
					SafetyExtensions.IgnoreExceptions(() =>
					{
						AppToastNotificationHelper.ShowBackgroundRunningToast();

						userSettingsService.AppSettingsService.ShowBackgroundRunningNotification = false;
					});
				}

				var cts = new CancellationTokenSource();
				TryEmptyWorkingSetWhenIdle(cts.Token);

				// Waiting must not block the dispatcher; WinRT wrapper finalizers stall until it pumps again
				await WaitOneAsync(pool);

				cts.Cancel();
				// Resume the instance; a rapid close-reopen-close may have already replaced the semaphore
				pool.Dispose();
				if (ReferenceEquals(Program.Pool, pool))
					Program.Pool = null;

				if (!AppModel.ForceProcessTermination)
				{
					_ = AppLifecycleHelper.CheckAppUpdate();
					return;
				}
			}

			// Stop the tray icon's hidden window before continuing teardown so a late "Quit"
			// click can't dispatch into OnQuitClicked once Application.Current is null.
			SystemTrayIcon?.Dispose();
			SystemTrayIcon = null;

			// Method can take a long time, make sure the window is hidden
			await Task.Yield();

			// Try to maintain clipboard data after app close
			SafetyExtensions.IgnoreExceptions(() =>
			{
				var dataPackage = Clipboard.GetContent();
				if (dataPackage.Properties.PackageFamilyName == Package.Current.Id.FamilyName)
				{
					if (dataPackage.Contains(StandardDataFormats.StorageItems))
						Clipboard.Flush();
				}
			},
			Logger);

			// Destroy cached properties windows
			FilePropertiesHelpers.DestroyCachedWindows();

			// Wait for ongoing file operations
			FileOperationsHelpers.WaitForCompletion();

			// Close the still-alive window for real now that teardown is done
			if (needsFinalClose && !_isWindowTeardownCompleted)
			{
				_isWindowTeardownCompleted = true;
				MainWindow.Instance.Close();
			}
		}

		private static async Task WaitOneAsync(WaitHandle handle)
		{
			var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var registration = ThreadPool.RegisterWaitForSingleObject(
				handle,
				static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
				tcs,
				Timeout.InfiniteTimeSpan,
				executeOnlyOnce: true);

			await tcs.Task;
			registration.Unregister(null);
		}

		private static void TryEmptyWorkingSetWhenIdle(CancellationToken cancellationToken)
		{
			static void AggressiveGC(Windows.Win32.Foundation.HANDLE processHandle, CancellationToken cancellationToken)
			{
				GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
				GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
				GC.WaitForPendingFinalizers();
				GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
				Thread.Sleep(1000);

				if (cancellationToken.IsCancellationRequested)
					return;

				PInvoke.K32EmptyWorkingSet(processHandle);
			}

			new Thread(() =>
			{
				using var process = Process.GetCurrentProcess();
				var processHandle = new Windows.Win32.Foundation.HANDLE(process.Handle);

				// Try to empty the working set
				AggressiveGC(processHandle, cancellationToken);

				if (cancellationToken.IsCancellationRequested)
					return;

				FileOperationsHelpers.WaitForCompletion();
				if (cancellationToken.IsCancellationRequested)
					return;

				// After all pending file operations are completed, try to empty the working set again
				AggressiveGC(processHandle, cancellationToken);
			})
			{ IsBackground = true }.Start();
		}

		/// <summary>
		/// Gets invoked when the last opened flyout is closed.
		/// </summary>
		[DynamicWindowsRuntimeCast(typeof(FlyoutBase))]
		private static void LastOpenedFlyout_Closed(object? sender, object e)
		{
			if (sender is not FlyoutBase flyoutBase)
				return;

			flyoutBase.Closed -= LastOpenedFlyout_Closed;
			if (_LastOpenedFlyout == flyoutBase)
				_LastOpenedFlyout = null;
		}

		private static DialogResultWriter? dialogResultWriter;
		internal static bool DialogTraceRequested { get; set; }
		internal static string? DialogRequestError { get; set; }
		internal static bool DialogTraceForWriter { get; set; }

		internal static bool TryCompleteDialog(IReadOnlyList<string> paths, out string error)
		{
			if (string.IsNullOrEmpty(OutputPath))
			{
				error = Strings.InvalidPath.GetLocalizedResource();
				return false;
			}
			if (dialogResultWriter?.OutputPath != OutputPath)
				dialogResultWriter = new DialogResultWriter(OutputPath);
			var session = DialogSession.Current;
			if (!dialogResultWriter.TryComplete(paths, out error, session?.SelectedTypeIndex ?? 1, session is null ? null : session.SaveCurrent))
				return false;
			DialogConfirmRequested = dialogResultWriter.IsConfirmed;
			return true;
		}
	}
}
