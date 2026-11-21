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
		// The NAI_Files fork is distributed from GitHub releases; the upstream CDN
		// self-update feed is not used.
		private readonly HttpClient _client = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(3) });

		private ILogger? Logger { get; } = Ioc.Default.GetRequiredService<ILogger<App>>();

		private string PackageName { get; } = Package.Current.Id.Name;

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

		public Task CheckForUpdatesAsync()
		{
			// Upstream CDN self-update is disabled for NAI Files. Updates are installed
			// manually from the NAI Files download domain once it is published.
			IsUpdateAvailable = false;
			return Task.CompletedTask;
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
			// Release notes live on the fork's GitHub releases page; no upstream feed probe.
			AreReleaseNotesAvailable = false;
			return Task.CompletedTask;
		}
		private Task ApplyPackageUpdateAsync()
		{
			// Sideload self-update removed for the NAI_Files fork.
			return Task.CompletedTask;
		}
		public void Dispose()
		{
			_client?.Dispose();
		}
	}
}
