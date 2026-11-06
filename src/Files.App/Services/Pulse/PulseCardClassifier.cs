// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Shared.Helpers;

namespace Files.App.Services.Pulse
{
	/// <summary>
	/// Maps a listed item to the Pulse card family it renders as. Extension-based
	/// and allocation-free, so it is safe to call per container realization.
	/// </summary>
	public static class PulseCardClassifier
	{
		private static readonly string[] DocumentExtensions =
		[
			".pdf", ".doc", ".docx", ".ppt", ".pptx", ".xls", ".xlsx", ".odt", ".odp", ".ods", ".rtf", ".epub",
		];

		public static PulseCardKind Classify(ListedItem item)
		{
			// Browsable archives are enumerated as folders (ZipItem): classify them before the folder rule
			if (item.IsArchive || (!item.IsFolder && FileExtensionHelpers.IsZipFile(item.FileExtension)))
				return PulseCardKind.Archive;

			if (item.IsFolder || item.IsShortcut)
				return PulseCardKind.Generic;

			var extension = item.FileExtension;
			if (string.IsNullOrEmpty(extension))
				return PulseCardKind.Generic;

			if (FileExtensionHelpers.IsImageFile(extension))
				return PulseCardKind.Image;
			if (FileExtensionHelpers.IsVideoFile(extension))
				return PulseCardKind.Video;
			if (FileExtensionHelpers.IsAudioFile(extension))
				return PulseCardKind.Audio;
			// Everything the bundled FFmpeg 9 toolset can decode gets a media card even when the
			// shell type lists do not know the container (the Pulse tools are ffmpeg-backed).
			if (PulseMediaExtensions.IsVideoExtension(extension))
				return PulseCardKind.Video;
			if (PulseMediaExtensions.IsAudioExtension(extension))
				return PulseCardKind.Audio;
			if (FileExtensionHelpers.IsMarkdownFile(extension))
				return PulseCardKind.Markdown;
			if (FileExtensionHelpers.HasExtension(extension, DocumentExtensions))
				return PulseCardKind.Document;

			return PulseCardKind.Generic;
		}

		/// <summary>
		/// Short upper-case type badge shown in the card footer ("JPG", "MP4", "MD").
		/// Folders get no badge: their card already reads as a folder.
		/// </summary>
		public static string GetTypeBadge(ListedItem item)
		{
			// Real folders carry no badge; archives are "folders" to Files but show their format
			if (item.IsFolder && !item.IsArchive)
				return string.Empty;

			var extension = item.FileExtension;
			if (string.IsNullOrEmpty(extension))
				return string.Empty;

			return extension.TrimStart('.').ToUpperInvariant();
		}
	}
}
