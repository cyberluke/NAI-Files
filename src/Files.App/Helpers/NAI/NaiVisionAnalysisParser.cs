// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Text.Json;

namespace Files.App.Helpers
{
	public static class NaiVisionAnalysisParser
	{
		public static NaiImageAnalysis Apply(string response, NaiImageAnalysis identity)
		{
			if (response.Length > 24000) throw new FormatException("Vision response exceeds its limit");
			var json = response.Trim();
			if (json.StartsWith("```", StringComparison.Ordinal) && json.EndsWith("```", StringComparison.Ordinal))
			{
				var newline = json.IndexOf('\n');
				if (newline < 0) throw new FormatException("Invalid vision JSON");
				json = json[(newline + 1)..^3].Trim();
			}
			try
			{
				using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
				var root = document.RootElement;
				if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Expected vision object");
				return identity with
				{
					Caption = Text(root, "caption", 1600, true), EnglishCaption = Text(root, "englishCaption", 1600, true),
					Tags = Tags(root, "tags"), EnglishTags = Tags(root, "englishTags"), Text = Text(root, "text", 4000, false),
				};
			}
			catch (JsonException ex) { throw new FormatException("Invalid vision JSON", ex); }
		}

		private static string Text(JsonElement root, string key, int limit, bool required)
		{
			if (!root.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
			{
				if (required) throw new FormatException("Missing image caption");
				return string.Empty;
			}
			if (value.ValueKind != JsonValueKind.String) throw new FormatException("Invalid image text");
			var text = value.GetString()!.Trim();
			if (text.Length > limit || required && text.Length == 0) throw new FormatException("Invalid image text length");
			return text;
		}

		private static string[] Tags(JsonElement root, string key)
		{
			if (!root.TryGetProperty(key, out var tags) || tags.ValueKind != JsonValueKind.Array || tags.GetArrayLength() > 12)
				throw new FormatException("Invalid image tags");
			var result = new List<string>();
			foreach (var tag in tags.EnumerateArray())
			{
				if (tag.ValueKind != JsonValueKind.String) throw new FormatException("Invalid image tag");
				var text = tag.GetString()!.Trim();
				if (text.Length is < 1 or > 80 || text.Any(char.IsControl)) throw new FormatException("Invalid image tag length");
				result.Add(text);
			}
			return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		}
	}
}
