// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Services.Pulse
{
	/// <summary>
	/// Cheap structural summary of a markdown file for the Pulse card: the title, the
	/// first content lines and counts, without rendering the document.
	/// </summary>
	/// <param name="Title">First H1 (or first heading) or the file name without extension.</param>
	/// <param name="Lines">Up to a few preview lines after the title (headings keep their "#" prefix).</param>
	/// <param name="HeadingCount">Number of ATX headings.</param>
	/// <param name="CodeBlockCount">Number of fenced code blocks.</param>
	/// <param name="IsAgentSpec">Whether the text reads like an AI agent spec/prompt/task definition.</param>
	public sealed record PulseMarkdownExcerpt(
		string Title,
		IReadOnlyList<string> Lines,
		int HeadingCount,
		int CodeBlockCount,
		bool IsAgentSpec)
	{
		public bool HasLines => Lines.Count > 0;

		public string Stats => string.Format(Strings.PulseMarkdownStats.GetLocalizedResource(), HeadingCount, CodeBlockCount);
	}
}
