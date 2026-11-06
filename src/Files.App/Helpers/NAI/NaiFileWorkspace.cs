// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Security.Cryptography;

namespace Files.App.Helpers
{
	public sealed record NaiFileSnapshot(string Name, string Path, bool IsFolder, long Size, long ModifiedUtcTicks, bool Local);
	public sealed record NaiFolderSnapshot(string Folder, NaiFileSnapshot[] Items, int Skipped, bool Truncated, DateTimeOffset ObservedAt);
	public enum NaiComparisonKind { OnlyLeft, OnlyRight, TypeChanged, MetadataChanged, MetadataEqual }
	public sealed record NaiFolderDifference(string Name, NaiComparisonKind Kind, NaiFileSnapshot? Left, NaiFileSnapshot? Right);
	public sealed record NaiDuplicateGroup(string Sha256, NaiFileSnapshot[] Files);
	public sealed record NaiReviewProgress(int Completed, int Total, int Skipped);

	public static class NaiFileWorkspace
	{
		public const int EntryLimit = 10000;
		public static Task<NaiFolderSnapshot> SnapshotAsync(string folder, CancellationToken token) => Task.Run(() =>
		{
			if (!Path.IsPathFullyQualified(folder)) throw new ArgumentException("Expected a local folder path");
			var items = new List<NaiFileSnapshot>();
			var skipped = 0;
			var truncated = false;
			foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
			{
				token.ThrowIfCancellationRequested();
				if (items.Count + skipped >= EntryLimit) { truncated = true; break; }
				try
				{
					var directory = entry.Attributes.HasFlag(FileAttributes.Directory);
					items.Add(new(entry.Name, entry.FullName, directory, directory ? 0 : ((FileInfo)entry).Length,
						entry.LastWriteTimeUtc.Ticks, NaiThumbnailAvailability.CanReadAttributes((uint)entry.Attributes) && entry.LinkTarget is null));
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
			}
			return new NaiFolderSnapshot(Path.GetFullPath(folder), items.ToArray(), skipped, truncated, DateTimeOffset.UtcNow);
		}, token);

		public static IReadOnlyList<NaiFolderDifference> Compare(NaiFolderSnapshot left, NaiFolderSnapshot right)
		{
			var a = left.Items.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
			var b = right.Items.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
			return a.Keys.Concat(b.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(name =>
			{
				a.TryGetValue(name, out var x); b.TryGetValue(name, out var y);
				var kind = x is null ? NaiComparisonKind.OnlyRight : y is null ? NaiComparisonKind.OnlyLeft
					: x.IsFolder != y.IsFolder ? NaiComparisonKind.TypeChanged
					: x.Size != y.Size || x.ModifiedUtcTicks != y.ModifiedUtcTicks ? NaiComparisonKind.MetadataChanged : NaiComparisonKind.MetadataEqual;
				return new NaiFolderDifference(name, kind, x, y);
			}).ToArray();
		}

		public static async Task<IReadOnlyList<NaiDuplicateGroup>> ReviewDuplicatesAsync(NaiFolderSnapshot snapshot, IProgress<NaiReviewProgress>? progress, CancellationToken token)
		{
			var candidates = snapshot.Items.Where(item => !item.IsFolder && item.Local && item.Size <= 512L * 1024 * 1024)
				.GroupBy(item => item.Size).Where(group => group.Count() > 1).SelectMany(group => group).ToArray();
			var hashes = new List<(string Hash, NaiFileSnapshot File)>();
			var completed = 0; var skipped = snapshot.Items.Count(item => !item.IsFolder && (!item.Local || item.Size > 512L * 1024 * 1024));
			progress?.Report(new(0, candidates.Length, skipped));
			foreach (var item in candidates)
			{
				token.ThrowIfCancellationRequested();
				try
				{
					if (!MatchesCurrentMetadata(item)) { skipped++; continue; }
					await using var stream = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
					var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
					if (MatchesCurrentMetadata(item)) hashes.Add((hash, item)); else skipped++;
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
				finally { progress?.Report(new(++completed, candidates.Length, skipped)); }
			}
			return hashes.GroupBy(item => item.Hash, StringComparer.Ordinal).Where(group => group.Count() > 1)
				.Select(group => new NaiDuplicateGroup(group.Key, group.Select(item => item.File).ToArray())).ToArray();
		}

		public static bool MatchesCurrentMetadata(NaiFileSnapshot item)
		{
			try
			{
				var file = new FileInfo(item.Path);
				return file.Exists && file.LinkTarget is null && NaiThumbnailAvailability.CanReadAttributes((uint)file.Attributes)
					&& file.Length == item.Size && file.LastWriteTimeUtc.Ticks == item.ModifiedUtcTicks;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
		}
	}
}
