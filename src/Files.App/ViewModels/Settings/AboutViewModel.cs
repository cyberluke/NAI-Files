// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Win32;
using System.ComponentModel;
using System.Windows.Input;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace Files.App.ViewModels.Settings
{
	/// <summary>
	/// Represents view model of <see cref="Views.Settings.AboutPage"/>.
	/// </summary>
	public sealed partial class AboutViewModel : ObservableObject
	{
		// Dependency injections

		private IGeneralSettingsService GeneralSettingsService { get; } = Ioc.Default.GetRequiredService<IGeneralSettingsService>();
		private IUpdateService UpdateService { get; } = Ioc.Default.GetRequiredService<IUpdateService>();


		// Properties

		public string Version
			=> string.Format($"{Strings.SettingsAboutVersionTitle.GetLocalizedResource()} {AppVersion.Major}.{AppVersion.Minor}.{AppVersion.Build}.{AppVersion.Revision}");

		public string AppName
			=> Package.Current.DisplayName;

		public PackageVersion AppVersion
			=> Package.Current.Id.Version;

		public ObservableCollection<OpenSourceLibraryItem> OpenSourceLibraries { get; }

		public bool IsUpdateAvailable => UpdateService.IsUpdateAvailable;

		public bool IsUpdating => UpdateService.IsUpdating;

		public string UpdateHeader
			=> IsUpdateAvailable ? Strings.UpdateNow.GetLocalizedResource() : Strings.CheckForUpdates.GetLocalizedResource();

		public string UpdateStatus
		{
			get
			{
				if (IsUpdating)
					return string.Format("{0} {1}%", Strings.Updating.GetLocalizedResource(), UpdateService.UpdateProgress);

				return IsUpdateAvailable
					? Strings.UpdateAvailable.GetLocalizedResource()
					: Strings.UpToDate.GetLocalizedResource();
			}
		}

		// Commands

		public ICommand CopyAppVersionCommand { get; }
		public ICommand CopyWindowsVersionCommand { get; }
		public ICommand CopyUserIDCommand { get; }
		public ICommand UpdateCommand { get; }
		// Sponsor link removed (NAI_Files fork).
		public ICommand OpenLogLocationCommand { get; }
		public ICommand OpenDocumentationCommand { get; }
		public ICommand SubmitFeatureRequestCommand { get; }
		public ICommand SubmitBugReportCommand { get; }
		public ICommand OpenGitHubRepoCommand { get; }
		// Privacy link removed (NAI_Files fork).

		// Constructor

		/// <summary>
		/// Initializes an instance of <see cref="AboutViewModel"/> class.
		/// </summary>
		public AboutViewModel()
		{
			OpenSourceLibraries =
			[
				new ("https://github.com/omar/ByteSize", "ByteSize"),
				new ("https://github.com/CommunityToolkit/dotnet", "CommunityToolkit.Mvvm"),
				new ("https://github.com/DiscUtils/DiscUtils", "DiscUtils.Udf"),
				new ("https://github.com/robinrodricks/FluentFTP", "FluentFTP"),
				new ("https://github.com/libgit2/libgit2sharp", "libgit2sharp"),
				new ("https://github.com/jeffijoe/messageformat.net", "MessageFormat"),
				new ("https://github.com/dotnet/efcore", "EF Core for SQLite"),
				new ("https://github.com/dotnet/runtime", "Microsoft.Extensions"),
				new ("https://github.com/files-community/SevenZipSharp", "SevenZipSharp"),
				new ("https://sourceforge.net/projects/sevenzip", "7zip"),
				new ("https://github.com/ericsink/SQLitePCL.raw", "PCL for SQLite"),
				new ("https://github.com/microsoft/WindowsAppSDK", "WindowsAppSDK"),
				new ("https://github.com/microsoft/microsoft-ui-xaml", "WinUI 3"),
				new ("https://github.com/microsoft/Win2D", "Win2D"),
				new ("https://github.com/CommunityToolkit/Windows", "Windows Community Toolkit"),
				new ("https://github.com/mono/taglib-sharp", "TagLibSharp"),
				new ("https://github.com/microsoft/CsWin32", "CsWin32"),
				new ("https://github.com/microsoft/CsWinRT", "CsWinRT"),
				new ("https://github.com/GihanSoft/NaturalStringComparer", "NaturalStringComparer"),
				new ("https://github.com/leeqwind/PESignAnalyzer", "PESignAnalyzer"),
			];

			CopyAppVersionCommand = new RelayCommand(CopyAppVersion);
			CopyWindowsVersionCommand = new RelayCommand(CopyWindowsVersion);
			CopyUserIDCommand = new RelayCommand(CopyUserID);
			UpdateCommand = new AsyncRelayCommand(UpdateAsync);
			UpdateService.PropertyChanged += UpdateService_PropertyChanged;
			// SupportUsCommand removed (NAI_Files fork).
			OpenDocumentationCommand = new AsyncRelayCommand(DoOpenDocumentation);
			SubmitFeatureRequestCommand = new AsyncRelayCommand(DoSubmitFeatureRequest);
			SubmitBugReportCommand = new AsyncRelayCommand(DoSubmitBugReport);
			OpenGitHubRepoCommand = new AsyncRelayCommand(DoOpenGitHubRepo);
			// OpenPrivacyPolicyCommand removed (NAI_Files fork).
			OpenLogLocationCommand = new AsyncRelayCommand(OpenLogLocation);
		}

		// Methods

		private async Task UpdateAsync()
		{
			if (UpdateService.IsUpdating)
				return;

			if (UpdateService.IsUpdateAvailable)
				await UpdateService.DownloadUpdatesAsync();
			else
				await UpdateService.CheckForUpdatesAsync();
		}

		private void UpdateService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			OnPropertyChanged(nameof(IsUpdateAvailable));
			OnPropertyChanged(nameof(IsUpdating));
			OnPropertyChanged(nameof(UpdateHeader));
			OnPropertyChanged(nameof(UpdateStatus));
		}

		private async Task<bool> OpenLogLocation()
		{
			await Launcher.LaunchFolderAsync(ApplicationData.Current.LocalFolder).AsTask();

			// TODO: Move this to an application service
			// Detect if Files is set as the default file manager
			using var subkey = Registry.ClassesRoot.OpenSubKey(@"Folder\shell\open\command");
			var command = (string?)subkey?.GetValue(string.Empty);

			// Close the settings dialog if Files is the default file manager
			if (!string.IsNullOrEmpty(command) && command.Contains("Files.App.Launcher.exe"))
				UIHelpers.CloseAllDialogs();

			return true;
		}

		public Task DoOpenDocumentation()
		{
			return Launcher.LaunchUriAsync(new Uri(Constants.ExternalUrl.DocumentationUrl)).AsTask();
		}

		public Task DoSubmitFeatureRequest()
		{
			return Launcher.LaunchUriAsync(new Uri(Constants.ExternalUrl.FeatureRequestUrl)).AsTask();
		}

		public Task DoSubmitBugReport()
		{
			return Launcher.LaunchUriAsync(new Uri($"{Constants.ExternalUrl.BugReportUrl}&{GetVersionsQueryString()}")).AsTask();
		}

		public Task DoOpenGitHubRepo()
		{
			return Launcher.LaunchUriAsync(new Uri(Constants.ExternalUrl.GitHubRepoUrl)).AsTask();
		}

		// Privacy link removed (NAI_Files fork).

		public void CopyAppVersion()
		{
			SafetyExtensions.IgnoreExceptions(() =>
			{
				DataPackage dataPackage = new DataPackage();
				dataPackage.RequestedOperation = DataPackageOperation.Copy;
				dataPackage.SetText(GetAppVersion());
				Clipboard.SetContent(dataPackage);
			});
		}

		public void CopyWindowsVersion()
		{
			SafetyExtensions.IgnoreExceptions(() =>
			{
				DataPackage dataPackage = new DataPackage();
				dataPackage.RequestedOperation = DataPackageOperation.Copy;
				dataPackage.SetText(GetWindowsVersion());
				Clipboard.SetContent(dataPackage);
			});
		}

		public void CopyUserID()
		{
			SafetyExtensions.IgnoreExceptions(() =>
			{
				DataPackage dataPackage = new DataPackage();
				dataPackage.RequestedOperation = DataPackageOperation.Copy;
				dataPackage.SetText(GetUserID());
				Clipboard.SetContent(dataPackage);
			});
		}

		// Sponsor link removed (NAI_Files fork).

		public string GetAppVersion()
		{
			return $"{AppVersion.Major}.{AppVersion.Minor}.{AppVersion.Build}.{AppVersion.Revision}";
		}

		public string GetWindowsVersion()
		{
			ulong v = ulong.Parse(Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamilyVersion);
			return $"{(v >> 48) & 0xFFFF}.{(v >> 32) & 0xFFFF}.{(v >> 16) & 0xFFFF}.{v & 0xFFFF}";
		}

		public string GetUserID()
		{
			return GeneralSettingsService.UserId;
		}

		public string GetVersionsQueryString()
		{
			var query = System.Web.HttpUtility.ParseQueryString(string.Empty);
			query["files_version"] = GetAppVersion();
			query["windows_version"] = GetWindowsVersion();
			return query.ToString() ?? string.Empty;
		}
	}
}
