// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Files.App.Helpers
{
	/// <summary>
	/// Plug &amp; Play device helpers for NAI telemetry (SetupAPI based, sync, AOT-safe).
	/// </summary>
	internal static partial class NaiPnpDevices
	{
		/// <summary>
		/// Counts physical GPUs per (vendor, device) PCI identity from the Display
		/// class Plug &amp; Play devices. Only devices whose hardware ID is a real
		/// PCI device count — ROOT\ entries (Meta/Oculus virtual screens, VirtualBox,
		/// Microsoft Basic...) do not. Virtual display adapters clone the host GPU's
		/// name and PCI IDs in DXGI, so a DXGI adapter is only physical when the
		/// number of accepted adapters with its PCI identity is below this count.
		/// </summary>
		public static Dictionary<(uint Vendor, uint Device), int> EnumeratePhysicalGpuCounts()
		{
			var counts = new Dictionary<(uint, uint), int>();
			try
			{
				// Enumerate all present devices (the same path as NPU detection —
				// a class-GUID-restricted query proved unreliable here) and only
				// count the Display-class nodes whose hardware ID is a real PCI
				// device. ROOT\ entries (Meta/Oculus virtual screens, VirtualBox,
				// Microsoft Basic...) never match the PCI pattern.
				using var devices = PInvoke.SetupDiGetClassDevs(null, null, HWND.Null,
					SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_PRESENT | SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_ALLCLASSES);
				if (devices.IsInvalid)
					return counts;

				var data = new SP_DEVINFO_DATA { cbSize = (uint)sizeof(SP_DEVINFO_DATA) };
				for (uint index = 0; PInvoke.SetupDiEnumDeviceInfo(devices, index, ref data); index++)
				{
					var className = GetString(devices, ref data, SETUP_DI_REGISTRY_PROPERTY.SPDRP_CLASS);
					if (className is null || !className.Equals("Display", StringComparison.OrdinalIgnoreCase))
						continue;

					var hardwareId = GetString(devices, ref data, SETUP_DI_REGISTRY_PROPERTY.SPDRP_HARDWAREID);
					if (hardwareId is null)
						continue;
					if (PciIdentity().Match(hardwareId) is not { Success: true } match)
						continue;

					var key = (Convert.ToUInt32(match.Groups["vendor"].Value, 16), Convert.ToUInt32(match.Groups["device"].Value, 16));
					counts[key] = counts.GetValueOrDefault(key) + 1;
				}
			}
			catch (Exception ex)
			{
				NaiLanguageBridge.StartupStage("gpu-count: exception " + ex);
			}
			return counts;
		}

		private static readonly Guid DisplayClassGuid = new("{4d36e968-e325-11ce-bfc1-08002be10318}");

		[GeneratedRegex(@"^PCI\\VEN_(?<vendor>[0-9A-F]{4})&DEV_(?<device>[0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
		private static partial Regex PciIdentity();

		/// <summary>
		/// Finds the first present NPU device node — the Compute Accelerator device
		/// class (Intel AI Boost, Qualcomm Hexagon, AMD XDNA...) or a vendor name
		/// marker — and returns its friendly name, or null when no NPU exists.
		/// </summary>
		public static string? FindNpu()
		{
			try
			{
				using var devices = PInvoke.SetupDiGetClassDevs(null, null, HWND.Null,
					SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_PRESENT | SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_ALLCLASSES);
				if (devices.IsInvalid)
					return null;

				var data = new SP_DEVINFO_DATA { cbSize = (uint)sizeof(SP_DEVINFO_DATA) };
				for (uint index = 0; PInvoke.SetupDiEnumDeviceInfo(devices, index, ref data); index++)
				{
					var className = GetString(devices, ref data, SETUP_DI_REGISTRY_PROPERTY.SPDRP_CLASS);
					var name = GetString(devices, ref data, SETUP_DI_REGISTRY_PROPERTY.SPDRP_FRIENDLYNAME)
						?? GetString(devices, ref data, SETUP_DI_REGISTRY_PROPERTY.SPDRP_DEVICEDESC);
					if (name is null)
						continue;

					if (IsNpu(className, name))
						return name;
				}
			}
			catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
			{
			}

			return null;
		}

		private static bool IsNpu(string? className, string name)
		{
			if (className is not null && className.Equals("ComputeAccelerator", StringComparison.OrdinalIgnoreCase))
				return true;

			// Word-boundary match: a bare "npu" also appears inside "input"/"output",
			// which would false-positive on unrelated audio/input devices.
			return NpuNameMarker().IsMatch(name);
		}

		[GeneratedRegex(@"\bnpu\b|\bneural processing\b|\bai boost\b|\bai accelerator\b|\bai engine\b|\bxdna\b|\bhexagon\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
		private static partial Regex NpuNameMarker();

		private static unsafe string? GetString(SafeHandle devices, ref SP_DEVINFO_DATA data, SETUP_DI_REGISTRY_PROPERTY property)
		{
			// Raw pointer call (matching SetupDiGetDeviceRegistryPropertyW) instead of
			// the Span<byte> overload: the wrapper passes null RequiredSize, which some
			// setupapi builds reject, so properties would silently come back empty.
			byte* buffer = stackalloc byte[1024];
			uint type = 0, required = 0;
			if (!PInvoke.SetupDiGetDeviceRegistryProperty(new HDEVINFO(devices.DangerousGetHandle()), (SP_DEVINFO_DATA*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref data), property, &type, buffer, 1024, &required))
				return null;

			var length = (int)Math.Min(required, 1024);
			var text = System.Text.Encoding.Unicode.GetString(buffer, length);
			return text.TrimEnd('\0');
		}
	}
}