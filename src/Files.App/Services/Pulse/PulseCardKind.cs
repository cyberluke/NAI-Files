// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Services.Pulse
{
	/// <summary>
	/// Content family a Pulse card renders as. Decides which card template is used
	/// and which preview pipeline (thumbnail, media frame, markdown excerpt) fills it.
	/// </summary>
	public enum PulseCardKind
	{
		/// <summary>Folders, archives, executables and anything without a rich preview.</summary>
		Generic,

		/// <summary>Raster/vector images: the thumbnail is the card.</summary>
		Image,

		/// <summary>Video: frame thumbnail, duration badge and hover preview.</summary>
		Video,

		/// <summary>Audio: cover art / waveform and duration badge.</summary>
		Audio,

		/// <summary>Markdown: rendered excerpt (headings, first text) instead of a file icon.</summary>
		Markdown,

		/// <summary>PDF and office documents whose shell preview handler produces a cover page.</summary>
		Document,

		/// <summary>Archives (zip, 7z, rar, msix…). Files lists browsable archives as folders, so they get their own family.</summary>
		Archive,

		/// <summary>A deterministic stack of sibling versions of the same document.</summary>
		Stack,
	}
}
