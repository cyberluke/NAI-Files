// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Services.Pulse.MediaLab
{
	/// <summary>
	/// The capability-aware FFmpeg operation registry. Operations declare the filters/encoders they
	/// need; the probe reads the bundled binary's `-filters` / `-encoders` output so the Lab only
	/// offers what this build can actually run. All operations are non-destructive: they write a
	/// sibling file and leave the original untouched.
	/// </summary>
	public static class MediaOperationRegistry
	{
		public static IReadOnlyList<MediaOperation> All { get; } = Build();

		public static IReadOnlyList<MediaOperation> For(MediaKind kind)
			=> All.Where(operation => operation.Kind == kind).ToArray();

		/// <summary>Reads the bundled binary's filter/encoder lists once per session.</summary>
		public sealed class CapabilityProbe
		{
			private readonly HashSet<string> filters = new(StringComparer.OrdinalIgnoreCase);
			private readonly HashSet<string> encoders = new(StringComparer.OrdinalIgnoreCase);
			private bool loaded;

			public async Task LoadAsync(PulseFfmpeg ffmpeg, CancellationToken token)
			{
				if (loaded)
					return;

				foreach (var line in await ffmpeg.ProbeCapabilitiesAsync("-filters", token))
				{
					var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
					if (parts.Length >= 2 && !parts[0].StartsWith('-'))
						filters.Add(parts[1]);
				}

				foreach (var line in await ffmpeg.ProbeCapabilitiesAsync("-encoders", token))
				{
					var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
					if (parts.Length >= 2)
						encoders.Add(parts[1]);
				}

				loaded = true;
			}

			public bool Supports(MediaOperation operation)
				=> operation.RequiredFilters.All(filters.Contains)
					&& operation.RequiredEncoders.All(encoders.Contains);
		}

		private static string[] Resolve(IEnumerable<string> args, string inputPath)
			=> args.Select(arg => arg.Replace("{in}", inputPath, StringComparison.Ordinal)).ToArray();

		private static MediaOperation Produce(
			string id, string title, string description, MediaKind kind, MediaLabGroup group,
			string suffix, string extension, string[] requiredFilters, string[] requiredEncoders, params string[] args)
			=> new()
			{
				Id = id,
				Title = title,
				Description = description,
				Kind = kind,
				Group = group,
				Suffix = suffix,
				OutputExtension = extension,
				RequiredFilters = requiredFilters,
				RequiredEncoders = requiredEncoders,
				Execute = async (context, progress, token) =>
				{
					var succeeded = await context.Ffmpeg.RunCustomAsync(Resolve(args, context.InputPath), context.OutputPath, token, progress);
					return succeeded ? context.OutputPath : null;
				},
			};

		private static MediaOperation Analyze(
			string id, string title, string description, MediaKind kind, string[] requiredFilters, params string[] args)
			=> new()
			{
				Id = id,
				Title = title,
				Description = description,
				Kind = kind,
				Group = MediaLabGroup.Analyze,
				IsAnalysis = true,
				RequiredFilters = requiredFilters,
				Execute = async (context, _, token) =>
					await context.Ffmpeg.RunAnalysisAsync(Resolve(args, context.InputPath), token),
			};

		private static List<MediaOperation> Build()
		{
			var videoHead = new[] { "-y", "-hide_banner", "-nostdin", "-i", "{in}" };
			var h264 = new[] { "libx264" };

			return
			[
				// ---- Video / Enhance ----
				Produce("video.60fps", "60 FPS motion", "Optical-flow interpolation (MCI) for smooth 60 fps playback.",
					MediaKind.Video, MediaLabGroup.Enhance, "60fps", ".mp4",
					["minterpolate"], h264,
					[.. videoHead, "-vf", "minterpolate=fps=60:mi_mode=mci", "-c:v", "libx264", "-crf", "18", "-preset", "slow", "-c:a", "copy"]),

				Produce("video.denoise", "Denoise", "Temporal/spatial denoise for compression noise (hqdn3d).",
					MediaKind.Video, MediaLabGroup.Enhance, "denoised", ".mp4",
					["hqdn3d"], h264,
					[.. videoHead, "-vf", "hqdn3d=4:3:6:4.5", "-c:v", "libx264", "-crf", "20", "-c:a", "copy"]),

				Produce("video.sharpen", "Sharpen", "Unsharp mask for soft footage.",
					MediaKind.Video, MediaLabGroup.Enhance, "sharpened", ".mp4",
					["unsharp"], h264,
					[.. videoHead, "-vf", "unsharp=5:5:1.0:5:5:0.0", "-c:v", "libx264", "-crf", "20", "-c:a", "copy"]),

				Produce("video.deinterlace", "Deinterlace", "BWDIF deinterlace for interlaced sources.",
					MediaKind.Video, MediaLabGroup.Enhance, "deinterlaced", ".mp4",
					["bwdif"], h264,
					[.. videoHead, "-vf", "bwdif", "-c:v", "libx264", "-crf", "20", "-c:a", "copy"]),

				Produce("video.hdr", "Tone map", "HDR roll-off / tone mapping (hable).",
					MediaKind.Video, MediaLabGroup.Enhance, "tonemapped", ".mp4",
					["tonemap"], h264,
					[.. videoHead, "-vf", "format=gbrpf32le,tonemap=hable:desat=0,format=yuv420p", "-c:v", "libx264", "-crf", "20", "-c:a", "copy"]),

				// ---- Video / Create ----
				Produce("video.reverse", "Reverse", "Reverse video and audio.",
					MediaKind.Video, MediaLabGroup.Create, "reversed", ".mp4",
					["reverse"], ["libx264", "aac"],
					[.. videoHead, "-vf", "reverse", "-af", "areverse", "-c:v", "libx264", "-crf", "20", "-c:a", "aac"]),

				Produce("video.speed2x", "Speed 2×", "Double speed (setpts + atempo).",
					MediaKind.Video, MediaLabGroup.Create, "2x", ".mp4",
					["setpts"], ["libx264", "aac"],
					[.. videoHead, "-vf", "setpts=0.5*PTS", "-af", "atempo=2.0", "-c:v", "libx264", "-crf", "20", "-c:a", "aac"]),

				Produce("video.slowmo", "Slow motion 0.5×", "Half speed (setpts + atempo).",
					MediaKind.Video, MediaLabGroup.Create, "slowmo", ".mp4",
					["setpts"], ["libx264", "aac"],
					[.. videoHead, "-vf", "setpts=2.0*PTS", "-af", "atempo=0.5", "-c:v", "libx264", "-crf", "20", "-c:a", "aac"]),

				Produce("video.contact", "Contact sheet", "One overview frame every 5 s, tiled 5×4.",
					MediaKind.Video, MediaLabGroup.Create, "contact", ".png",
					["tile"], [],
					[.. videoHead, "-vf", "fps=1/5,scale=320:-1,tile=5x4", "-frames:v", "1"]),

				// ---- Video / Analyze ----
				Analyze("video.freezedetect", "Detect frozen footage", "Flags static segments (freezedetect).",
					MediaKind.Video, ["freezedetect"],
					"-y", "-hide_banner", "-nostdin", "-i", "{in}", "-vf", "freezedetect=n=-50dB:d=2", "-an"),

				Analyze("video.scenedetect", "Detect scene changes", "Lists scene-cut timestamps (select + showinfo).",
					MediaKind.Video, ["select"],
					"-y", "-hide_banner", "-nostdin", "-i", "{in}", "-vf", "select='gt(scene,0.4)',showinfo", "-an"),

				Analyze("video.blackdetect", "Detect black frames", "Flags black intervals (blackdetect).",
					MediaKind.Video, ["blackdetect"],
					"-y", "-hide_banner", "-nostdin", "-i", "{in}", "-vf", "blackdetect=d=0.5:pix_th=0.10", "-an"),

				Analyze("video.silencedetect", "Detect silence", "Flags silent audio intervals.",
					MediaKind.Video, ["silencedetect"],
					"-y", "-hide_banner", "-nostdin", "-i", "{in}", "-af", "silencedetect=n=-50dB:d=1", "-vn"),

				// ---- Audio / Enhance ----
				Produce("audio.loudnorm", "Loudness normalize", "EBU R128 loudness to −16 LUFS.",
					MediaKind.Audio, MediaLabGroup.Enhance, "loudnorm", ".mp3",
					["loudnorm"], ["libmp3lame"],
					[.. videoHead, "-af", "loudnorm=I=-16:TP=-1.5:LRA=11", "-c:a", "libmp3lame", "-b:a", "192k"]),

				Produce("audio.denoise", "Denoise", "FFT denoise (afftdn) for hiss/room noise.",
					MediaKind.Audio, MediaLabGroup.Enhance, "denoised", ".mp3",
					["afftdn"], ["libmp3lame"],
					[.. videoHead, "-af", "afftdn=nf=-25", "-c:a", "libmp3lame", "-b:a", "192k"]),

				Produce("audio.compress", "Compress", "Gentle dynamic compression (acompressor).",
					MediaKind.Audio, MediaLabGroup.Enhance, "compressed", ".mp3",
					["acompressor"], ["libmp3lame"],
					[.. videoHead, "-af", "acompressor=threshold=-18dB:ratio=3:attack=20:release=250", "-c:a", "libmp3lame", "-b:a", "192k"]),

				Produce("audio.limiter", "Limiter", "Peak limiting at −0.5 dBFS (alimiter).",
					MediaKind.Audio, MediaLabGroup.Enhance, "limited", ".mp3",
					["alimiter"], ["libmp3lame"],
					[.. videoHead, "-af", "alimiter=limit=0.95", "-c:a", "libmp3lame", "-b:a", "192k"]),

				// ---- Audio / Analyze ----
				Analyze("audio.silence", "Detect silence", "Lists silent intervals.",
					MediaKind.Audio, ["silencedetect"],
					"-y", "-hide_banner", "-nostdin", "-i", "{in}", "-af", "silencedetect=n=-50dB:d=1", "-vn"),

				Analyze("audio.loudness", "Measure loudness", "EBU R128 loudness report (JSON).",
					MediaKind.Audio, ["loudnorm"],
					"-y", "-hide_banner", "-nostdin", "-i", "{in}", "-af", "loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json", "-vn"),

				// ---- Image / Enhance ----
				Produce("image.upscale2x", "Upscale 2×", "Lanczos 2× upscale.",
					MediaKind.Image, MediaLabGroup.Enhance, "2x", ".png",
					["scale"], [],
					[.. videoHead, "-vf", "scale=iw*2:ih*2:flags=lanczos"]),

				Produce("image.sharpen", "Sharpen", "Unsharp mask for soft images.",
					MediaKind.Image, MediaLabGroup.Enhance, "sharpened", ".png",
					["unsharp"], [],
					[.. videoHead, "-vf", "unsharp=5:5:1.0:5:5:0.0"]),

				Produce("image.denoise", "Denoise", "Light spatial denoise (hqdn3d).",
					MediaKind.Image, MediaLabGroup.Enhance, "denoised", ".png",
					["hqdn3d"], [],
					[.. videoHead, "-vf", "hqdn3d=1.5:1.5:6:6"]),

				Produce("image.grayscale", "Grayscale", "Desaturate the image.",
					MediaKind.Image, MediaLabGroup.Enhance, "gray", ".png",
					["hue"], [],
					[.. videoHead, "-vf", "hue=s=0"]),
			];
		}
	}
}
