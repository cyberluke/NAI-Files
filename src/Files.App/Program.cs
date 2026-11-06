// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Shared.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Settings;
using Microsoft.Windows.AppLifecycle;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32.UI.WindowsAndMessaging;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

namespace Files.App
{
	/// <summary>
	/// Represents the base entry point of the Files app.
	/// </summary>
	/// <remarks>
	/// Gets called at the first time when the app launched or activated.
	/// </remarks>
	internal sealed class Program
	{
		public static Semaphore? Pool { get; set; }

		private const string LaunchCwdKey = "LastLaunchCwd";

		/// <summary>
		/// Reads and clears the working directory captured by the source process
		/// in <see cref="Main"/>, falling back to the current process's working
		/// directory if no value is pending. (#16982)
		/// </summary>
		public static string ConsumeLaunchCwd()
		{
			var values = ApplicationData.Current.LocalSettings.Values;
			var cwd = values.TryGetValue(LaunchCwdKey, out var raw) ? raw as string : null;
			if (cwd is not null)
				values.Remove(LaunchCwdKey);
			return string.IsNullOrEmpty(cwd) ? Environment.CurrentDirectory : cwd;
		}

		private static void ResumeCachedInstance(AppActivationArguments activation)
		{
			var pool = new Semaphore(0, 1, $"Files-{AppLifecycleHelper.AppEnvironment}-Instance", out var isNew);

			if (!isNew)
			{
				var activePid = ApplicationData.Current.LocalSettings.Values.Get("INSTANCE_ACTIVE", -1);
				var instance = AppInstance.FindOrRegisterForKey(activePid.ToString());

				// A stale key resolves to this process itself; redirecting to self would drop the activation
				if (!instance.IsCurrent)
				{
					// Resume cached instance
					try
					{
						pool.Release();
					}
					catch (SemaphoreFullException)
					{
						// Another launch already queued a wake-up for the cached instance
					}

					// Redirect to the main process
					RedirectActivationTo(instance, activation);

					// Kill the current process
					Environment.Exit(0);
				}
			}

			pool.Dispose();
		}

