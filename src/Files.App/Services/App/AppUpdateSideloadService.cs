// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Windows.Storage;
using Windows.Win32;
using Windows.Win32.System.Recovery;

namespace Files.App.Services
{
	public sealed partial class SideloadUpdateService : ObservableObject, IUpdateService, IDisposable
	{
		// NAI Files is distributed from the Cloudflare R2 feed and updates through
		// Windows App Installer.
		private readonly HttpClient _client = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(3) });

		private ILogger? Logger { get; } = Ioc.Default.GetRequiredService<ILogger<App>>();

		private Version PackageVersion { get; } = new(
			Package.Current.Id.Version.Major,
			Package.Current.Id.Version.Minor,
			Package.Current.Id.Version.Build,
			Package.Current.Id.Version.Revision);

		private bool _isUpdateAvailable;
		public bool IsUpdateAvailable
		{
			get => _isUpdateAvailable;
			private set => SetProperty(ref _isUpdateAvailable, value);
		}

		private bool _isUpdating;
		public bool IsUpdating
		{
			get => _isUpdating;
			private set => SetProperty(ref _isUpdating, value);
		}

		private int _updateProgress;
		public int UpdateProgress
		{
			get => _updateProgress;
			private set => SetProperty(ref _updateProgress, value);
		}

		public bool IsAppUpdated
		{
			get => AppLifecycleHelper.IsAppUpdated;
		}

		private bool _areReleaseNotesAvailable = false;
		public bool AreReleaseNotesAvailable
		{
			get => _areReleaseNotesAvailable;
			private set => SetProperty(ref _areReleaseNotesAvailable, value);
		}

		public async Task DownloadUpdatesAsync()
		{
			await ApplyPackageUpdateAsync();
		}

		public Task DownloadMandatoryUpdatesAsync()
		{
			return Task.CompletedTask;
		}

		public async Task CheckForUpdatesAsync()
		{
			try
			{
				using var response = await _client.GetAsync(Constants.ExternalUrl.UpdateFeedUrl);
				response.EnsureSuccessStatusCode();

				await using var stream = await response.Content.ReadAsStreamAsync();
				var document = await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None);
				if (!Version.TryParse((string?)document.Root?.Attribute("Version"), out var availableVersion))
				{
					IsUpdateAvailable = false;
					return;
				}

				var updateAvailable = availableVersion > PackageVersion;
				MainWindow.Instance.DispatcherQueue.TryEnqueue(() => IsUpdateAvailable = updateAvailable);
			}
			catch (Exception ex)
			{
				Logger?.LogWarning(ex, "Unable to check the NAI Files update feed.");
				IsUpdateAvailable = false;
			}
		}
		public async Task CheckAndUpdateFilesLauncherAsync()
		{
			try
			{
				var destFolderPath = Path.Combine(UserDataPaths.GetDefault().LocalAppData, "Files");
				var destExeFilePath = Path.Combine(destFolderPath, "Files.App.Launcher.exe");
				var branchFilePath = Path.Combine(destFolderPath, "Branch.txt");

				// If Files.App.Launcher.exe doesn't exist, no need to update it.
				if (!File.Exists(destExeFilePath))
					return;

				// Check if the launcher file is associated with the current branch of the app.
				if (File.Exists(branchFilePath))
				{
					try
					{
						var branch = await File.ReadAllTextAsync(branchFilePath, Encoding.UTF8);
						if (!string.Equals(branch.Trim(), "files-dev", StringComparison.OrdinalIgnoreCase))
							return;
					}
					catch { }
				}
				else
				{
					try
					{
						// Create branch file for users updating from versions earlier than v4.0.20.
						await File.WriteAllTextAsync(branchFilePath, "files-dev", Encoding.UTF8);
					}
					catch { }
				}

				var srcExeFile = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Assets/FilesOpenDialog/Files.App.Launcher.exe"));
				var destFolder = await StorageFolder.GetFolderFromPathAsync(destFolderPath);

				await srcExeFile.CopyAsync(destFolder, "Files.App.Launcher.exe", NameCollisionOption.ReplaceExisting);

				App.Logger.LogInformation("Files.App.Launcher updated.");
			}
			catch (Exception ex)
			{
				Logger?.LogError(ex, ex.Message);
				return;
			}
		}

		public Task CheckForReleaseNotesAsync()
		{
			// Release notes will be served from the NAI Files download domain once it is published.
			AreReleaseNotesAvailable = false;
			return Task.CompletedTask;
		}
		private async Task ApplyPackageUpdateAsync()
		{
			if (!IsUpdateAvailable)
				return;

			IsUpdating = true;
			try
			{
				AppLifecycleHelper.SaveSessionTabs();
				PInvoke.RegisterApplicationRestart(null, (REGISTER_APPLICATION_RESTART_FLAGS)0);

				var packageManager = new PackageManager();
				await Task.Run(async () =>
				{
					var deployment = packageManager.AddPackageByAppInstallerFileAsync(
						new Uri(Constants.ExternalUrl.UpdateFeedUrl),
						AddPackageByAppInstallerOptions.ForceTargetAppShutdown,
						packageManager.GetDefaultPackageVolume());

					deployment.Progress = (_, progress) =>
					{
						MainWindow.Instance.DispatcherQueue.TryEnqueue(() => UpdateProgress = (int)progress.percentage);
					};

					var result = await deployment;
					if (result.ExtendedErrorCode is not null)
						Logger?.LogError($"Update failed: {result.ErrorText} ({result.ExtendedErrorCode})");
				});
			}
			catch (Exception ex)
			{
				Logger?.LogError(ex, "Unable to install the NAI Files update.");
			}
			finally
			{
				IsUpdating = false;
				IsUpdateAvailable = false;
				UpdateProgress = 0;
			}
		}
		public void Dispose()
		{
			_client?.Dispose();
		}
	}
}
