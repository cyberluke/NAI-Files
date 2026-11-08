// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Helpers
{
	/// <summary>
	/// The paired AI engine (LUSI first, Kelvin fallback) is reachable but its
	/// local retrieval backend (/v1/apps, /v1/index/*, /v1/search on Kelvin) is
	/// not serving. Raised instead of a raw HTTP 502/503 so callers can present
	/// the recovery guidance once.
	/// </summary>
	public sealed class NaiRetrievalUnavailableException : Exception
	{
		public NaiRetrievalUnavailableException(string message) : base(message) { }
	}
}