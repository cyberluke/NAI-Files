// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Text.Json;

namespace Files.App.Helpers
{
	public static class NaiModelCatalog
	{
		public static bool Supports(JsonElement model, string operation)
		{
			// Older gateways mislabeled every model (including Whisper) as chat.
			// Require the typed projection rather than guessing from a display name.
			if (model.ValueKind != JsonValueKind.Object ||
				!model.TryGetProperty("capability_schema_version", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1 ||
				!model.TryGetProperty("api_type", out var api) || api.ValueKind != JsonValueKind.String || api.GetString() != "chat.completions" ||
				!model.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Array)
				return false;
			return capabilities.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == operation);
		}
	}
}
