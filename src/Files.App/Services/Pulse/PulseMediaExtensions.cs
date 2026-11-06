// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.Frozen;

namespace Files.App.Services.Pulse
{
	/// <summary>
	/// Media extension sets mirroring what the bundled FFmpeg 9 toolset can demux. The Pulse
	/// classifier falls back to these so ANY ffmpeg-readable audio/video container gets a media
	/// card (waveform, duration badge, trim/extract/convert buttons) even when the shell's
	/// built-in media type lists do not know it (e.g. .dsf, .ape, .wv, .m2ts, .ogv, .3gp, .mxf).
	/// Extension checks are allocation-free via frozen hash sets.
	/// </summary>
	public static class PulseMediaExtensions
	{
		private static readonly FrozenSet<string> audioExtensions =
			new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				// Common
				".mp3", ".wav", ".flac", ".aac", ".m4a", ".m4b", ".m4p", ".ogg", ".oga", ".opus",
				".wma", ".aiff", ".aif", ".aifc", ".au", ".snd", ".amr", ".ape", ".wv", ".dsf",
				".dff", ".tta", ".mka", ".ac3", ".eac3", ".dts", ".mp2", ".mpa", ".ra", ".rm",
				".voc", ".caf", ".3ga", ".weba", ".spx", ".mid", ".midi", ".adts", ".loas",
				// Less common but ffmpeg-readable
				".cda", ".alac", ".mpc", ".ofr", ".opus", ".tak", ".w64", ".rf64", ".avr", ".cvs",
				".cwf", ".fap", ".ircam", ".m4r", ".mat", ".mjf", ".mpc8", ".oga", ".paf", ".pvf",
				".sln", ".smp", ".snd", ".sph", ".svx", ".vms", ".wve", ".xwma", ".yop", ".8svx",
			}.ToFrozenSet();

		private static readonly FrozenSet<string> videoExtensions =
			new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				// Common
				".mp4", ".m4v", ".mkv", ".webm", ".avi", ".mov", ".wmv", ".flv", ".f4v", ".ts",
				".m2ts", ".mts", ".m2v", ".mpg", ".mpeg", ".mpe", ".vob", ".ogv", ".ogm", ".3gp",
				".3g2", ".asf", ".rm", ".rmvb", ".divx", ".xvid", ".mxf", ".nut", ".nsv", ".swf",
				".drc", ".dv", ".wtv", ".dvr-ms", ".bik", ".flic", ".roq", ".y4m", ".hevc", ".h265",
				".265", ".h264", ".264", ".av1", ".vp8", ".vp9", ".avc", ".m2p", ".m1v", ".m2v",
				// Less common but ffmpeg-readable
				".amv", ".asf", ".avs", ".cavs", ".cdxl", ".dpg", ".flm", ".gxf", ".h261", ".h263",
				".ipu", ".m4v", ".mj2", ".mjpg", ".mts", ".mxf", ".nsv", ".nuv", ".ogg", ".ogv",
				".pva", ".r3d", ".rtsp", ".smk", ".thp", ".v210", ".vc1", ".vro", ".wmv", ".yuv",
				".webm", ".wmv", ".wtv", ".xmv", ".mpegts", ".mts", ".vob",
			}.ToFrozenSet();

		public static bool IsAudioExtension(string? extension)
			=> !string.IsNullOrEmpty(extension) && audioExtensions.Contains(extension);

		public static bool IsVideoExtension(string? extension)
			=> !string.IsNullOrEmpty(extension) && videoExtensions.Contains(extension);

		public static bool IsMediaExtension(string? extension)
			=> IsAudioExtension(extension) || IsVideoExtension(extension);
	}
}