// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Files.App.Helpers
{
	public sealed record NaiSavedWorkspace(string Name, string[] Tabs, int SelectedIndex, DateTimeOffset SavedAt);
	public sealed record NaiSearchLens(string Name, string Folder, bool AllCached, string Query, string[] Alternatives, string? Tag, string Model, string Language, DateTimeOffset SavedAt);
	public sealed record NaiWorkspaceState(int SchemaVersion, NaiSavedWorkspace[] Workspaces, NaiSearchLens[] Lenses);
	[JsonSerializable(typeof(NaiWorkspaceState))]
	public partial class NaiWorkspaceJsonContext : JsonSerializerContext;

	public sealed class NaiWorkspaceStore(string path)
	{
		private static readonly SemaphoreSlim gate = new(1, 1);
		private const int MaximumBytes = 1024 * 1024;
		public async Task<NaiWorkspaceState> ReadAsync(CancellationToken token)
		{
			await gate.WaitAsync(token);
			try { return await ReadCoreAsync(token); }
			finally { gate.Release(); }
		}
		private async Task<NaiWorkspaceState> ReadCoreAsync(CancellationToken token)
		{
			if (!File.Exists(path)) return new(1, [], []);
			if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("Workspace state exceeds its budget");
			await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
			using var reader = new StreamReader(stream, Encoding.UTF8);
			var value = JsonSerializer.Deserialize(await reader.ReadToEndAsync(token), NaiWorkspaceJsonContext.Default.NaiWorkspaceState);
			Validate(value);
			return value!;
		}
		private static void Validate(NaiWorkspaceState? value)
		{
			static bool Text(string? text, int max, bool allowEmpty = false) => text is not null && text.Length <= max && (allowEmpty || !string.IsNullOrWhiteSpace(text)) && !text.Any(char.IsControl);
			if (value is null || value.SchemaVersion != 1 || value.Workspaces is null || value.Lenses is null || value.Workspaces.Length > 32 || value.Lenses.Length > 32)
				throw new InvalidDataException("Invalid workspace state");
			foreach (var workspace in value.Workspaces)
				if (workspace is null || !Text(workspace.Name, 80) || workspace.Tabs is null || workspace.Tabs.Length is < 1 or > 24 || workspace.Tabs.Any(tab => tab is null || tab.Length is < 1 or > 32768)
					|| workspace.SelectedIndex < 0 || workspace.SelectedIndex >= workspace.Tabs.Length) throw new InvalidDataException("Invalid saved workspace");
			foreach (var lens in value.Lenses)
				if (lens is null || !Text(lens.Name, 80) || !Text(lens.Folder, 4096, lens.AllCached) || !Text(lens.Query, 256, true) || lens.Alternatives is null
					|| lens.Alternatives.Length > 3 || lens.Alternatives.Any(query => !Text(query, 256)) || !Text(lens.Model, 256, true) || !Text(lens.Language, 40, true)
					|| lens.Tag is not null && !Text(lens.Tag, 120)) throw new InvalidDataException("Invalid saved image search");
		}
		public Task SaveWorkspaceAsync(NaiSavedWorkspace workspace, CancellationToken token) => UpdateAsync(state => state with
		{
			Workspaces = new[] { workspace }.Concat(state.Workspaces.Where(item => !item.Name.Equals(workspace.Name, StringComparison.OrdinalIgnoreCase))).ToArray(),
		}, token);
		public Task SaveLensAsync(NaiSearchLens lens, CancellationToken token) => UpdateAsync(state => state with
		{
			Lenses = new[] { lens }.Concat(state.Lenses.Where(item => !item.Name.Equals(lens.Name, StringComparison.OrdinalIgnoreCase))).ToArray(),
		}, token);
		public Task DeleteWorkspaceAsync(string name, CancellationToken token) => UpdateAsync(state => state with { Workspaces = state.Workspaces.Where(item => item.Name != name).ToArray() }, token);
		public Task DeleteLensAsync(string name, CancellationToken token) => UpdateAsync(state => state with { Lenses = state.Lenses.Where(item => item.Name != name).ToArray() }, token);
		private async Task UpdateAsync(Func<NaiWorkspaceState, NaiWorkspaceState> update, CancellationToken token)
		{
			await gate.WaitAsync(token);
			try
			{
				await Task.Run(() =>
				{
					var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
					using var mutex = new Mutex(false, "Local\\NAI-Files-Workspace-" + key[..24]);
					var acquired = false;
					try
					{
						try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(3)); } catch (AbandonedMutexException) { acquired = true; }
						if (!acquired) throw new IOException("Workspace state is busy");
						token.ThrowIfCancellationRequested();
						// A named mutex, like document history, preserves edits from other
						// Files processes. Its ownership stays on this worker thread.
						var next = update(ReadCoreAsync(token).GetAwaiter().GetResult()); Validate(next);
						var json = JsonSerializer.Serialize(next, NaiWorkspaceJsonContext.Default.NaiWorkspaceState);
						if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("Workspace state exceeds its budget");
						Directory.CreateDirectory(Path.GetDirectoryName(path)!);
						var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
						try { File.WriteAllText(temporary, json, new UTF8Encoding(false)); token.ThrowIfCancellationRequested(); File.Move(temporary, path, true); }
						finally { if (File.Exists(temporary)) File.Delete(temporary); }
					}
					finally { if (acquired) mutex.ReleaseMutex(); }
				}, token);
			}
			finally { gate.Release(); }
		}
	}
}
