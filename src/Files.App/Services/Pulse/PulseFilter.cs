// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Services.Pulse
{
	/// <summary>
	/// Content filter chips above the Pulse feed. Real folders always pass so navigation is never hidden;
	/// archives are their own group (Files lists browsable archives as folders, so they must not ride along).
	/// </summary>
	public enum PulseFilter
	{
		All,
		Photos,
		Videos,
		Audio,
		Markdown,
		Documents,
		Archives,
	}

	public static class PulseFilterExtensions
	{
		public static bool Matches(this PulseFilter filter, ListedItem item)
		{
			if (filter is PulseFilter.All)
				return true;

			var kind = PulseCardClassifier.Classify(item);

			// Plain folders stay visible under every content filter; archives only under their own chip
			if (item.IsFolder && kind is not PulseCardKind.Archive)
				return true;

			return filter switch
			{
				PulseFilter.Photos => kind is PulseCardKind.Image,
				PulseFilter.Videos => kind is PulseCardKind.Video,
				PulseFilter.Audio => kind is PulseCardKind.Audio,
				PulseFilter.Markdown => kind is PulseCardKind.Markdown,
				PulseFilter.Documents => kind is PulseCardKind.Document,
				PulseFilter.Archives => kind is PulseCardKind.Archive,
				_ => true,
			};
		}
	}
}
