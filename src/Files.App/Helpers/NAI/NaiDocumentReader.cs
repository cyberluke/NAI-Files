// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Files.App.Helpers
{
	public static class NaiDocumentReader
	{
		public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is
			".md" or ".markdown" or ".txt" or ".csv" or ".json" or ".log" or ".yaml" or ".yml" or ".docx" or ".odt" or ".pptx";

		public static async Task<string> ReadAsync(string path, int maxCharacters, CancellationToken token)
		{
			if (!Supports(path)) throw new NotSupportedException("Unsupported document text format");
			if (maxCharacters < 1 || maxCharacters > 64000) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
			await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
				4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
			var extension = Path.GetExtension(path).ToLowerInvariant();
			if (extension is not (".docx" or ".pptx" or ".odt"))
			{
				using var reader = new StreamReader(file, detectEncodingFromByteOrderMarks: true);
				var buffer = new char[maxCharacters];
				var count = await reader.ReadBlockAsync(buffer.AsMemory(), token);
				return new string(buffer, 0, count);
			}

			// No archive extraction, external entities, macro execution, or embedded
			// links. Bound both decompressed XML and returned text before any AI call.
			if (file.Length > 128 * 1024 * 1024) throw new InvalidDataException("Document container exceeds the extraction limit");
			return await Task.Run(() => ReadOpenXml(file, extension, maxCharacters, token), token);
		}

		private static string ReadOpenXml(Stream file, string extension, int maxCharacters, CancellationToken token)
		{
			using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
			if (archive.Entries.Count > 10000) throw new InvalidDataException("Document has too many archive entries");
			var entries = extension switch
			{
				".docx" => new[] { archive.GetEntry("word/document.xml") },
				".odt" => new[] { archive.GetEntry("content.xml") },
				_ => archive.Entries.Where(entry => entry.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)
					&& entry.FullName.EndsWith(".xml", StringComparison.Ordinal) && entry.FullName.Count(c => c == '/') == 2)
					.OrderBy(entry => entry.FullName.Length).ThenBy(entry => entry.FullName, StringComparer.Ordinal).Take(30).ToArray(),
			};
			var text = new StringBuilder();
			foreach (var entry in entries)
			{
				token.ThrowIfCancellationRequested();
				if (entry is null || text.Length >= maxCharacters) break;
				if (entry.Length > 2 * 1024 * 1024) throw new InvalidDataException("Document XML exceeds the extraction limit");
				using var data = entry.Open();
				using var xml = XmlReader.Create(data, new XmlReaderSettings
				{
					DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024,
				});
				while (text.Length < maxCharacters && xml.Read())
				{
					token.ThrowIfCancellationRequested();
					if (xml.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
						text.Append(xml.Value.AsSpan(0, Math.Min(xml.Value.Length, maxCharacters - text.Length)));
					else if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName is "p" or "h")
						text.Append('\n');
				}
			}
			return text.ToString(0, Math.Min(text.Length, maxCharacters));
		}
	}
}
