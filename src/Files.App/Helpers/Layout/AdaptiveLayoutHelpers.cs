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
			Windows.Storage.UserDataPaths.GetDefault().Pictures,
			Windows.Storage.UserDataPaths.GetDefault().Videos,
		]);

		/// <summary>True for the Pictures and Videos folders (not their subfolders).</summary>
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
					if (viewMode is "Pictures" or "Videos")
					{
						layout = FolderLayoutModes.PulseView;
						return true;
					}

					// Other Explorer modes (Documents, General, ...) are not a media signal:
					// fall through to the content sample so a media folder still opens in Pulse.
				}

				int total = 0, media = 0;
				foreach (var file in SystemIO.Directory.EnumerateFiles(path))
				{
					if (IsMediaExtension(SystemIO.Path.GetExtension(file)))
						media++;
					if (++total >= 200)
						break;
				}

				if (total is 0)
					return false;

				// Any image/video/audio makes this a visual folder; a plain directory stays default.
				layout = media > 0 ? FolderLayoutModes.PulseView : FolderLayoutModes.DetailsView;
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
				// Another Explorer mode must not suppress the content rule.
				_ => Layouts.None,
			};
		}

		private static Layouts GetContentLayout(IList<ListedItem> filesAndFolders)
		{
			if (filesAndFolders.Count is 0)
				return Layouts.None;

			// Any image/video/audio makes this a visual folder; a plain directory stays default.
			return filesAndFolders.Any(IsMedia) ? Layouts.Pulse : Layouts.Detail;

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
