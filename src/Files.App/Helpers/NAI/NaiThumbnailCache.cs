// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Helpers
{
	public static class NaiThumbnailCache
	{
		private sealed record Entry(string Key, byte[]? Bytes, DateTimeOffset Expires);
		private static readonly object gate = new();
		private static readonly Dictionary<string, LinkedListNode<Entry>> entries = new(StringComparer.OrdinalIgnoreCase);
		private static readonly LinkedList<Entry> lru = new();
		private static long bytes;
		private const long ByteLimit = 64 * 1024 * 1024;
		private const int EntryLimit = 2048;

		public static bool TryGet(string key, out byte[]? data)
		{
			lock (gate)
			{
				data = null;
				if (!entries.TryGetValue(key, out var node)) return false;
				if (node.Value.Expires < DateTimeOffset.UtcNow) { Remove(node); return false; }
				lru.Remove(node); lru.AddFirst(node);
				data = node.Value.Bytes;
				return true;
			}
		}

		public static void Put(string key, byte[]? data)
		{
			if (data?.LongLength > ByteLimit / 4) return;
			lock (gate)
			{
				if (entries.TryGetValue(key, out var old)) Remove(old);
				var entry = new Entry(key, data, DateTimeOffset.UtcNow.Add(data is null ? TimeSpan.FromSeconds(3) : TimeSpan.FromMinutes(5)));
				entries[key] = lru.AddFirst(entry); bytes += data?.LongLength ?? 0;
				while (bytes > ByteLimit || entries.Count > EntryLimit) Remove(lru.Last!);
			}
		}

		public static void InvalidatePath(string path)
		{
			lock (gate)
				foreach (var node in entries.Where(pair => pair.Key.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Value).ToArray()) Remove(node);
		}

		private static void Remove(LinkedListNode<Entry> node)
		{
			entries.Remove(node.Value.Key); lru.Remove(node); bytes -= node.Value.Bytes?.LongLength ?? 0;
		}
	}
}
