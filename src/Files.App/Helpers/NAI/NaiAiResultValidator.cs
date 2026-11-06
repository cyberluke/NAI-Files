// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Files.App.Helpers
{
	public static class NaiAiResultValidator
	{
		private static JsonDocument Parse(string response, JsonValueKind kind)
		{
			if (response.Length > 64000) throw new FormatException("Response too large");
			var json = response.Trim();
			if (json.StartsWith("```", StringComparison.Ordinal))
			{
				var end = json.IndexOf('\n');
				if (end < 0 || !json.EndsWith("```", StringComparison.Ordinal) ||
					json[..end].Trim() is not ("```" or "```json")) throw new FormatException("Invalid JSON fence");
				json = json[(end + 1)..^3].Trim();
			}
			try
			{
				var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
				if (document.RootElement.ValueKind == kind) return document;
				document.Dispose();
				throw new FormatException("Unexpected JSON shape");
			}
			catch (JsonException ex) { throw new FormatException("Invalid JSON response", ex); }
		}

		private static void Keys(JsonElement value, params string[] allowed)
		{
			if (value.ValueKind != JsonValueKind.Object) throw new FormatException("Expected object");
			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (var property in value.EnumerateObject())
				if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
					throw new FormatException("Unknown or duplicate property");
		}

		private static string? Text(JsonElement value, string name, int limit, bool required = false)
		{
			if (!value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null)
			{
				if (required) throw new FormatException("Missing required value");
				return null;
			}
			if (field.ValueKind != JsonValueKind.String) throw new FormatException("Expected string");
			var text = field.GetString()!;
			if (string.IsNullOrWhiteSpace(text) || text.Length > limit || text.Any(char.IsControl)) throw new FormatException("Invalid text value");
			return text;
		}

		private static string Quote(string value)
		{
			if (value.Contains('"') || value.Any(char.IsControl)) throw new FormatException("Unsupported search literal");
			return "\"" + value + "\"";
		}

		public static string BuildSearchQuery(string response)
		{
			using var document = Parse(response, JsonValueKind.Object);
			var root = document.RootElement;
			Keys(root, "nameContains", "extensions", "contentContains", "modifiedAfter", "modifiedBefore");
			var clauses = new List<string>();
			if (Text(root, "nameContains", 200) is string name)
			{
				if (name.IndexOfAny(['*', '?']) >= 0) throw new FormatException("Expected a filename fragment, not a pattern");
				clauses.Add("System.FileName:" + Quote("*" + name + "*"));
			}
			if (Text(root, "contentContains", 200) is string content) clauses.Add("System.Search.Contents:" + Quote(content));
			if (root.TryGetProperty("extensions", out var extensions) && extensions.ValueKind != JsonValueKind.Null)
			{
				if (extensions.ValueKind != JsonValueKind.Array || extensions.GetArrayLength() is < 1 or > 10) throw new FormatException("Invalid extension list");
				var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (var element in extensions.EnumerateArray())
				{
					if (element.ValueKind != JsonValueKind.String) throw new FormatException("Invalid extension");
					var extension = element.GetString()!;
					if (extension.Length is < 1 or > 16 || !extension.All(char.IsAsciiLetterOrDigit)) throw new FormatException("Invalid extension");
					values.Add("System.FileExtension:" + Quote("." + extension.ToLowerInvariant()));
				}
				clauses.Add("(" + string.Join(" OR ", values) + ")");
			}
			DateOnly? after = Date(root, "modifiedAfter"), before = Date(root, "modifiedBefore");
			if (after > before) throw new FormatException("Reversed date range");
			if (after is { } start) clauses.Add("System.DateModified:>=" + start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
			if (before is { } end) clauses.Add("System.DateModified:<=" + end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
			if (clauses.Count == 0) throw new FormatException("Empty search criteria");
			return "$" + string.Join(" AND ", clauses);
		}

		public static string[] ValidateImageQuery(string response)
		{
			using var document = Parse(response, JsonValueKind.Object);
			var root = document.RootElement;
			Keys(root, "englishQueries");
			if (!root.TryGetProperty("englishQueries", out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() is < 1 or > 3)
				throw new FormatException("Expected one to three query translations");
			var queries = new List<string>();
			foreach (var value in values.EnumerateArray())
			{
				if (value.ValueKind != JsonValueKind.String) throw new FormatException("Expected a query string");
				var query = value.GetString()!.Trim();
				if (query.Length is < 1 or > 256 || query.Any(char.IsControl)) throw new FormatException("Invalid query translation");
				queries.Add(query);
			}
			return queries.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		}

		private static DateOnly? Date(JsonElement root, string key)
		{
			var text = Text(root, key, 10);
			if (text is null) return null;
			return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
				? date : throw new FormatException("Invalid date");
		}

		private static void SafeName(string name)
		{
			if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.') ||
				name.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 || name.Any(char.IsControl)) throw new FormatException("Unsafe Windows name");
			var stem = name.Split('.')[0].TrimEnd(' ', '.').ToUpperInvariant();
			if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
				(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3])))
				throw new FormatException("Reserved Windows name");
		}

		private static IEnumerable<JsonElement> Items(JsonDocument document)
		{
			if (document.RootElement.GetArrayLength() is < 1 or > 100) throw new FormatException("Empty or oversized proposal");
			return document.RootElement.EnumerateArray();
		}

		private static string Source(JsonElement item, IReadOnlyCollection<string> names, HashSet<string> seen)
		{
			var source = Text(item, "source", 240, required: true)!;
			var actual = names.FirstOrDefault(name => name.Equals(source, StringComparison.OrdinalIgnoreCase));
			if (actual is null || !seen.Add(actual)) throw new FormatException("Unknown or repeated source file");
			return actual;
		}

		public static string ValidateRename(string response, IReadOnlyCollection<string> sourceNames, IReadOnlyCollection<string> occupiedNames, string? evidence = null)
		{
			using var document = Parse(response, JsonValueKind.Array);
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var lines = new List<string>();
			foreach (var item in Items(document))
			{
				Keys(item, "source", "proposedName", "reason");
				var source = Source(item, sourceNames, seen);
				var proposed = Text(item, "proposedName", 240, required: true)!;
				SafeName(proposed);
				if (evidence is not null)
					foreach (Match year in Regex.Matches(proposed, @"(?<!\d)(?:19|20|21)\d{2}(?!\d)", RegexOptions.CultureInvariant))
						if (!evidence.Contains(year.Value, StringComparison.Ordinal)) throw new FormatException("Unsupported year in suggested name");
				if (!Path.GetExtension(source).Equals(Path.GetExtension(proposed), StringComparison.OrdinalIgnoreCase) ||
					source.Equals(proposed, StringComparison.OrdinalIgnoreCase) || occupiedNames.Contains(proposed, StringComparer.OrdinalIgnoreCase) || !destinations.Add(proposed))
					throw new FormatException("Unchanged name, collision, or extension change");
				var reason = Text(item, "reason", 300);
				lines.Add(source + " → " + proposed + (reason is null ? string.Empty : "\n" + reason));
			}
			return string.Join("\n\n", lines);
		}

		public static string ValidateOrganization(string response, IReadOnlyCollection<string> fileNames, IReadOnlyCollection<string> folderNames)
		{
			using var document = Parse(response, JsonValueKind.Array);
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var lines = new List<string>();
			foreach (var item in Items(document))
			{
				Keys(item, "source", "destinationFolder");
				var source = Source(item, fileNames, seen);
				var destination = Text(item, "destinationFolder", 240, required: true)!;
				var parts = destination.Replace('\\', '/').Split('/');
				foreach (var part in parts) SafeName(part);
				if (fileNames.Contains(parts[0], StringComparer.OrdinalIgnoreCase)) throw new FormatException("Folder conflicts with an existing file");
				// Existing folders can be destinations, but their unread contents are
				// never inferred or presented as facts by this renderer.
				lines.Add(source + " → " + string.Join("/", parts) + "/");
			}
			return string.Join("\n", lines);
		}

		public static string ValidateTags(string response, IReadOnlyCollection<string> sourceNames)
		{
			using var document = Parse(response, JsonValueKind.Array);
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var lines = new List<string>();
			foreach (var item in Items(document))
			{
				Keys(item, "source", "tags");
				var source = Source(item, sourceNames, seen);
				if (!item.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array || tags.GetArrayLength() is < 1 or > 8) throw new FormatException("Invalid tags");
				var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (var tag in tags.EnumerateArray())
				{
					if (tag.ValueKind != JsonValueKind.String) throw new FormatException("Invalid tag");
					var text = tag.GetString()!;
					if (string.IsNullOrWhiteSpace(text) || text.Length > 60 || text.Any(char.IsControl)) throw new FormatException("Invalid tag");
					values.Add(text);
				}
				lines.Add(source + ": " + string.Join(", ", values));
			}
			return string.Join("\n", lines);
		}

		public static string ValidateTranslation(string response, IReadOnlyDictionary<string, string> excerpts)
		{
			using var document = Parse(response, JsonValueKind.Array);
			if (excerpts.Count == 0 || document.RootElement.GetArrayLength() != excerpts.Count)
				throw new FormatException("Translation must cover every selected excerpt");
			var names = excerpts.Keys.ToArray();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var lines = new List<string>();
			foreach (var item in Items(document))
			{
				Keys(item, "source", "translation");
				var source = Source(item, names, seen);
				if (!item.TryGetProperty("translation", out var value) || value.ValueKind != JsonValueKind.String)
					throw new FormatException("Missing translation text");
				var translation = value.GetString()!;
				if (string.IsNullOrWhiteSpace(translation) || translation.Length > 32000 || translation.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
					throw new FormatException("Invalid translation text");
				// The model is instructed to retain original numeric notation. This
				// rejects silently omitted budgets/dates rather than claiming a full
				// translation solely because some text was returned.
				var numbers = Regex.Matches(translation, @"\d+(?:[.,]\d+)*", RegexOptions.CultureInvariant)
					.Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
				foreach (Match number in Regex.Matches(excerpts[source], @"\d+(?:[.,]\d+)*", RegexOptions.CultureInvariant))
					if (!numbers.Contains(number.Value)) throw new FormatException("Translation omitted an original numeric value");
				lines.Add(source + "\n" + translation);
			}
			return string.Join("\n\n", lines);
		}
	}
}
