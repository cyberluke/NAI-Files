// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dxgi;

namespace Files.App.Helpers
{
	public sealed record NaiEngineUtilization(string Adapter, double Percent);
	public static partial class NaiEngineCounters
	{
		[GeneratedRegex(@"^(?:pid_\d+_)?(?<adapter>luid_0x[0-9a-f]+_0x[0-9a-f]+_phys_\d+)_eng_(?<engine>\d+)_engtype_.*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
		private static partial Regex CounterName();

		[GeneratedRegex(@"^luid_0x([0-9a-f]{8})_0x([0-9a-f]{8})_phys_\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
		private static partial Regex AdapterName();

		/// <summary>
		/// Physical GPU adapter LUIDs with their DXGI display names, resolved at most
		/// once per OS boot (the result is cached in LocalState and shared across
		/// Files instances). Virtual/indirect display adapters (Oculus/Meta virtual
		/// monitors, VirtualBox, Microsoft Basic...) are excluded so the panel
		/// reports real hardware only. Null when DXGI enumeration failed; callers
		/// then keep every engine as a graceful fallback.
		/// </summary>
		private static readonly Lazy<Dictionary<ulong, string>?> physicalAdapters = new(ResolvePhysicalAdapters, LazyThreadSafetyMode.ExecutionAndPublication);

		public static NaiEngineUtilization[] Aggregate(IEnumerable<(string Name, double Percent)> samples)
			=> Aggregate(samples, physicalAdapters.Value);

		/// <summary>Hermetic aggregation for tests: the physical-adapter table is
		/// injected instead of resolved from DXGI, so synthetic LUIDs are not
		/// filtered by the machine's real hardware.</summary>
		internal static NaiEngineUtilization[] Aggregate(IEnumerable<(string Name, double Percent)> samples, Dictionary<ulong, string>? table)
		{
			var engines = new Dictionary<(string Adapter, string Engine), double>();
			foreach (var (name, percent) in samples)
			{
				if (!double.IsFinite(percent) || percent < 0 || name.Length > 512) continue;
				var match = CounterName().Match(name);
				if (!match.Success) continue;
				var key = (match.Groups["adapter"].Value.ToLowerInvariant(), match.Groups["engine"].Value);
				engines[key] = engines.GetValueOrDefault(key) + percent;
			}

			// Process instances share physical engines. Sum within an engine, then
			// use the busiest engine for the adapter, never sum different engines.
			return engines.GroupBy(entry => entry.Key.Adapter).OrderBy(group => group.Key, StringComparer.Ordinal)
				.Where(group => table is null || AdapterLuid(group.Key) is ulong luid && table.ContainsKey(luid))
				.Select(group => new NaiEngineUtilization(ResolveAdapterName(table, group.Key), Math.Min(100, group.Max(entry => entry.Value))))
				.ToArray();
		}

		private static Dictionary<ulong, string>? ResolvePhysicalAdapters()
		{
			// Reuse the per-boot cached table when available; only the first Files
			// instance after an OS boot pays for the DXGI enumeration.
			if (NaiHardwareCache.LoadCurrent() is { Gpus.Length: > 0 } cached)
				return cached.Gpus.ToDictionary(entry => entry.Luid, entry => entry.Name);

			var table = BuildPhysicalAdapters();
			if (table is { Count: > 0 })
				NaiHardwareCache.Save(gpus: table.Select(entry => new NaiHardwareCache.GpuEntry(entry.Key, entry.Value)).ToArray());
			return table;
		}

		private static ulong? AdapterLuid(string adapter)
		{
			var match = AdapterName().Match(adapter);
			if (!match.Success) return null;
			return (ulong)Convert.ToUInt32(match.Groups[1].Value, 16) << 32 | Convert.ToUInt32(match.Groups[2].Value, 16);
		}

		private static string ResolveAdapterName(Dictionary<ulong, string>? table, string adapter)
		{
			if (table is not null && AdapterLuid(adapter) is ulong luid && table.TryGetValue(luid, out var name))
				return name;
			return adapter;
		}

		private static Dictionary<ulong, string>? BuildPhysicalAdapters()
		{
			try
			{
				var table = new Dictionary<ulong, string>();
				var hr = PInvoke.CreateDXGIFactory1(out IDXGIFactory1 factory);
				if (hr.Failed)
					return table;

				// Virtual display adapters clone the physical GPU's description AND
				// PCI identity in DXGI (and expose no outputs of their own), so the
				// only reliable discriminator is the Plug & Play device table: a
				// DXGI adapter with a given PCI identity is physical only while the
				// number of accepted adapters with that identity is below the count
				// of physical (PCI\...) devices in the Display class.
				var physicalCounts = NaiPnpDevices.EnumeratePhysicalGpuCounts();
				var accepted = new Dictionary<(uint Vendor, uint Device), int>();
				try
				{
					for (uint index = 0; ; index++)
					{
						hr = factory.EnumAdapters1(index, out IDXGIAdapter1 adapter);
						if (hr.Failed)
							break;
						try
						{
							if (adapter.GetDesc1(out var desc).Failed)
								continue;

							var name = desc.Description.ToString();
							if (IsVirtualAdapter(name, desc.Flags))
								continue;

							var key = (desc.VendorId, desc.DeviceId);
							if (accepted.GetValueOrDefault(key) >= physicalCounts.GetValueOrDefault(key))
								continue;
							accepted[key] = accepted.GetValueOrDefault(key) + 1;

							table[LuidValue(desc.AdapterLuid)] = name;
						}
						finally
						{
							Release(adapter);
						}
					}
				}
				finally
				{
					Release(factory);
				}

				return table;
			}
			catch
			{
				return null;
			}
		}

		private static ulong LuidValue(LUID luid) => (ulong)(uint)luid.HighPart << 32 | luid.LowPart;

		private static bool IsVirtualAdapter(string name, DXGI_ADAPTER_FLAG flags)
		{
			if (flags.HasFlag(DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE))
				return true;

			// Indirect display / virtual monitors (Oculus, Meta, VirtualBox, ...)
			// and the Microsoft Basic fallback adapter carry these markers.
			var lower = name.ToLowerInvariant();
			return lower.Contains("virtual")
				|| lower.Contains("indirect")
				|| lower.Contains("oculus")
				|| lower.Contains("meta ")
				|| lower.Contains("microsoft basic")
				|| lower.Contains("basic display")
				|| lower.Contains("basic render")
				|| lower.Contains("remote")
				|| lower.Contains("paravirtual");
		}

		private static void Release(object comObject)
		{
			try
			{
				Marshal.ReleaseComObject(comObject);
			}
			catch
			{
				// Not an RCW (already released); nothing to clean up.
			}
		}
	}
}