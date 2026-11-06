// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Files.App.Helpers
{
	/// <summary>
	/// P/Invoke bindings for the shared NAI lexical search kernel
	/// (<c>nai_search_core.dll</c>, built from Seelen-UI <c>libs/nai-search-core</c>).
	/// The kernel implements normalization, tokenization, initials and bounded
	/// lexical scoring; this class only marshals UTF-8 buffers and status codes.
	/// </summary>
	internal static partial class NaiSearchNative
	{
		public const int NscOk = 0;
		public const int NscErrInvalid = -2;
		public const int NscErrPanic = -3;

		private static readonly Lazy<bool> _available = new(Probe);

		/// <summary>True when the native kernel is loadable and the ABI version matches.</summary>
		public static bool IsAvailable => _available.Value;

		private static bool Probe()
		{
			try
			{
				return Version() == 1;
			}
			catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
			{
				return false;
			}
		}

		[LibraryImport("nai_search_core", EntryPoint = "nsc_version")]
		private static partial uint Version();

		/// <summary>
		/// One-shot kernel score. Returns <c>NscOk..</c> semantics:
		/// <c>&gt;= 0</c> score, <c>-1</c> no match, <c>&lt; -1</c> kernel error.
		/// The facade contract maps every negative value to "no match".
		/// </summary>
		[LibraryImport("nai_search_core", EntryPoint = "nsc_score")]
		private static partial int Score(byte[] query, int queryLen, byte[] candidate, int candidateLen);

		[LibraryImport("nai_search_core", EntryPoint = "nsc_query_create")]
		private static partial int QueryCreate(byte[] query, int queryLen, out nint handle);

		[LibraryImport("nai_search_core", EntryPoint = "nsc_query_score")]
		private static partial int QueryScore(nint handle, byte[] candidate, int candidateLen);

		[LibraryImport("nai_search_core", EntryPoint = "nsc_query_free")]
		private static partial void QueryFree(nint handle);

		/// <summary>
		/// Scores one candidate through the kernel. Returns <c>-1</c> for "no
		/// match" and for every kernel error; never throws for a missing module
		/// (the caller degrades to no-match instead of inventing a fallback scorer).
		/// </summary>
		public static int Score(string query, string candidate)
		{
			if (!IsAvailable)
				return -1;

			byte[] queryBytes;
			byte[] candidateBytes;
			try
			{
				queryBytes = Encoding.UTF8.GetBytes(query);
				candidateBytes = Encoding.UTF8.GetBytes(candidate);
			}
			catch (EncoderFallbackException)
			{
				return -1;
			}

			int result = Score(queryBytes, queryBytes.Length, candidateBytes, candidateBytes.Length);
			return result < 0 ? -1 : result;
		}
	}
}