		/// <summary>
		/// Initializes the process; the entry point of the process.
		/// </summary>
		/// <remarks>
		/// <see cref="Main"/> cannot be declared to be async because this prevents Narrator from reading XAML elements in a WinUI app.
		/// </remarks>
		[STAThread]
		private static void Main()
		{
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
			WinRT.ComWrappersSupport.InitializeComWrappers();

			// Now we can do the first WinRT server call
			Server.AppInstanceMonitor.StartMonitor(Environment.ProcessId);

			var OpenTabInExistingInstance = ApplicationData.Current.LocalSettings.Values.Get("OpenTabInExistingInstance", true);

			AppActivationArguments activatedArgs;
			try
			{
				activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
			}
			catch (COMException ex) when (ex.HResult == unchecked((int)0x80040154))
			{
				Windows.Win32.PInvoke.MessageBox(
					default,
					Constants.Startup.MissingRuntimeMessage,
					Constants.Startup.MissingRuntimeTitle,
					MESSAGEBOX_STYLE.MB_ICONERROR);

				throw new InvalidOperationException(Constants.Startup.MissingRuntimeMessage, ex);
			}

			var commandLineArgs = GetCommandLineArgs(activatedArgs);
			ApplicationData.Current.LocalSettings.Values[LaunchCwdKey] = Environment.CurrentDirectory;
			var isDialog = commandLineArgs is not null && CommandLineParser.ParseUntrustedCommands(commandLineArgs)
				.Any(command => command.Type == ParsedCommandType.OutputPath);
DialogSession.IsDialogLaunch = isDialog;

			// Self-heal the native Open/Save COM registration so a bare sideload
			// (NAI OS not running) and package updates keep re-pointing the per-user
			// CLSID shadows at this package's dialog servers. Never blocks startup.
			DialogRegistrationSelfHeal.EnsureRegistration();

			if (!isDialog)
				ResumeCachedInstance(activatedArgs);

			if (commandLineArgs is not null)
			{
				var parsedCommands = CommandLineParser.ParseUntrustedCommands(commandLineArgs);

				if (parsedCommands is not null)
				{
					foreach (var command in parsedCommands)
					{
						switch (command.Type)
						{
							case ParsedCommandType.ExplorerShellCommand:
								if (!ShellHelpers.IsSupportedShellPath(command.Payload))
								{
									OpenShellCommandInExplorer(command.Payload, Environment.ProcessId);
									return;
								}
								break;

							default:
								break;
						}
					}
				}

				// Always open a new instance for OpenDialog, never open new instance for "-Tag" command
				if (parsedCommands is null || !parsedCommands.Any(x => x.Type == ParsedCommandType.OutputPath) &&
					(OpenTabInExistingInstance || parsedCommands.Any(x => x.Type == ParsedCommandType.TagFiles)))
				{
					var activePid = ApplicationData.Current.LocalSettings.Values.Get("INSTANCE_ACTIVE", -1);
					var instance = AppInstance.FindOrRegisterForKey(activePid.ToString());

					if (!instance.IsCurrent)
					{
						RedirectActivationTo(instance, activatedArgs);
						return;
					}
				}
			}
			else if (activatedArgs.Data is ILaunchActivatedEventArgs tileArgs)
			{
				if (tileArgs.Arguments is not null &&
					FileExtensionHelpers.IsExecutableFile(tileArgs.Arguments))
				{
					if (File.Exists(tileArgs.Arguments))
					{
						OpenFileFromTile(tileArgs.Arguments);
						return;
					}
				}
			}

			if (OpenTabInExistingInstance && commandLineArgs is null)
			{
				if (activatedArgs.Data is ILaunchActivatedEventArgs launchArgs)
				{
					var activePid = ApplicationData.Current.LocalSettings.Values.Get("INSTANCE_ACTIVE", -1);
					var instance = AppInstance.FindOrRegisterForKey(activePid.ToString());
					if (!instance.IsCurrent && !string.IsNullOrWhiteSpace(launchArgs.Arguments))
					{
						RedirectActivationTo(instance, activatedArgs);
						return;
					}
				}
				else if (activatedArgs.Data is IProtocolActivatedEventArgs protocolArgs)
				{
					var parsedArgs = protocolArgs.Uri.Query.TrimStart('?').Split('=');
					if (parsedArgs.Length == 1)
					{
						var activePid = ApplicationData.Current.LocalSettings.Values.Get("INSTANCE_ACTIVE", -1);
						var instance = AppInstance.FindOrRegisterForKey(activePid.ToString());
						if (!instance.IsCurrent)
						{
							RedirectActivationTo(instance, activatedArgs);
							return;
						}
					}
				}
				else if (activatedArgs.Data is IFileActivatedEventArgs)
				{
					var activePid = ApplicationData.Current.LocalSettings.Values.Get("INSTANCE_ACTIVE", -1);
					var instance = AppInstance.FindOrRegisterForKey(activePid.ToString());
					if (!instance.IsCurrent)
					{
						RedirectActivationTo(instance, activatedArgs);
						return;
					}
				}
			}

			var currentInstance = AppInstance.FindOrRegisterForKey((-Environment.ProcessId).ToString());
			if (currentInstance.IsCurrent)
				currentInstance.Activated += OnActivated;

			ApplicationData.Current.LocalSettings.Values["INSTANCE_ACTIVE"] = -Environment.ProcessId;

			// Optional XAML performance changes must be opted into before XAML initialization
			XamlOptionalChanges.EnableChange(XamlChangeId.DefaultStyleOptimizations);
			XamlOptionalChanges.EnableChange(XamlChangeId.OptimizeApplyStyles);
			XamlOptionalChanges.EnableChange(XamlChangeId.IconNoGridOptimization);
			XamlOptionalChanges.EnableChange(XamlChangeId.DeferContextFlyoutInit);

			Application.Start((p) =>
			{
				// NAI: on .NET 11 the runtime eagerly initializes the WinRT reference-tracker
				// callbacks (dotnet/runtime#121538, fixed by #121558), so the built-in sync
				// context no longer deadlocks during GC callouts.
				var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
				SynchronizationContext.SetSynchronizationContext(context);

				_ = new App();
			});
		}

