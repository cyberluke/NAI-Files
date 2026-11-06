// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Services.Pulse
{
	/// <summary>
	/// Spectral waveform of one audio file, one column per <see cref="Columns"/>: the peak amplitude of the
	/// slice and the energy split into three bands, the way Traktor paints its coloured waveforms.
	/// Stored compactly (bytes) so the on-disk cache stays small.
	/// </summary>
	/// <param name="Columns">Number of time slices.</param>
	/// <param name="Peaks">Per-column peak amplitude, 0..255.</param>
	/// <param name="Low">Per-column low-band (bass) share, 0..255.</param>
	/// <param name="Mid">Per-column mid-band share, 0..255.</param>
	/// <param name="High">Per-column high-band share, 0..255.</param>
	/// <param name="Duration">Decoded duration, used as a secondary validity check.</param>
	public sealed record PulseWaveform(int Columns, byte[] Peaks, byte[] Low, byte[] Mid, byte[] High, TimeSpan Duration)
	{
		public const int DefaultColumns = 256;
	}
}
