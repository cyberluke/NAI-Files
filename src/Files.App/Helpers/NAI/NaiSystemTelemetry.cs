// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using Windows.Networking.Connectivity;
using Windows.Win32;
using Windows.Win32.System.SystemInformation;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace Files.App.Helpers
{
	internal sealed record NaiNetworkInfo(bool IsWlan, byte? Bars, string? Name);
	internal sealed record NaiSystemSample(DateTimeOffset At, double? Cpu, ulong? RamUsed, ulong? RamTotal, long? DiskFree, long? DiskTotal, NaiNetworkInfo? Network, NaiEngineUtilization[] Gpu, NaiEngineUtilization[] Npu);

	/// <summary>
	/// Process-wide hardware sampler: one PDH query and one sampling loop shared
	/// by every window's resources panel. The loop runs only while at least one
	/// panel is subscribed (i.e. the section is expanded) and samples at 30 s —
	/// never per panel, never while everything is collapsed.
	/// </summary>
	internal sealed partial class NaiSystemTelemetry
	{
		private static readonly object sync = new();
		private static NaiSystemTelemetry? shared;
		private static int subscribers;

		public static NaiSystemTelemetry Shared
		{
			get { lock (sync) return shared ??= new NaiSystemTelemetry(); }
		}

		public event Action<NaiSystemSample>? Sampled;

		private readonly NaiWindowsEngineSampler engines = new();
		private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;
		private NaiCpuTimes? previous;
		private bool active;

		private NaiSystemTelemetry()
		{
			// UI-dispatcher timer: unlike a thread-pool timer it is guaranteed to
			// keep firing while the app is alive, and the sample itself runs on a
			// worker thread (Task.Run) so the UI thread is never blocked.
			timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
			timer.Interval = TimeSpan.FromSeconds(30);
			timer.Tick += async (_, _) => await RunSampleAsync();
		}

		/// <summary>Adds one consumer and starts the shared loop (with an immediate
		/// first sample) if it is not running yet.</summary>
		public void Start()
		{
			lock (sync)
			{
				subscribers++;
				if (!active)
				{
					active = true;
					timer.Start();
					_ = RunSampleAsync();
				}
			}
		}

		/// <summary>Removes one consumer; the loop stops when the last consumer
		/// (the last expanded panel) goes away.</summary>
		public void Stop()
		{
			lock (sync)
			{
				subscribers = Math.Max(0, subscribers - 1);
				if (subscribers == 0 && active)
				{
					active = false;
					timer.Stop();
					previous = null;
				}
			}
		}

		private async Task RunSampleAsync()
		{
			try
			{
				var sample = await Task.Run(Sample);
				Sampled?.Invoke(sample);
			}
			catch (Exception ex)
			{
				var text = ex.ToString();
				NaiLanguageBridge.StartupStage("sampler-tick-fail: " + text[..Math.Min(text.Length, 2000)]);
			}
		}

		public unsafe NaiSystemSample Sample()
		{
			double? cpu = null; ulong? ramUsed = null, ramTotal = null; long? diskFree = null, diskTotal = null; NaiNetworkInfo? network = null;
			FILETIME idle, kernel, user;
			if (PInvoke.GetSystemTimes(out idle, out kernel, out user))
			{
				static ulong Value(FILETIME time) => ((ulong)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
				var now = new NaiCpuTimes(Value(idle), Value(kernel), Value(user)); cpu = now.UsageSince(previous); previous = now;
			}
			else previous = null;
			var memory = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
			if (PInvoke.GlobalMemoryStatusEx(&memory) && memory.ullTotalPhys >= memory.ullAvailPhys)
			{
				ramTotal = memory.ullTotalPhys; ramUsed = memory.ullTotalPhys - memory.ullAvailPhys;
			}
			try
			{
				if (Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) is string root)
				{
					var drive = new DriveInfo(root); if (drive.IsReady) { diskFree = drive.AvailableFreeSpace; diskTotal = drive.TotalSize; }
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
			try
			{
				// Report the active connection: signal bars for Wi-Fi, the profile
				// name (or adapter type) for wired/WWAN — never just "unavailable"
				// while the machine is connected over Ethernet.
				var profile = NetworkInformation.GetInternetConnectionProfile();
				if (profile is not null)
				{
					var isWlan = profile.IsWlanConnectionProfile;
					byte? bars = isWlan ? profile.GetSignalBars() : null;
					string? name = string.IsNullOrWhiteSpace(profile.ProfileName) ? null : profile.ProfileName;
					network = new(isWlan, bars, name);
				}
			}
			catch (System.Runtime.InteropServices.COMException) { }
			var devices = engines.Sample();
			return new(DateTimeOffset.UtcNow, cpu, ramUsed, ramTotal, diskFree, diskTotal, network, devices.Gpu, devices.Npu);
		}
	}
}