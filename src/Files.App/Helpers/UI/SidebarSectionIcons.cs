// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using Files.App.Data.Contracts;
using Files.App.Utils.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI.ViewManagement;
using System.IO;

namespace Files.App.Helpers
{
	public static class SidebarSectionIcons
	{
		private const string BasePath = "ms-appx:///Assets/FluentIcons/SidebarSections/";

		private static readonly AccessibilitySettings AccessibilitySettings = new();
		private static readonly UISettings UISettings = new();

		/// <summary>
		/// Section icon with the NAI OS icon theme applied first: the desktop-wide theme
		/// exports semantic folder glyphs (home, favorites, cloud, network), so the matching
		/// sections get the same colorized icons as the folders in the rest of the app.
		/// UI-only sections (drives, tags, ...) fall back to the bundled Fluent icon.
		/// </summary>
		public static async Task<BitmapImage?> LoadIconAsync(SectionType section)
		{
			var themedName = section switch
			{
				SectionType.Home => "home",
				SectionType.Pinned => "favorites",
				SectionType.CloudDrives => "cloud",
				SectionType.Network => "network",
				_ => null,
			};

			if (themedName is not null)
			{
				// Resolve through the theme with a probe path whose folder NAME is the
				// semantic glyph ("C:\home" -> folderNames["home"]); the helper rejects
				// drive roots, so any rooted dummy path works.
				var probePath = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, themedName);
				var bytes = NaiIconThemeHelper.GetIcon(probePath, (int)(16 * App.AppModel.AppWindowDPI), true);
				if (bytes is not null)
				{
					try
					{
						return await bytes.ToBitmapAsync();
					}
					catch (Exception ex)
					{
						App.Logger.LogDebug(ex, "Sidebar section themed icon decode failed for {Section}", section);
					}
				}
			}

			var fallback = For(section);
			return fallback is null ? null : new BitmapImage(new Uri(fallback));
		}

		public static string? For(SectionType section) => section switch
		{
			SectionType.Home => Resolve("Home"),
			SectionType.Pinned => Resolve("Pinned"),
			SectionType.Library => Resolve("Libraries"),
			SectionType.Drives => Resolve("Drives"),
			SectionType.CloudDrives => Resolve("CloudDrives"),
			SectionType.Network => Resolve("Network"),
			SectionType.WSL => Resolve("Wsl"),
			SectionType.FileTag => Resolve("Tags"),
			_ => null
		};

		private static string Resolve(string name)
		{
			if (!AccessibilitySettings.HighContrast)
				return $"{BasePath}{name}.png";

			var bg = UISettings.GetColorValue(UIColorType.Background);
			var suffix = bg.R + bg.G + bg.B < 384 ? "Black" : "White";
			return $"{BasePath}{name}-{suffix}.png";
		}
	}
}