// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;

namespace Files.App.Helpers
{
	public static class NaiThumbnailAvailability
	{
		private const uint RecallOnOpen = 0x00040000;
		private const uint RecallOnDataAccess = 0x00400000;
		public static bool CanReadAttributes(uint attributes) =>
			(attributes & ((uint)FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) == 0;

		public static bool CanReadContent(string? path)
		{
			if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
			try { return CanReadAttributes((uint)File.GetAttributes(path)); }
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
		}
	}
}
