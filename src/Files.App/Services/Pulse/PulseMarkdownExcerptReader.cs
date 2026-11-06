// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Files.App.Services.Pulse
{
	/// <summary>
	/// Reads only the head of a markdown file and derives a <see cref="PulseMarkdownExcerpt"/>:
	/// headings, fenced code blocks, frontmatter and agent-spec vocabulary. No markdown
	/// renderer is involved, so thousands of cards stay cheap.
	/// </summary>
	public static partial class PulseMarkdownExcerptReader
	{
		// Enough to find the title and a handful of preview lines; the counts are a lower bound for huge files
		private const int MaxBytes = 64 * 1024;
		private const int MaxPreviewLines = 4;
		private const int MaxPreviewLineLength = 72;

		private static readonly ConcurrentDictionary<string, (long Ticks, long Length, PulseMarkdownExcerpt Excerpt)> cache = new(StringComparer.OrdinalIgnoreCase);

		[GeneratedRegex(@"\b(agent|spec|specification|prompt|system prompt|todo|implementation|acceptance criteria|requirements?|workflow|task(s)?|goal(s)?|deliverables?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
		private static partial Regex AgentVocabulary();

		[GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*#*\s*$", RegexOptions.CultureInvariant)]
		private static partial Regex Heading();

		public static async Task<PulseMarkdownExcerpt?> ReadAsync(string path, CancellationToken token)
		{
			try
			{
				var info = new FileInfo(path);
				if (!info.Exists)
					return null;

				if (cache.TryGetValue(path, out var cached) && cached.Ticks == info.LastWriteTimeUtc.Ticks && cached.Length == info.Length)
					return cached.Excerpt;

				var excerpt = await Task.Run(() => Read(path, info), token);
				if (excerpt is not null)
					cache[path] = (info.LastWriteTimeUtc.Ticks, info.Length, excerpt);

				return excerpt;
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch
			{
				return null;
			}
		}

		private static PulseMarkdownExcerpt? Read(string path, FileInfo info)
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
			var buffer = new byte[(int)Math.Min(info.Length, MaxBytes)];
			var read = stream.Read(buffer, 0, buffer.Length);
			var text = Encoding.UTF8.GetString(buffer, 0, read);

			return Parse(text, Path.GetFileNameWithoutExtension(path));
		}

		/// <summary>Parses markdown text; exposed for deterministic testing.</summary>
		public static PulseMarkdownExcerpt Parse(string text, string fallbackTitle)
		{
			string? title = null;
			var lines = new List<string>(MaxPreviewLines);
			int headingCount = 0, codeBlockCount = 0, agentHits = 0;
			bool inFence = false, inFrontmatter = false, frontmatterSeen = false;

			foreach (var rawLine in text.AsSpan().EnumerateLines())
			{
				var line = rawLine.Trim();

				// YAML frontmatter is metadata, not preview text
				if (!frontmatterSeen && line.SequenceEqual("---") && title is null && lines.Count == 0)
				{
					inFrontmatter = !inFrontmatter;
					if (!inFrontmatter)
						frontmatterSeen = true;
					continue;
				}
				if (inFrontmatter)
				{
					if (AgentVocabulary().IsMatch(line))
						agentHits++;
					continue;
				}

				if (line.StartsWith("```") || line.StartsWith("~~~"))
				{
					if (!inFence)
						codeBlockCount++;
					inFence = !inFence;
					continue;
				}
				if (inFence)
					continue;

				if (line.IsEmpty)
					continue;

				var lineText = line.ToString();
				if (AgentVocabulary().IsMatch(lineText))
					agentHits++;

				var heading = Heading().Match(lineText);
				if (heading.Success)
				{
					headingCount++;
					var headingText = heading.Groups[2].Value;
					if (title is null)
					{
						title = headingText;
						continue;
					}
					if (lines.Count < MaxPreviewLines)
						lines.Add(Truncate(heading.Groups[1].Value + " " + headingText));
					continue;
				}

				if (lines.Count < MaxPreviewLines)
				{
					// Checkbox items and bullets read well as preview lines; strip emphasis noise
					var cleaned = lineText.Replace("**", string.Empty).Replace("__", string.Empty);
					if (cleaned.StartsWith("- [ ] ", StringComparison.Ordinal))
						cleaned = "☐ " + cleaned[6..];
					else if (cleaned.StartsWith("- [x] ", StringComparison.OrdinalIgnoreCase))
						cleaned = "☑ " + cleaned[6..];
					lines.Add(Truncate(cleaned));
				}
			}

			title ??= fallbackTitle;

			// Spec vocabulary in the title or several hits in the head of the document
			var isAgentSpec = AgentVocabulary().IsMatch(title) || agentHits >= 3;

			return new PulseMarkdownExcerpt(title, lines, headingCount, codeBlockCount, isAgentSpec);
		}

		private static string Truncate(string value)
			=> value.Length <= MaxPreviewLineLength ? value : string.Concat(value.AsSpan(0, MaxPreviewLineLength - 1), "…");
	}
}
