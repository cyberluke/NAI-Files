// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Files.App.Helpers
{
	public sealed record NaiIndexedDocument(string Id, string RelativePath, long Length, long LastWriteUtcTicks, string TextSha256,
		string[] ChunkIds, int IndexedCharacters, bool Truncated, string ExtractionVersion, DateTimeOffset IndexedAt);
	public sealed record NaiSemanticIndexManifest(int SchemaVersion, string Folder, string Dataset, bool Recursive,
		NaiIndexedDocument[] Documents, string[] PendingDeleteIds, DateTimeOffset UpdatedAt, bool Partial);
	[JsonSerializable(typeof(NaiSemanticIndexManifest))]
	public partial class NaiSemanticJsonContext : JsonSerializerContext;

	public sealed class NaiSemanticIndexStore(string directory)
	{
		private string StatePath(string folder) => Path.Combine(directory, NaiSemanticProtocol.Dataset(folder) + ".json");
		public FileStream AcquireWriter(string folder)
		{
			Directory.CreateDirectory(directory);
			return new FileStream(StatePath(folder) + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		}
		public async Task<NaiSemanticIndexManifest?> ReadAsync(string folder, CancellationToken token)
		{
			var path = StatePath(folder);
			if (!File.Exists(path)) return null;
			await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
			if (stream.Length > 8 * 1024 * 1024) throw new InvalidDataException("Semantic manifest exceeds its budget");
			var value = await JsonSerializer.DeserializeAsync(stream, NaiSemanticJsonContext.Default.NaiSemanticIndexManifest, token);
			Validate(value, folder);
			return value;
		}
		public async Task SaveAsync(NaiSemanticIndexManifest value, CancellationToken token)
		{
			Validate(value, value.Folder);
			Directory.CreateDirectory(directory);
			var path = StatePath(value.Folder); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
				{
					await JsonSerializer.SerializeAsync(stream, value, NaiSemanticJsonContext.Default.NaiSemanticIndexManifest, token);
					if (stream.Length > 8 * 1024 * 1024) throw new InvalidDataException("Semantic manifest exceeds its budget");
				}
				token.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
			}
			finally { if (File.Exists(temporary)) File.Delete(temporary); }
		}
		private static void Validate(NaiSemanticIndexManifest? state, string folder)
		{
			if (state is null || state.SchemaVersion != 1 || !string.Equals(state.Folder, NaiSemanticProtocol.CanonicalFolder(folder), StringComparison.OrdinalIgnoreCase)
				|| state.Dataset != NaiSemanticProtocol.Dataset(folder) || state.Documents is null || state.Documents.Length > 256 || state.PendingDeleteIds is null || state.PendingDeleteIds.Length > 16384)
				throw new InvalidDataException("Invalid semantic index scope");
			var files = new HashSet<string>(StringComparer.Ordinal); var chunks = new HashSet<string>(StringComparer.Ordinal);
			foreach (var item in state.Documents)
			{
				if (item is null || string.IsNullOrWhiteSpace(item.RelativePath) || item.RelativePath.Length > 4096 || ResolvePath(state.Folder, item.RelativePath) is null
					|| item.Id != NaiSemanticProtocol.FileId(item.RelativePath) || !files.Add(item.Id) || item.Length < 0 || item.LastWriteUtcTicks <= 0
					|| item.TextSha256 is not { Length: 64 } || !item.TextSha256.All(Uri.IsHexDigit) || item.ChunkIds is null || item.ChunkIds.Length is < 1 or > 16
					|| item.ChunkIds.Any(id => !id.StartsWith(item.Id + "-", StringComparison.Ordinal) || id.Length > 256 || !chunks.Add(id)))
					throw new InvalidDataException("Invalid semantic document identity");
			}
			if (state.PendingDeleteIds.Any(id => id is null || !id.StartsWith("file-", StringComparison.Ordinal) || id.Length > 256 || chunks.Contains(id)))
				throw new InvalidDataException("Invalid semantic cleanup identities");
		}
		public static string? ResolvePath(string folder, string relativePath)
		{
			try
			{
				if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Split((char[])[Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]).Any(part => part is ".." or ".")) return null;
				var root = NaiSemanticProtocol.CanonicalFolder(folder);
				var path = Path.GetFullPath(Path.Combine(root, relativePath));
				var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
				return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path : null;
			}
			catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
		}
		public static bool IsCurrentLocalFile(string folder, NaiIndexedDocument document)
		{
			var path = ResolvePath(folder, document.RelativePath); if (path is null) return false;
			try
			{
				var file = new FileInfo(path);
				if (!file.Exists || file.LinkTarget is not null || !NaiThumbnailAvailability.CanReadAttributes((uint)file.Attributes)
					|| file.Length != document.Length || file.LastWriteTimeUtc.Ticks != document.LastWriteUtcTicks) return false;
				var root = NaiSemanticProtocol.CanonicalFolder(folder);
				for (var parent = file.Directory; parent is not null; parent = parent.Parent)
				{
					if (parent.LinkTarget is not null || !NaiThumbnailAvailability.CanReadAttributes((uint)parent.Attributes)) return false;
					if (string.Equals(Path.TrimEndingDirectorySeparator(parent.FullName), root, StringComparison.OrdinalIgnoreCase)) return true;
				}
				return false;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
		}
	}
}
