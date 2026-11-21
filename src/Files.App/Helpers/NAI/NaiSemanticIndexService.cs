// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Security.Cryptography;
using System.Text;
using Files.App.AI.Semantic;
using Windows.Storage;

namespace Files.App.Helpers
{
	public sealed record NaiSemanticIndexProgress(int Completed, int Total, int Indexed, int Cached, int Skipped, int Failed, bool Limited);
	public sealed record NaiSemanticCandidate(string RelativePath, long Length, long LastWriteUtcTicks);
	public sealed record NaiSemanticIndexPlan(string Folder, bool Recursive, NaiSemanticCandidate[] Documents, int Skipped, bool Limited);
	public sealed record NaiSemanticFileMatch(string Path, double Score, string Evidence, DateTimeOffset IndexedAt);
	public sealed record NaiSemanticSearchResult(IReadOnlyList<NaiSemanticFileMatch> Matches, int CurrentDocuments, int StaleDocuments, bool Partial);

	public static class NaiSemanticIndexService
	{
		public static NaiSemanticIndexStore Store { get; } = new(Path.Combine(ApplicationData.Current.LocalFolder.Path, "nai-semantic"));
		private const string ExtractionVersion = "bounded-document-text-v1";
		public static async Task<NaiSemanticIndexPlan> PreviewAsync(string folder, bool recursive, CancellationToken token)
		{
			folder = NaiSemanticProtocol.CanonicalFolder(folder);
			var plan = await Task.Run(() => Enumerate(folder, recursive, token), token);
			return new(folder, recursive, plan.Files.Select(file => new NaiSemanticCandidate(Path.GetRelativePath(folder, file.FullName), file.Length, file.LastWriteTimeUtc.Ticks)).ToArray(), plan.Skipped, plan.Limited);
		}

