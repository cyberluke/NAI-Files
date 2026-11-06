// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Storage;

namespace Files.App.Helpers
{
	[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
	[JsonSerializable(typeof(NaiHardwareCache.HardwareCache))]
	internal sealed partial class NaiHardwareCacheJsonContext : JsonSerializerContext
	{
	}

	/// <summary>
	/// Machine-level cache for hardware detection results (physical GPU LUIDs and
	/// NPU presence). Detection runs at most once per OS boot: the first Files
	/// instance after boot detects and stores the result, every later instance
	/// (and every later panel sample) reuses the cached table for the session.
	/// </summary>
	internal static class NaiHardwareCache
	{
		private const string FileName = "nai-hardware-cache.json";

		// Bump when the detection logic changes so stale caches are re-detected.
		private const int FormatVersion = 4;

		// Two minutes of tolerance: instances starting right after boot may race on
		// the first write; the boot tick itself is monotonic, so anything within a
		// couple of minutes is the same boot session.
		private const long BootToleranceMs = 120_000;

		public sealed record GpuEntry(ulong Luid, string Name);
		public sealed record HardwareCache(int Version, long BootTick, GpuEntry[] Gpus, string? Npu)
		{
			public static HardwareCache Empty(long bootTick) => new(FormatVersion, bootTick, [], null);
		}

		private static string CachePath => Path.Combine(ApplicationData.Current.LocalFolder.Path, FileName);

		/// <summary>
		/// Loads the cached hardware table when it was produced by the current
		/// format in the current OS boot session; null when missing, stale
		/// (previous boot or old format) or corrupt.
		/// </summary>
		public static HardwareCache? LoadCurrent()
		{
			try
			{
				if (!File.Exists(CachePath))
					return null;
				var cache = JsonSerializer.Deserialize(File.ReadAllText(CachePath), NaiHardwareCacheJsonContext.Default.HardwareCache);
				if (cache is null || cache.Version != FormatVersion)
					return null;
				return IsCurrentBoot(cache.BootTick) ? cache : null;
			}
			catch
			{
				return null;
			}
		}

		public static bool IsCurrentBoot(long bootTick) => Math.Abs(bootTick - Environment.TickCount64) <= BootToleranceMs;

		/// <summary>
		/// Atomically persists a detection result. Merging happens only against a
		/// cache from the current boot session, so the GPU and NPU halves written
		/// by different components do not overwrite each other — while a stale
		/// (previous boot) cache is never carried forward.
		/// </summary>
		public static void Save(GpuEntry[]? gpus = null, string? npu = null)
		{
			try
			{
				var existing = LoadCurrent();
				var merged = new HardwareCache(
					FormatVersion,
					Environment.TickCount64,
					gpus ?? existing?.Gpus ?? [],
					npu ?? existing?.Npu);

				var temp = CachePath + ".tmp";
				File.WriteAllText(temp, JsonSerializer.Serialize(merged, NaiHardwareCacheJsonContext.Default.HardwareCache));
				File.Move(temp, CachePath, overwrite: true);
			}
			catch (Exception ex)
			{
				NaiLanguageBridge.StartupStage("hw-cache-save-failed: " + ex.GetType().Name + " " + ex.Message);
			}
		}
	}
}