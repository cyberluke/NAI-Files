// Copyright (c) Files Community
// Licensed under the MIT License.

using System;

namespace Files.App.Helpers
{
	/// <summary>
	/// Files adapter for the shared NAI lexical search kernel
	/// (<c>nai_search_core.dll</c>). Normalization, tokenization, initials,
	/// matching and scoring all execute inside the kernel; this facade keeps
	/// only Files-specific routing (raw AQS / colon-structured detection and
	/// the <c>files:</c> scope prefix) in C#.
	/// </summary>
	public static class NaiFileSearch
	{
		private const int MaxQueryLength = 256;
		private const int MaxCandidateLength = 1024;

		/// <summary>
		/// Returns -1 for no match, otherwise a higher-is-better deterministic score.
		/// Empty queries score 0. Structured queries must be routed to AQS, not this scorer.
		/// Inputs exceeding 256 query or 1024 candidate UTF-16 units are rejected, not truncated.
		/// Equal scores deliberately remain ties; callers should retain their stable input order.
		/// </summary>
		public static int Score(string query, string candidate)
		{
			if (query is null || candidate is null || query.Length > MaxQueryLength || candidate.Length > MaxCandidateLength)
				return -1;

			if (IsStructuredQuery(query))
				return -1;
			query = StripFilesPrefix(query);
			if (string.IsNullOrWhiteSpace(query))
				return 0;

			// No second C# fallback scorer: when the native kernel module is
			// missing, lexical matching honestly reports no match.
			return NaiSearchNative.Score(query, candidate);
		}

		public static bool Matches(string query, string candidate) => Score(query, candidate) >= 0;

		/// <summary>
		/// Recognizes the Files raw-AQS marker and colon field syntax after an optional files: prefix.
		/// Conservatively treats any remaining colon as structured, matching Files' existing AQS routing.
		/// Does not remove $, alter quotes, fold accents, or rewrite operators.
		/// </summary>
		public static bool IsStructuredQuery(string query)
		{
			if (query is null)
				return false;

			ReadOnlySpan<char> value = StripFilesPrefix(query).AsSpan().TrimStart();
			return !value.IsEmpty && (value[0] == '$' || value.IndexOf(':') >= 0);
		}

		/// <summary>
		/// Removes one case-insensitive leading files: routing prefix and its surrounding leading whitespace.
		/// Without that prefix the original string is returned unchanged; the remainder is never normalized.
		/// </summary>
		public static string StripFilesPrefix(string query)
		{
			if (query is null)
				return string.Empty;

			ReadOnlySpan<char> value = query.AsSpan().TrimStart();
			return value.StartsWith("files:", StringComparison.OrdinalIgnoreCase)
				? value.Slice(6).TrimStart().ToString()
				: query;
		}
	}
}