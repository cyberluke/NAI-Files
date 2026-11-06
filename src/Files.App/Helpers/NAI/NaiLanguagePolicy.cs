// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Helpers
{
	public static class NaiLanguagePolicy
	{
		public static string? Normalize(string? value) => value?.Replace('_', '-').ToLowerInvariant() switch
		{
			"cs" or "cs-cz" => "cs-CZ",
			"sk" or "sk-sk" => "sk-SK",
			"vi" or "vi-vn" => "vi",
			"en" or "en-us" => "en-US",
			_ => null,
		};
	}
}
