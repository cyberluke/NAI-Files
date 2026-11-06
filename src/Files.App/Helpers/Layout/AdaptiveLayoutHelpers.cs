// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.ViewModels.Previews;
using Files.Shared.Helpers;

namespace Files.App.Helpers
{
	public static class AdaptiveLayoutHelpers
	{
		private static ILayoutSettingsService LayoutSettingsService { get; } = Ioc.Default.GetRequiredService<ILayoutSettingsService>();
		private static IContentPageContext ContentPageContext { get; } = Ioc.Default.GetRequiredService<IContentPageContext>();
		private static IWindowsIniService WindowsIniService { get; } = Ioc.Default.GetRequiredService<IWindowsIniService>();

		// Folders whose contents are a stream of recent work rather than a hierarchy: Pulse is their natural view
		private static readonly Lazy<string[]> PulseFolders = new(() =>
		[
			Constants.UserEnvironmentPaths.DownloadsPath,
			Windows.Storage.UserDataPaths.GetDefault().Pictures,
			Windows.Storage.UserDataPaths.GetDefault().Videos,
		]);

		/// <summary>True for the Downloads, Pictures and Videos folders (not their subfolders).</summary>
		public static bool IsPulseFolder(string? path)
		{
			if (string.IsNullOrEmpty(path))
				return false;

			var normalized = path.TrimEnd('\\', '/');
			foreach (var folder in PulseFolders.Value)
			{
				if (!string.IsNullOrEmpty(folder) && string.Equals(normalized, folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
					return true;
			}

			return false;
		}

		// Predicts the layout from desktop.ini or a small directory sample, so navigation opens in the right layout without a post-enumeration switch
		public static bool TryPredictLayout(string? path, out FolderLayoutModes layout)
		{
			layout = FolderLayoutModes.DetailsView;

			if (path is null)
				return false;

			if (IsPulseFolder(path))
			{
				layout = FolderLayoutModes.PulseView;
				return true;
			}

			try
			{
				var root = SystemIO.Path.GetPathRoot(path);
				if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) ||
					new SystemIO.DriveInfo(root).DriveType is SystemIO.DriveType.Network or SystemIO.DriveType.NoRootDirectory)
					return false;

				var viewStateSection = WindowsIniService.GetData(SystemIO.Path.Combine(path, "desktop.ini"))
					.FirstOrDefault(x => x.SectionName == "ViewState");
				if (viewStateSection is not null)
				{
					var viewMode = viewStateSection.Parameters.FirstOrDefault(x => x.Key == "Mode").Value;
					layout = viewMode is "Pictures" or "Videos" ? FolderLayoutModes.PulseView : FolderLayoutModes.DetailsView;
					return true;
				}

				int total = 0, media = 0;
				foreach (var entry in SystemIO.Directory.EnumerateFileSystemEntries(path))
				{
					if (IsMediaExtension(SystemIO.Path.GetExtension(entry)))
						media++;
					if (++total >= 200)
						break;
				}

				if (total is 0)
					return false;

				layout = 100f * media / total > 60f ? FolderLayoutModes.PulseView : FolderLayoutModes.DetailsView;
				return true;
			}
			catch (Exception ex) when (ex is SystemIO.IOException or UnauthorizedAccessException or ArgumentException)
			{
				return false;
			}
		}

		public static void ApplyAdaptativeLayout(LayoutPreferencesManager folderSettings, IList<ListedItem> filesAndFolders)
		{
			if (LayoutSettingsService.SyncFolderPreferencesAcrossDirectories)
				return;
			if (folderSettings.IsLayoutModeFixed || !folderSettings.IsAdaptiveLayoutEnabled)
				return;

			switch (GetAdaptiveLayout(filesAndFolders))
			{
				case Layouts.Detail when folderSettings.LayoutMode is not FolderLayoutModes.DetailsView:
					folderSettings.ToggleLayoutModeDetailsView(false);
					break;
				case Layouts.Pulse when folderSettings.LayoutMode is not FolderLayoutModes.PulseView:
					folderSettings.ToggleLayoutModePulseView(false);
					break;
			}
		}

		private static Layouts GetAdaptiveLayout(IList<ListedItem> filesAndFolders)
		{
			if (IsPulseFolder(ContentPageContext.ShellPage?.ShellViewModel?.WorkingDirectory))
				return Layouts.Pulse;

			var pathLayout = GetPathLayout();
			if (pathLayout is not Layouts.None)
				return pathLayout;

			return GetContentLayout(filesAndFolders);
		}

		private static Layouts GetPathLayout()
		{
			var desktopIni = ContentPageContext.ShellPage?.ShellViewModel?.DesktopIni;
			if (desktopIni is null)
				return Layouts.None;

			var viewStateSection = desktopIni.FirstOrDefault(x => x.SectionName == "ViewState");
			if (viewStateSection is null)
				return Layouts.None;

			var viewMode = viewStateSection.Parameters.FirstOrDefault(x => x.Key == "Mode").Value;

			return viewMode switch
			{
				"Pictures" => Layouts.Pulse,
				"Videos" => Layouts.Pulse,
				_ => Layouts.Detail,
			};
		}

		private static Layouts GetContentLayout(IList<ListedItem> filesAndFolders)
		{
			int itemCount = filesAndFolders.Count;
			if (filesAndFolders.Count is 0)
				return Layouts.None;

			float mediaPercentage = 100f * filesAndFolders.Count(IsMedia) / itemCount;

			if (mediaPercentage > 60f)
				return Layouts.Pulse;
			return Layouts.Detail;

			static bool IsMedia(ListedItem item)
				=> IsMediaExtension(item.FileExtension);
		}

		private static bool IsMediaExtension(string? extension)
			=> !string.IsNullOrEmpty(extension)
			&& (FileExtensionHelpers.IsAudioFile(extension)
			|| FileExtensionHelpers.IsVideoFile(extension)
			|| FileExtensionHelpers.IsImageFile(extension));

		private enum Layouts
		{
			None, // Don't decide. Another function to decide can be called afterwards if available.
			Detail, // Apply the layout Detail.
			Pulse, // Apply the Pulse feed layout (media-centric folders).
		}
	}
}
