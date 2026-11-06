// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace Files.App.Services.Pulse
{
	/// <summary>
	/// A stack of files that are versions/siblings of one logical document
	/// ("Invoice-0098.pdf", "Invoice-0099.pdf", "Invoice-0101 (1).pdf").
	/// </summary>
	/// <param name="Key">Normalized basename + extension the members share.</param>
	/// <param name="Latest">Most recently modified member; it represents the stack in the feed.</param>
	/// <param name="Members">All members, newest first.</param>
	public sealed record FileStack(string Key, ListedItem Latest, IReadOnlyList<ListedItem> Members)
	{
		public int Count => Members.Count;
	}

	/// <summary>
	/// Deterministic, instant version/sibling detection for the Pulse feed. Works purely on
	/// names and timestamps: a normalized basename (version suffixes, copy markers and trailing
	/// counters removed) plus the extension identifies the document, and members must be
	/// modified within a bounded window of each other so unrelated same-named files far apart
	/// in time stay separate.
	/// </summary>
	public static partial class FileStackService
	{
		/// <summary>Members further apart than this from the stack's latest file are not stacked.</summary>
		public static readonly TimeSpan ProximityWindow = TimeSpan.FromDays(45);

		private const int MinimumMembers = 2;

// " (1)", " - Copy", "-copy", " copy 2", "_v2", " v3", "-final", "_FINAL2", "(2)", trailing "-1" / "_01" counters,
		// and the Pulse tool exporters' version markers: " (trimmed)", " (converted)", " (edited)", " (frame)", " (gif)"
		[GeneratedRegex(@"(\s*\(\d+\)|\s*\((trimmed|converted|edited|frame|gif)\)|[\s_-]+(copy|kopie|final|draft|new|old|backup|bak)(\s*\d+)?|[\s_-]*v\d+(\.\d+)*|[\s_-]+\d{1,3})+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
		private static partial Regex VersionSuffix();

		// Leading date/time stamps such as "2026-03-01_", "20260301-", "2026.03.01 "
		[GeneratedRegex(@"^(\d{4}[-._]?\d{2}[-._]?\d{2}[-_T ]?(\d{2}[-.:]?\d{2}([-.:]?\d{2})?)?[\s_-]*)", RegexOptions.CultureInvariant)]
		private static partial Regex LeadingTimestamp();

		/// <summary>
		/// Groups files into stacks. Folders, shortcuts and files whose normalized name is
		/// empty never stack. Items not part of any stack are not returned.
		/// </summary>
		public static IReadOnlyList<FileStack> BuildStacks(IEnumerable<ListedItem> items)
		{
			var groups = new Dictionary<string, List<ListedItem>>(StringComparer.OrdinalIgnoreCase);

			foreach (var item in items)
			{
				if (TryGetStackKey(item) is not { } key)
					continue;

				if (!groups.TryGetValue(key, out var members))
					groups[key] = members = [];
				members.Add(item);
			}

			var stacks = new List<FileStack>();
			foreach (var (key, members) in groups)
			{
				if (members.Count < MinimumMembers)
					continue;

				members.Sort((a, b) => b.ItemDateModifiedReal.CompareTo(a.ItemDateModifiedReal));

				// Split into runs: a member joins the current stack only if it is close in time to its latest file
				var run = new List<ListedItem> { members[0] };
				for (int i = 1; i < members.Count; i++)
				{
					if (run[0].ItemDateModifiedReal - members[i].ItemDateModifiedReal <= ProximityWindow)
					{
						run.Add(members[i]);
						continue;
					}

					if (run.Count >= MinimumMembers)
						stacks.Add(new FileStack(key, run[0], run.ToArray()));
					run = [members[i]];
				}
				if (run.Count >= MinimumMembers)
					stacks.Add(new FileStack(key, run[0], run.ToArray()));
			}

			return stacks;
		}

		/// <summary>Normalized "basename|ext" key, or null when the item cannot be part of a stack.</summary>
		public static string? TryGetStackKey(ListedItem item)
		{
			if (item.IsFolder || item.IsShortcut || item.IsRecycleBinItem || string.IsNullOrEmpty(item.ItemNameRaw))
				return null;

			var extension = item.FileExtension ?? string.Empty;
			var baseName = extension.Length > 0 && item.ItemNameRaw.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
				? item.ItemNameRaw[..^extension.Length]
				: item.ItemNameRaw;

			var normalized = Normalize(baseName);
			if (normalized.Length < 3)
				return null;

			return normalized + "|" + extension.ToLowerInvariant();
		}

		/// <summary>Strips version markers, copy markers, counters and leading timestamps; exposed for testing.</summary>
		public static string Normalize(string baseName)
		{
			var value = LeadingTimestamp().Replace(baseName, string.Empty);
			value = VersionSuffix().Replace(value, string.Empty);
			value = value.Trim(' ', '_', '-', '.');
			return value.ToLowerInvariant();
		}
	}
}
