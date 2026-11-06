// Copyright (c) Files Community
// Licensed under the MIT License.

using Windows.Win32;
using Windows.Win32.System.Performance;

namespace Files.App.Helpers
{
	internal sealed unsafe partial class NaiWindowsEngineSampler : IDisposable
	{
		private readonly object gate = new();
		private PdhCloseQuerySafeHandle? query;
		private PDH_HCOUNTER gpu, npu;
		private bool initialized, primed, disposed;

		// Many NPU drivers do not publish the "NPU Engine" PDH counter set; the
		// device node is still reported so the panel detects the NPU regardless.
		// Resolution is cached per OS boot — the SetupAPI device scan (hundreds of
		// nodes) runs only in the first Files instance after boot.
		private static readonly string? npuDeviceName = ResolveNpu();

		private static string? ResolveNpu()
		{
			if (NaiHardwareCache.LoadCurrent() is { Npu: not null } cached)
				return cached.Npu;

			var npu = NaiPnpDevices.FindNpu();
			NaiHardwareCache.Save(npu: npu);
			return npu;
		}

		public (NaiEngineUtilization[] Gpu, NaiEngineUtilization[] Npu) Sample()
		{
			lock (gate)
			{
				if (disposed) return ([], []);
				if (!initialized)
				{
					initialized = true;
					if (PInvoke.PdhOpenQuery(null!, 0, out query) != 0) { query?.Dispose(); query = null; return ([], []); }
					if (PInvoke.PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", 0, out gpu) != 0) gpu = default;
					if (PInvoke.PdhAddEnglishCounter(query, @"\NPU Engine(*)\Utilization Percentage", 0, out npu) != 0) npu = default;
				}
				if (query is null || query.IsInvalid || PInvoke.PdhCollectQueryData((PDH_HQUERY)query.DangerousGetHandle()) != 0)
				{
					primed = false; return ([], []);
				}
				if (!primed) { primed = true; return ([], []); }
				var gpuEngines = Read(gpu);
				var npuEngines = Read(npu);
				if (npuEngines.Length == 0 && npuDeviceName is { } name)
					npuEngines = [new NaiEngineUtilization(name, double.NaN)];
				return (gpuEngines, npuEngines);
			}
		}
		private static NaiEngineUtilization[] Read(PDH_HCOUNTER counter)
		{
			if (counter.IsNull) return [];
			const uint MoreData = 0x800007D2;
			for (var attempt = 0; attempt < 2; attempt++)
			{
				uint bytes = 0, count = 0;
				var status = PInvoke.PdhGetFormattedCounterArray(counter, PDH_FMT.PDH_FMT_DOUBLE, &bytes, &count, null);
				if (status != MoreData || bytes is 0 or > 8 * 1024 * 1024 || count > 32768) return [];
				var buffer = new byte[bytes];
				fixed (byte* pointer = buffer)
				{
					status = PInvoke.PdhGetFormattedCounterArray(counter, PDH_FMT.PDH_FMT_DOUBLE, &bytes, &count, (PDH_FMT_COUNTERVALUE_ITEM_W*)pointer);
					if (status == MoreData) continue;
					if (status != 0 || count > 32768 || (ulong)count * (uint)sizeof(PDH_FMT_COUNTERVALUE_ITEM_W) > (ulong)buffer.Length) return [];
					var values = new List<(string Name, double Percent)>();
					var items = (PDH_FMT_COUNTERVALUE_ITEM_W*)pointer;
					for (var index = 0; index < count; index++)
					{
						var item = items[index];
						if (item.FmtValue.CStatus <= 1 && item.szName.ToString() is string name) values.Add((name, item.FmtValue.Anonymous.doubleValue));
					}
					return NaiEngineCounters.Aggregate(values);
				}
			}
			return [];
		}
		public void Dispose()
		{
			lock (gate) { disposed = true; query?.Dispose(); query = null; }
		}
	}
}
