// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Text.Json;

namespace Files.App.Helpers
{
	public sealed record NaiRuntimeResource(string Id, string Provider, string Kind, string Model, string State, string Health, DateTimeOffset UpdatedAt);
	public sealed record NaiRuntimeWorkload(string Id, string Capability, string State, string? Provider, string? Device);
	public sealed record NaiRuntimeRelationship(string From, string To, string Kind);
	public sealed record NaiRuntimeSnapshot(DateTimeOffset ObservedAt, NaiRuntimeResource[] Resources, NaiRuntimeWorkload[] Workloads,
		int? Active, int? Queued, NaiRuntimeRelationship[] Relationships, bool Truncated)
	{
		/// <summary>
		/// Composes a Files-shaped runtime snapshot from the canonical LUSI engine's
		/// <c>/v1/health</c> + capabilities map. LUSI exposes a lane inventory rather
		/// than an orchestrator topology; these rows make the canonical engine the
		/// FIRST-CLASS runtime view, keeping the Kelvin <c>files/overview</c> response
		/// (parsed by <see cref="Parse"/>) as the fallback shape.
		/// </summary>
		public static NaiRuntimeSnapshot FromLusi(JsonElement health, string provider)
		{
			var observedAt = DateTimeOffset.UtcNow;
			var lanes = health.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object
				? capabilities : default;
			var resources = new List<NaiRuntimeResource>();
			var workloads = new List<NaiRuntimeWorkload>();
			if (lanes.ValueKind == JsonValueKind.Object)
			{
				string LaneModel(string lane)
				{
					if (!lanes.TryGetProperty(lane, out var entry) || entry.ValueKind != JsonValueKind.Object) return string.Empty;
					return entry.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String ? model.GetString() ?? string.Empty : string.Empty;
				}
				string LaneDevice(string lane)
				{
					if (!lanes.TryGetProperty(lane, out var entry) || entry.ValueKind != JsonValueKind.Object) return string.Empty;
					return entry.TryGetProperty("device", out var device) && device.ValueKind == JsonValueKind.String ? device.GetString() ?? string.Empty : string.Empty;
				}
				foreach (var lane in new[] { "chat", "stt", "ocr", "embeddings", "rerank", "vision" })
				{
					if (!lanes.TryGetProperty(lane, out var entry) || entry.ValueKind != JsonValueKind.Object) continue;
					var available = entry.TryGetProperty("available", out var avail) && avail.ValueKind == JsonValueKind.True;
					var model = LaneModel(lane); var device = LaneDevice(lane);
					resources.Add(new NaiRuntimeResource("lusi-" + lane, provider, lane, model,
						available ? "running" : "down", available ? "healthy" : "unavailable", observedAt));
					if (available) workloads.Add(new NaiRuntimeWorkload("lusi-" + lane, lane, "running", provider, string.IsNullOrEmpty(device) ? null : device));
				}
			}
			if (resources.Count == 0) resources.Add(new NaiRuntimeResource("lusi", provider, "gateway", string.Empty, "unknown", "unknown", observedAt));
			return new(observedAt, resources.ToArray(), workloads.ToArray(), workloads.Count, null, [], false);
		}

		public static NaiRuntimeSnapshot Parse(JsonElement root)
		{
			if (root.GetProperty("schemaVersion").GetInt32() != 1) throw new FormatException("Unsupported runtime snapshot");
			static string Text(JsonElement node, string key) => node.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String
				&& field.GetString() is { Length: <= 256 } value && !value.Any(char.IsControl) ? value : throw new FormatException("Invalid runtime label");
			static string? Optional(JsonElement node, string key) => node.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Null ? null : Text(node, key);
			static JsonElement.ArrayEnumerator Array(JsonElement node, string key, int limit)
			{
				var array = node.GetProperty(key);
				if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > limit) throw new FormatException("Runtime snapshot exceeds its budget");
				return array.EnumerateArray();
			}
			// A single malformed provider row (bad label, control characters in an
			// id) must not blank the whole topology: skip the row instead of
			// failing the snapshot, so the panel still renders every healthy row.
			static T[] Rows<T>(JsonElement node, string key, int limit, Func<JsonElement, T> parse)
			{
				var rows = new List<T>();
				foreach (var row in Array(node, key, limit))
				{
					try { rows.Add(parse(row)); }
					catch (FormatException) { }
				}
				return rows.ToArray();
			}
			var resources = Rows(root, "resources", 512, row => new NaiRuntimeResource(Text(row, "id"), Text(row, "provider"), Text(row, "kind"),
				Optional(row, "modelName") ?? string.Empty, Text(row, "state"), Text(row, "health"), row.GetProperty("updatedAt").GetDateTimeOffset()));
			var relationships = Rows(root, "relationships", 1024, row => new NaiRuntimeRelationship(Text(row, "from"), Text(row, "to"), Text(row, "kind")));
			int? active = null, queued = null; NaiRuntimeWorkload[] work = [];
			if (root.TryGetProperty("workloads", out var workload) && workload.ValueKind != JsonValueKind.Null)
			{
				if (Text(workload, "scope") != "orchestrator") throw new FormatException("Unknown queue scope");
				active = workload.GetProperty("active").GetInt32(); queued = workload.GetProperty("queued").GetInt32();
				if (active < 0 || queued < 0) throw new FormatException("Invalid workload counts");
				work = Rows(workload, "items", 256, row => new NaiRuntimeWorkload(Text(row, "id"), Text(row, "capability"), Text(row, "state"), Optional(row, "provider"), Optional(row, "device")));
			}
			return new(root.GetProperty("observedAt").GetDateTimeOffset(), resources, work, active, queued, relationships, root.GetProperty("truncated").GetBoolean());
		}
	}
	public readonly record struct NaiCpuTimes(ulong Idle, ulong Kernel, ulong User)
	{
		public double? UsageSince(NaiCpuTimes? previous)
		{
			if (previous is not { } prior || Idle < prior.Idle || Kernel < prior.Kernel || User < prior.User) return null;
			var total = (double)(Kernel - prior.Kernel) + (User - prior.User);
			var idle = Idle - prior.Idle;
			return total > 0 && idle <= total ? (total - idle) * 100 / total : null;
		}
	}
}
