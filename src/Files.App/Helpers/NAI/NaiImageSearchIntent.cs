// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Globalization;
using System.Text;

namespace Files.App.Helpers
{
	public sealed record NaiImageSearchRequest(string Query, bool AllCached);

	public static class NaiImageSearchIntent
	{
		private static readonly string[] prefixes =
		[
			"hledam fotografii", "hledam screenshot", "najdi screenshot", "hledam obrazek", "najdi obrazek", "hledam fotku", "najdi fotku",
			"hladam fotografiu", "hladam screenshot", "najdi snimku obrazovky", "hladam obrazok", "najdi obrazok", "hladam fotku",
			"find a screenshot", "find a photograph", "find a picture", "find an image", "find a photo", "find screenshot", "find image", "find picture",
			"tim hinh chup man hinh", "tim anh chup man hinh", "tim buc anh", "tim hinh anh", "tim anh", "tim hinh",
			"screenshot", "photograph", "picture", "image", "photo", "obrazek", "obrazok", "fotka", "hinh anh", "anh", "hinh", "vision",
		];

		public static NaiImageSearchRequest? Parse(string? text)
		{
			if (string.IsNullOrWhiteSpace(text) || text.Length > 2000) return null;
			var original = text.Trim();
			var normalized = Normalize(original);
			foreach (var prefix in prefixes.OrderByDescending(value => value.Length))
			{
				if (!normalized.StartsWith(prefix, StringComparison.Ordinal) ||
					(normalized.Length > prefix.Length && normalized[prefix.Length] is not (' ' or ':'))) continue;
				// Accent folding keeps the precomposed CS/SK/VI prefix length.
				// Locate the boundary by normalized prefix rather than translating the query.
				var end = 0;
				while (end < original.Length && Normalize(original[..end]).Length < prefix.Length) end++;
				var query = original[end..].TrimStart(' ', ':');
				var all = query.StartsWith("cache:", StringComparison.OrdinalIgnoreCase);
				return new(all ? query[6..].Trim() : query, all);
			}
			return null;
		}

		public static string Normalize(string text)
		{
			var result = new StringBuilder(text.Length);
			foreach (var character in text.Normalize(NormalizationForm.FormD))
			{
				if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
				result.Append(character is 'đ' or 'Đ' ? 'd' : char.ToLowerInvariant(character));
			}
			return result.ToString();
		}
	}
}