		/// <summary>
		/// Gets command line args from AppActivationArguments
		/// Command line args can be ILaunchActivatedEventArgs, ICommandLineActivatedEventArgs or IProtocolActivatedEventArgs
		/// </summary>
		private static string? GetCommandLineArgs(AppActivationArguments activatedArgs)
		{
			// WINUI3: When launching from commandline the argument is not ICommandLineActivatedEventArgs (#10370)
			var cmdLaunchArgs = activatedArgs.Data is ILaunchActivatedEventArgs launchArgs &&
				launchArgs.Arguments is not null &&
				CommandLineParser.SplitArguments(launchArgs.Arguments, true).FirstOrDefault() is string arg0 &&
				(arg0.EndsWith($"files-dev.exe", StringComparison.OrdinalIgnoreCase) ||
				arg0.EndsWith($"files-dev", StringComparison.OrdinalIgnoreCase)) ? launchArgs.Arguments : null;
			var cmdProtocolArgs = activatedArgs.Data is IProtocolActivatedEventArgs protocolArgs &&
				protocolArgs.Uri.Query.TrimStart('?').Split('=') is string[] parsedArgs &&
				parsedArgs.Length == 2 && parsedArgs[0] == "cmd" ? DecodeDialogCommand(parsedArgs[1]) : null;
			var cmdLineArgs = activatedArgs.Data is ICommandLineActivatedEventArgs cmdArgs ? cmdArgs.Operation.Arguments : null;

			return cmdLaunchArgs ?? cmdProtocolArgs ?? cmdLineArgs;
		}

		/// <summary>
		/// The Files dialog COM servers hand the command line over the
		/// <c>files-dev:</c> protocol as UTF-8 bytes hex-encoded into
		/// [0-9A-F] — percent-decoding alone leaves a single opaque token that
		/// the command-line parser cannot understand, so decode the hex first.
		/// Plain (non-hex) payloads fall back to the percent-decoded text.
		/// </summary>
		internal static string DecodeDialogCommand(string payload)
		{
			var unescaped = Uri.UnescapeDataString(payload);
			if (unescaped.Length % 2 == 0 && unescaped.Length >= 8 &&
				unescaped.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
			{
				try
				{
					return System.Text.Encoding.UTF8.GetString(Convert.FromHexString(unescaped));
				}
				catch (FormatException)
				{
					// Not a valid hex payload after all: keep the decoded text.
				}
			}
			return unescaped;
		}

		/// <summary>
		/// Gets invoked when the application is activated.
		/// </summary>
		private static async void OnActivated(object? sender, AppActivationArguments args)
		{
			// WINUI3: Verify if needed or OnLaunched is called
			if (App.Current is App thisApp)
				await thisApp.OnActivatedAsync(args);
		}

		/// <summary>
		/// Redirects the activation to the main process.
		/// </summary>
		/// <remarks>
		/// Redirects on another thread and uses a non-blocking wait method to wait for the redirection to complete.
		/// </remarks>
		public static void RedirectActivationTo(AppInstance keyInstance, AppActivationArguments args)
		{
			keyInstance.RedirectActivationToAsync(args).AsTask().Wait();
		}

		public static void OpenShellCommandInExplorer(string shellCommand, int pid)
		{
			Win32Helper.OpenFolderInExistingShellWindow(shellCommand);
		}

		public static void OpenFileFromTile(string filePath)
		{
			LaunchHelper.LaunchAppAsync(filePath, null, null).Wait();
		}
	}
}