		public static async Task<NaiSemanticIndexManifest> IndexAsync(NaiSemanticIndexPlan plan, IProgress<NaiSemanticIndexProgress>? progress, CancellationToken token)
		{
			var folder = NaiSemanticProtocol.CanonicalFolder(plan.Folder);
			if (plan.Documents.Length > 256 || plan.Documents.Any(item => item.Length < 0 || item.Length > 32L * 1024 * 1024)
				|| plan.Documents.Sum(item => item.Length) > 512L * 1024 * 1024) throw new ArgumentException("Approved index plan exceeds its budget");
			using var writer = Store.AcquireWriter(folder);
			var previous = await Store.ReadAsync(folder, token);
			var state = previous ?? new NaiSemanticIndexManifest(1, folder, NaiSemanticProtocol.Dataset(folder), plan.Recursive, [], [], DateTimeOffset.UtcNow, true);
			state = state with { Recursive = plan.Recursive, Partial = true };
			var documents = state.Documents.ToDictionary(item => item.Id, StringComparer.Ordinal);
			var pending = state.PendingDeleteIds.ToHashSet(StringComparer.Ordinal);
			int done = 0, indexed = 0, cached = 0, skipped = plan.Skipped, failed = 0;
			progress?.Report(new(0, plan.Documents.Length, 0, 0, skipped, 0, plan.Limited));
			await Store.SaveAsync(state, token);
			foreach (var candidate in plan.Documents)
			{
				token.ThrowIfCancellationRequested();
				var relative = candidate.RelativePath; var id = NaiSemanticProtocol.FileId(relative);
				try
				{
					var path = NaiSemanticIndexStore.ResolvePath(folder, relative);
					if (path is null || !plan.Recursive && Path.GetDirectoryName(relative) is not (null or "")) { skipped++; continue; }
					var file = new FileInfo(path);
					if (!file.Exists || file.Length != candidate.Length || file.LastWriteTimeUtc.Ticks != candidate.LastWriteUtcTicks) { skipped++; continue; }
					if (documents.TryGetValue(id, out var old) && old.ExtractionVersion == ExtractionVersion && NaiSemanticIndexStore.IsCurrentLocalFile(folder, old)) { cached++; continue; }
					if (!documents.ContainsKey(id) && documents.Count >= 256) { skipped++; continue; }
					file.Refresh(); var length = file.Length; var modified = file.LastWriteTimeUtc.Ticks;
					var guard = new NaiIndexedDocument(id, relative, length, modified, new string('0', 64), [], 0, false, ExtractionVersion, DateTimeOffset.UtcNow);
					if (!NaiSemanticIndexStore.IsCurrentLocalFile(folder, guard)) { skipped++; continue; }
					var text = await NaiDocumentReader.ReadAsync(file.FullName, 24001, token);
					if (!NaiSemanticIndexStore.IsCurrentLocalFile(folder, guard)) throw new IOException("Document changed during index extraction");
					if (string.IsNullOrWhiteSpace(text)) { skipped++; continue; }
					var lengthLimit = Math.Min(text.Length, 24000);
					if (lengthLimit < text.Length && lengthLimit > 0 && char.IsHighSurrogate(text[lengthLimit - 1])) lengthLimit--;
					var limitedText = text[..lengthLimit];
					var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(limitedText))).ToLowerInvariant();
					var parts = ChunkText(limitedText).Take(16).ToArray();
					var chunks = parts.Select((part, index) => new NaiSemanticChunk(id + "-" + hash[..16] + "-" + index, id, file.Name, part)).ToArray();
					// Persist write intent first. Cancellation/network failure can leave
					// accepted remote chunks, but never an untracked broad cleanup task.
					var liveIds = documents.Values.SelectMany(document => document.ChunkIds).ToHashSet(StringComparer.Ordinal);
					foreach (var chunk in chunks) if (!liveIds.Contains(chunk.Id)) pending.Add(chunk.Id);
					state = state with { PendingDeleteIds = pending.ToArray() };
					await Store.SaveAsync(state, token);
					foreach (var batch in chunks.Chunk(8))
					{
						var vectors = await LusiEmbedder.EmbedAsync(batch.Select(chunk => chunk.Text).ToArray(), query: false, token);
						if (vectors.Count != batch.Length) throw new InvalidOperationException("LUSI returned a mismatched embedding count");
						var points = batch.Select((chunk, index) => new SemanticIndexPoint(chunk.Id, vectors[index], chunk.Name, chunk.Text, state.Dataset)).ToArray();
						await LatticeSemanticClient.IndexBatchAsync(points, token);
					}
					var updated = new NaiIndexedDocument(id, relative, length, modified, hash, chunks.Select(chunk => chunk.Id).ToArray(), parts.Sum(part => part.Length),
						text.Length > 24000 || parts.Sum(part => part.Length) < limitedText.Length, ExtractionVersion, DateTimeOffset.UtcNow);
					if (old is not null) foreach (var chunk in old.ChunkIds.Except(updated.ChunkIds, StringComparer.Ordinal)) pending.Add(chunk);
					documents[id] = updated;
					foreach (var chunk in updated.ChunkIds) pending.Remove(chunk);
					state = state with { Documents = documents.Values.ToArray(), PendingDeleteIds = pending.ToArray(), UpdatedAt = DateTimeOffset.UtcNow };
					await Store.SaveAsync(state, token);
					indexed++;
				}
				catch (OperationCanceledException) { throw; }
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException) { failed++; }
				catch (Exception) { failed++; throw; }
				finally { progress?.Report(new(++done, plan.Documents.Length, indexed, cached, skipped, failed, plan.Limited)); }
			}
			foreach (var batch in pending.Chunk(500).ToArray())
			{
				await LatticeSemanticClient.DeleteAsync(batch, token);
				pending.ExceptWith(batch); state = state with { PendingDeleteIds = pending.ToArray() }; await Store.SaveAsync(state, token);
			}
			state = state with { Partial = plan.Limited || failed > 0, UpdatedAt = DateTimeOffset.UtcNow };
			await Store.SaveAsync(state, token);
			return state;
		}

		public static async Task<NaiSemanticSearchResult> SearchAsync(string folder, string query, CancellationToken token)
		{
			folder = NaiSemanticProtocol.CanonicalFolder(folder);
			var state = await Store.ReadAsync(folder, token) ?? throw new InvalidOperationException("NaiSemanticIndexRequired".GetLocalizedResource());
			var eligible = state.Documents.Where(item => state.Recursive || Path.GetDirectoryName(item.RelativePath) is "" or null).ToArray();
			var current = await Task.Run(() => eligible.Where(item => NaiSemanticIndexStore.IsCurrentLocalFile(folder, item)).ToDictionary(item => item.Id, StringComparer.Ordinal), token);
			if (current.Count == 0) return new(Array.Empty<NaiSemanticFileMatch>(), 0, eligible.Length, state.Partial);
			var chunks = current.Values.SelectMany(item => item.ChunkIds).ToArray();
			var queryVectors = await LusiEmbedder.EmbedAsync([query], query: true, token);
			if (queryVectors.Count == 0)
				return new(Array.Empty<NaiSemanticFileMatch>(), current.Count, eligible.Length - current.Count, state.Partial);

			// Lattice searches its shared HNSW store; restrict the hits to this folder's chunks.
			var hits = await LatticeSemanticClient.QueryVectorAsync(queryVectors[0], Math.Min(256, Math.Max(32, chunks.Length)), token);
			var byChunk = new Dictionary<string, NaiIndexedDocument>(StringComparer.Ordinal);
			foreach (var item in current.Values)
				foreach (var chunkId in item.ChunkIds)
					byChunk[chunkId] = item;

			var matches = new List<NaiSemanticFileMatch>();
			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (var hit in hits)
			{
				token.ThrowIfCancellationRequested();
				if (!byChunk.TryGetValue(hit.Id, out var item) || !seen.Add(item.Id) || !NaiSemanticIndexStore.IsCurrentLocalFile(folder, item)) continue;
				if (NaiSemanticIndexStore.ResolvePath(folder, item.RelativePath) is string path)
					matches.Add(new(path, hit.Score, item.RelativePath, item.IndexedAt));
			}
			return new(matches, current.Count, eligible.Length - current.Count, state.Partial);
		}

		public static async Task ForgetAsync(string folder, CancellationToken token)
		{
			using var writer = Store.AcquireWriter(folder);
			var state = await Store.ReadAsync(folder, token); if (state is null) return;
			var ids = state.Documents.SelectMany(item => item.ChunkIds).Concat(state.PendingDeleteIds).Distinct(StringComparer.Ordinal).ToArray();
			// Withdraw the local mapping first, so partial cleanup cannot return old content.
			state = state with { Documents = [], PendingDeleteIds = ids, Partial = true };
			await Store.SaveAsync(state, token);
			foreach (var batch in ids.Chunk(500))
			{
				await LatticeSemanticClient.DeleteAsync(batch, token);
				state = state with { PendingDeleteIds = state.PendingDeleteIds.Except(batch, StringComparer.Ordinal).ToArray() };
				await Store.SaveAsync(state, token);
			}
		}

		private static (List<FileInfo> Files, int Skipped, bool Limited) Enumerate(string folder, bool recursive, CancellationToken token)
		{
			var files = new List<FileInfo>(); var folders = new Stack<DirectoryInfo>(); folders.Push(new(folder));
			int visited = 0, skipped = 0; long bytes = 0; var limited = false;
			while (folders.TryPop(out var directory))
			{
				token.ThrowIfCancellationRequested();
				if (directory.LinkTarget is not null || !NaiThumbnailAvailability.CanReadAttributes((uint)directory.Attributes)) { skipped++; continue; }
				try
				{
					foreach (var entry in directory.EnumerateFileSystemInfos())
					{
						token.ThrowIfCancellationRequested();
						if (++visited > 10000 || files.Count >= 256 || bytes > 512L * 1024 * 1024) return (files, skipped, true);
						if (entry.LinkTarget is not null || !NaiThumbnailAvailability.CanReadAttributes((uint)entry.Attributes)) { skipped++; continue; }
						if (entry is DirectoryInfo child) { if (recursive) folders.Push(child); continue; }
						if (entry is FileInfo file && NaiDocumentReader.Supports(file.FullName) && file.Length <= 32L * 1024 * 1024)
						{
							if (bytes + file.Length > 512L * 1024 * 1024) return (files, skipped + 1, true);
							files.Add(file); bytes += file.Length;
						}
						else skipped++;
					}
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; limited = true; }
			}
			return (files, skipped, limited);
		}

		private static IEnumerable<string> ChunkText(string text)
		{
			var builder = new StringBuilder(); var bytes = 0;
			foreach (var rune in text.EnumerateRunes())
			{
				if (bytes + rune.Utf8SequenceLength > 1800) { var part = builder.ToString(); if (!string.IsNullOrWhiteSpace(part)) yield return part; builder.Clear(); bytes = 0; }
				builder.Append(rune); bytes += rune.Utf8SequenceLength;
			}
			if (builder.Length > 0 && !string.IsNullOrWhiteSpace(builder.ToString())) yield return builder.ToString();
		}
	}
}
