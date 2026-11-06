// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Helpers
{
	/// <summary>
	/// Kelvin is paired and reachable, but its local NPU retrieval backend
	/// (/v1/apps, /v1/index/*, /v1/search) is not available. Raised instead of a
	/// raw HTTP 502/503 so callers can present the recovery guidance once.
	/// </summary>
	public sealed class NaiRetrievalUnavailableException : Exception
	{
		public NaiRetrievalUnavailableException(string message) : base(message) { }
	}
}