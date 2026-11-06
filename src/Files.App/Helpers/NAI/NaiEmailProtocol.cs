// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Text.Json;

namespace Files.App.Helpers
{
	public sealed class NaiEmailGrant(Uri endpoint, string secret)
	{
		public Uri Endpoint { get; } = endpoint;
		public string Secret { get; } = secret;
		public override string ToString() => "NAI E-Mail Files grant (redacted)";
	}
	public sealed record NaiEmailAttachment(string Name, string MimeType, long? Bytes, DateTimeOffset ReceivedAt, string Link);
	public sealed record NaiEmailMeeting(string Title, DateTimeOffset Start, DateTimeOffset End, bool AllDay, bool HasRecord, string Link);
	public sealed record NaiEmailOverview(DateTimeOffset GeneratedAt, DateTimeOffset DayStart, DateTimeOffset DayEnd, string TimeZone,
		int AccountCount, int TodayUnread, int DueToday, int TodayMeetings, NaiEmailAttachment[] Attachments, NaiEmailMeeting[] Meetings, bool Truncated);
	public static class NaiEmailProtocol
	{
		public const string GrantTarget = "NAI.E-Mail.IntegrationGrant.Files.v1";
		public static NaiEmailGrant ParseGrant(JsonElement root)
		{
			if (root.GetProperty("schemaVersion").GetInt32() != 1 || Text(root, "clientId", 32) != "nai-files") throw new FormatException("Invalid Files grant identity");
			if (!Uri.TryCreate(Text(root, "endpoint", 256), UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1" || endpoint.Port is < 1 or > 65535
				|| endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath != "/files/overview" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0) throw new FormatException("Invalid Files overview endpoint");
			var secret = Text(root, "secret", 64);
			if (secret.Length != 64 || !secret.All(Uri.IsHexDigit)) throw new FormatException("Invalid Files overview credential");
			return new(endpoint, secret);
		}
		public static bool IsSafeLink(string? value)
		{
			if (value is null || value.Length > 16000 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "naiemail" || uri.UserInfo.Length != 0
				|| !uri.IsDefaultPort || uri.Fragment.Length != 0 || uri.AbsolutePath is not ("" or "/")) return false;
			if (uri.Host is "inbox" or "attachments" or "calendar" or "tasks") return uri.Query.Length == 0;
			if (uri.Host != "open") return false;
			var keys = new HashSet<string>(StringComparer.Ordinal);
			foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
			{
				var pair = part.Split('=', 2); if (pair.Length != 2) return false;
				var key = Uri.UnescapeDataString(pair[0].Replace('+', ' ')); var id = Uri.UnescapeDataString(pair[1].Replace('+', ' '));
				if (key is not ("account" or "thread" or "message") || !keys.Add(key) || id.Length is < 1 or > 2048 || id != id.Trim() || id.Any(char.IsControl)) return false;
			}
			return keys.Contains("account") && keys.Contains("thread");
		}
		private static string Text(JsonElement root, string key, int maximum) => root.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String
			&& field.GetString() is { } text && text.Length <= maximum && !text.Any(char.IsControl) ? text : throw new FormatException("Invalid overview text field");
		private static int Count(JsonElement root, string key)
		{
			var count = root.GetProperty(key).GetInt32(); return count >= 0 ? count : throw new FormatException("Invalid overview count");
		}
		private static DateTimeOffset Time(JsonElement root, string key) => DateTimeOffset.FromUnixTimeSeconds(root.GetProperty(key).GetInt64());
		/// <summary>Unix seconds, or milliseconds when the value is clearly past year 2286 in seconds.</summary>
		private static DateTimeOffset UnixTime(JsonElement root, string key)
		{
			var value = root.GetProperty(key).GetInt64();
			return DateTimeOffset.FromUnixTimeSeconds(value > 100_000_000_000 ? value / 1000 : value);
		}
		private static JsonElement.ArrayEnumerator Rows(JsonElement root, string key)
		{
			var rows = root.GetProperty(key);
			if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 50) throw new FormatException("Overview row budget exceeded");
			return rows.EnumerateArray();
		}
		private static string Link(JsonElement row, string key)
		{
			var link = Text(row, key, 16000); return IsSafeLink(link) ? link : throw new FormatException("Invalid owner-app link");
		}
		public static NaiEmailOverview ParseOverview(JsonElement root)
		{
			if (root.GetProperty("schemaVersion").GetInt32() != 1 || Text(root, "source", 64) != "nai-email-local-cache") throw new FormatException("Unknown mail overview source");
			var accounts = Count(root, "approvedAccountCount"); if (accounts > 32) throw new FormatException("Invalid account scope");
			var start = Time(root, "dayStart"); var end = Time(root, "dayEnd");
			if (end <= start || end - start > TimeSpan.FromHours(26)) throw new FormatException("Invalid overview day");
			var attachments = Rows(root, "attachments").Select(row =>
			{
				long? bytes = row.GetProperty("sizeBytes").ValueKind == JsonValueKind.Null ? null : row.GetProperty("sizeBytes").GetInt64();
				if (bytes < 0) throw new FormatException("Invalid attachment size");
				return new NaiEmailAttachment(Text(row, "filename", 256), Text(row, "mimeType", 128), bytes, UnixTime(row, "receivedAt"), Link(row, "mailLink"));
			}).ToArray();
			var meetings = Rows(root, "meetings").Select(row => new NaiEmailMeeting(Text(row, "title", 512), UnixTime(row, "start"), UnixTime(row, "end"),
				row.GetProperty("allDay").GetBoolean(), row.GetProperty("hasMeetingRecord").GetBoolean(), Link(row, "deepLink"))).ToArray();
			if (accounts == 0 && (attachments.Length > 0 || meetings.Length > 0 || Count(root, "todayUnreadInboxThreads") > 0 || Count(root, "dueTodayTasks") > 0 || Count(root, "todayMeetingCount") > 0))
				throw new FormatException("Overview is outside its approved scope");
			return new(root.GetProperty("generatedAt").GetDateTimeOffset(), start, end, Text(root, "timeZone", 100), accounts,
				Count(root, "todayUnreadInboxThreads"), Count(root, "dueTodayTasks"), Count(root, "todayMeetingCount"), attachments, meetings, root.GetProperty("truncated").GetBoolean());
		}
	}
}
