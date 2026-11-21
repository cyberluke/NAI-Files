// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;
using Windows.Foundation;

namespace Files.App.Services.Pulse
{
	/// <summary>Audio export/convert codec choices (any source format FFmpeg can decode).</summary>
	public enum PulseAudioFormat
	{
		Mp3,
		M4a,
		Wav,
		Ogg,
		Flac,
		Opus,
	}

	/// <summary>Video cut/convert container choices (re-encoded).</summary>
	public enum PulseVideoFormat
	{
		Mp4,
		Webm,
		Mkv,
		Mov,
	}

	/// <summary>Image convert/export formats.</summary>
	public enum PulseImageFormat
	{
		Png,
		Jpg,
		Webp,
		Bmp,
		Tiff,
	}

	/// <summary>Audio post-processing applied at export time (in this order: loudness, tempo, fades).</summary>
	/// <param name="Normalize">EBU R128 loudness normalization (loudnorm, -16 LUFS).</param>
	/// <param name="Speed">Playback tempo 0.5..2.0 (atempo); 1.0 leaves the tempo untouched.</param>
	/// <param name="FadeInSeconds">Fade-in length at the start of the exported clip (afade).</param>
	/// <param name="FadeOutSeconds">Fade-out length at the end of the exported clip (afade).</param>
	public sealed record PulseAudioEffects(bool Normalize = false, double Speed = 1.0, double FadeInSeconds = 0, double FadeOutSeconds = 0)
	{
		/// <summary>Builds the <c>-af</c> chain for a clip of <paramref name="duration"/>; null when no effect applies.</summary>
		public string? BuildFilterChain(TimeSpan duration)
		{
			var speed = Math.Clamp(double.IsFinite(Speed) ? Speed : 1, 0.5, 2);
			var outputSeconds = duration.TotalSeconds / speed;
			var filters = new List<string>();
			if (Normalize)
				filters.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
			if (Math.Abs(speed - 1.0) > 0.005)
				filters.Add($"atempo={speed.ToString("0.###", CultureInfo.InvariantCulture)}");
			if (FadeInSeconds > 0)
				filters.Add($"afade=t=in:st=0:d={FadeInSeconds.ToString("0.###", CultureInfo.InvariantCulture)}");
			if (FadeOutSeconds > 0)
			{
				var start = Math.Max(0, outputSeconds - FadeOutSeconds);
				filters.Add($"afade=t=out:st={start.ToString("0.###", CultureInfo.InvariantCulture)}:d={FadeOutSeconds.ToString("0.###", CultureInfo.InvariantCulture)}");
			}
			return filters.Count > 0 ? string.Join(",", filters) : null;
		}
	}

	/// <summary>Image edit operations applied in order: rotation, crop, scale.</summary>
	/// <param name="QuarterTurnsCw">0..3 clockwise 90° steps (transpose).</param>
	/// <param name="Crop">Normalized crop rectangle (0..1 each of x, y, width, height); null keeps the full frame.</param>
	/// <param name="ScalePercent">Output scale relative to the (cropped) image; 1.0 keeps the size.</param>
	public sealed record PulseImageOps(int QuarterTurnsCw = 0, Rect? Crop = null, double ScalePercent = 1.0)
	{
		/// <summary>Builds the <c>-vf</c> chain; null when no operation applies.</summary>
		public string? BuildFilterChain()
		{
			var filters = new List<string>();
			var turns = ((QuarterTurnsCw % 4) + 4) % 4;
			for (int i = 0; i < turns; i++)
				filters.Add("transpose=1");
			if (Crop is { } crop && crop.Width > 0.001 && crop.Height > 0.001)
				filters.Add($"crop=iw*{crop.Width.ToString("0.####", CultureInfo.InvariantCulture)}:ih*{crop.Height.ToString("0.####", CultureInfo.InvariantCulture)}:iw*{crop.X.ToString("0.####", CultureInfo.InvariantCulture)}:ih*{crop.Y.ToString("0.####", CultureInfo.InvariantCulture)}");
			if (Math.Abs(ScalePercent - 1.0) > 0.005)
				filters.Add($"scale=iw*{ScalePercent.ToString("0.####", CultureInfo.InvariantCulture)}:ih*{ScalePercent.ToString("0.####", CultureInfo.InvariantCulture)}:flags=lanczos");
			return filters.Count > 0 ? string.Join(",", filters) : null;
		}
	}

	/// <summary>Encoding profile for one audio format.</summary>
	public sealed record PulseAudioFormatSpec(string Extension, string Codec, string CodecOptions)
	{
		public static PulseAudioFormatSpec For(PulseAudioFormat format) => format switch
		{
			PulseAudioFormat.Mp3 => new("mp3", "libmp3lame", "-q:a 2"),
			PulseAudioFormat.M4a => new("m4a", "aac", "-b:a 192k"),
			PulseAudioFormat.Wav => new("wav", "pcm_s16le", ""),
			PulseAudioFormat.Ogg => new("ogg", "libvorbis", "-q:a 5"),
			PulseAudioFormat.Flac => new("flac", "flac", "-compression_level 8"),
			PulseAudioFormat.Opus => new("opus", "libopus", "-b:a 160k"),
			_ => new("mp3", "libmp3lame", "-q:a 2"),
		};
	}

	/// <summary>Encoding profile for one video container (video + audio re-encoded).</summary>
	public sealed record PulseVideoFormatSpec(string Extension, string VideoCodec, string VideoOptions, string AudioCodec, string AudioOptions, string? MuxOptions = null)
	{
		public static PulseVideoFormatSpec For(PulseVideoFormat format) => format switch
		{
			PulseVideoFormat.Mp4 => new("mp4", "libx264", "-preset veryfast -crf 20 -pix_fmt yuv420p", "aac", "-b:a 160k", "-movflags +faststart"),
			PulseVideoFormat.Webm => new("webm", "libvpx-vp9", "-crf 32 -b:v 0 -row-mt 1", "libopus", "-b:a 128k"),
			PulseVideoFormat.Mkv => new("mkv", "libx264", "-preset veryfast -crf 20 -pix_fmt yuv420p", "libopus", "-b:a 128k"),
			PulseVideoFormat.Mov => new("mov", "libx264", "-preset veryfast -crf 20 -pix_fmt yuv420p", "aac", "-b:a 160k"),
			_ => new("mp4", "libx264", "-preset veryfast -crf 20 -pix_fmt yuv420p", "aac", "-b:a 160k", "-movflags +faststart"),
		};
	}

	/// <summary>Encoding profile for one image format; Quality is the -q:v range (null = lossless/raw).</summary>
	public sealed record PulseImageFormatSpec(string Extension, string Codec, (int Min, int Max)? Quality)
	{
		public static PulseImageFormatSpec For(PulseImageFormat format) => format switch
		{
			PulseImageFormat.Png => new("png", "png", null),
			PulseImageFormat.Jpg => new("jpg", "mjpeg", (2, 31)),
			PulseImageFormat.Webp => new("webp", "libwebp", (0, 100)),
			PulseImageFormat.Bmp => new("bmp", "bmp", null),
			PulseImageFormat.Tiff => new("tiff", "tiff", null),
			_ => new("png", "png", null),
		};

		/// <summary>
		/// Maps a 0..100 quality percentage to the format's <c>-q:v</c> value (JPG is inverted:
		/// lower is better, 2..31); null for lossless/raw formats.
		/// </summary>
		public static int? MapQuality(PulseImageFormat format, int percent)
		{
			percent = Math.Clamp(percent, 0, 100);
			return format switch
			{
				PulseImageFormat.Jpg => (int)Math.Round(32 - percent * 0.30),
				PulseImageFormat.Webp => percent,
				_ => null,
			};
		}
	}

	/// <summary>
	/// Thin wrapper around the FFmpeg 9 toolset bundled with the app (and, as a fallback, the copy
	/// shipped inside NAI OS, then PATH). Powers the Pulse media tools: probing media duration and
	/// image dimensions, decoding audio to raw PCM for waveform analysis (any container FFmpeg can
	/// read), and exporting/editing clips: audio trim/extract with effects (loudness, tempo, fades),
	/// video cuts and conversions, image conversion and edits (rotate/crop/scale), single-frame
	/// extraction and GIF creation. All processes run hidden; cancellation kills the child.
	/// </summary>
	public sealed class PulseFfmpeg
	{
		private const int PcmSampleRate = 44100;
		private const int PcmBytesPerSecond = PcmSampleRate * sizeof(float);

		private static PulseFfmpeg? located;
		private static readonly PulseRuntimeDiscovery<PulseFfmpeg> Discovery = new(Locate);
		public static string? LastLocateError { get; private set; }

		public static Task<PulseFfmpeg?> LocateAsync(CancellationToken cancellationToken = default)
			=> Discovery.GetAsync(cancellationToken);

		public static async Task<PulseFfmpeg> RequireAsync(CancellationToken cancellationToken = default)
			=> await LocateAsync(cancellationToken)
				?? throw new IOException(string.Format(Strings.PulseMediaRuntimeUnavailable.GetLocalizedResource(), LastLocateError ?? "ffmpeg / ffprobe"));

		public string FfmpegPath { get; }
		public string FfprobePath { get; }
		public string Version { get; }

		private PulseFfmpeg(string ffmpegPath, string ffprobePath, string version)
		{
			FfmpegPath = ffmpegPath;
			FfprobePath = ffprobePath;
			Version = version;
		}

		/// <summary>
		/// Resolves the FFmpeg toolset once per session. Order: the copy inside this package,
		/// the copy inside an installed NAI OS (dev-mode Files runs against the NAI OS resource
		/// dir), then whatever ffmpeg/ffprobe resolve on PATH. Every candidate is verified with a
		/// bounded <c>-version</c> run so a broken shim is never accepted.
		/// </summary>
		public static PulseFfmpeg? Locate(CancellationToken cancellationToken = default)
		{
			if (located is not null)
				return located;

			var failures = new List<string>();
			try { PulseRuntimeStager.StageFfmpeg(cancellationToken); }
			catch (OperationCanceledException) { throw; }
			catch (Exception ex) { failures.Add(ex.Message); }

			var candidates = new List<(string Ffmpeg, string Ffprobe)>();
			var packaged = false;

			// Staged writable copies (packaged run) — most reliable.
			if (PulseRuntimeStager.IsFfmpegStaged && PulseRuntimeStager.GetStagedRoot() is { } stagedRoot)
			{
				candidates.Add((Path.Combine(stagedRoot, "ffmpeg", "ffmpeg.exe"), Path.Combine(stagedRoot, "ffmpeg", "ffprobe.exe")));
			}

			// Packaged: content items land next to the app binaries.
			try
			{
				if (Package.Current is { } package)
				{
					packaged = true;
					var root = package.InstalledLocation.Path;
					candidates.Add((Path.Combine(root, "ffmpeg", "ffmpeg.exe"), Path.Combine(root, "ffmpeg", "ffprobe.exe")));
				}
			}
			catch
			{
				// Unpackaged (dev) run: Package.Current throws.
			}

			if (!packaged)
			{
				candidates.Add((Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"), Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffprobe.exe")));
				foreach (var root in GetNaiOsRoots())
					candidates.Add((Path.Combine(root, "static", "ffmpeg", "ffmpeg.exe"), Path.Combine(root, "static", "ffmpeg", "ffprobe.exe")));
				candidates.Add(("ffmpeg.exe", "ffprobe.exe"));
			}

			foreach (var (ffmpeg, ffprobe) in candidates)
			{
				cancellationToken.ThrowIfCancellationRequested();
				// Bare names resolve through PATH inside the version probe; paths must exist.
				var isBareName = !ffmpeg.Contains(Path.DirectorySeparatorChar) && !ffmpeg.Contains(Path.AltDirectorySeparatorChar);
				if (!isBareName && (!File.Exists(ffmpeg) || !File.Exists(ffprobe)))
					continue;

				if (TryGetVersion(ffmpeg, cancellationToken) is not { } version || TryGetVersion(ffprobe, cancellationToken) is null)
				{
					if (LastLocateError is not null) failures.Add(LastLocateError);
					continue;
				}

				LastLocateError = null;
				return located = new PulseFfmpeg(ffmpeg, ffprobe, version);
			}

			LastLocateError = string.Join(Environment.NewLine, failures.Distinct());
			return null;
		}

		// ---- probing --------------------------------------------------------------

		/// <summary>Media duration via ffprobe; null when the file cannot be probed.</summary>
		public async Task<TimeSpan?> ProbeDurationAsync(string path, CancellationToken cancellationToken = default)
		{
			var psi = NewProbeProcess(FfprobePath);
			psi.ArgumentList.Add("-show_entries");
			psi.ArgumentList.Add("format=duration");
			psi.ArgumentList.Add("-of");
			psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
			psi.ArgumentList.Add(path);

			var output = await PulseProcess.TextAsync(psi, cancellationToken, TimeSpan.FromSeconds(20));

			var line = output.Trim();
			if (double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) && seconds > 0)
				return TimeSpan.FromSeconds(seconds);

			return null;
		}

		/// <summary>Pixel size of the first video stream (images and videos); null when unavailable.</summary>
		public async Task<(int Width, int Height)?> ProbeImageSizeAsync(string path, CancellationToken cancellationToken = default)
		{
			var psi = NewProbeProcess(FfprobePath);
			psi.ArgumentList.Add("-select_streams");
			psi.ArgumentList.Add("v:0");
			psi.ArgumentList.Add("-show_entries");
			psi.ArgumentList.Add("stream=width,height");
			psi.ArgumentList.Add("-of");
			psi.ArgumentList.Add("csv=p=0");
			psi.ArgumentList.Add(path);

			var output = await PulseProcess.TextAsync(psi, cancellationToken, TimeSpan.FromSeconds(20));

			var parts = output.Trim().Split(',');
			if (parts.Length == 2
				&& int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
				&& int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
				&& width > 0 && height > 0)
			{
				return (width, height);
			}

			return null;
		}

		// ---- waveform -------------------------------------------------------------

		/// <summary>
		/// Decodes the file's first audio stream to mono 44.1 kHz float PCM (any container FFmpeg can
		/// read: MP3, FLAC, MP4, WebM, MKV, ...) and folds it into a spectral waveform with the same
		/// kernel the Media Foundation path uses. Progress is reported as 0..1 of the audio timeline.
		/// Returns null when the file has no decodable audio track.
		/// </summary>
		public async Task<PulseWaveform?> AnalyzeAsync(string path, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
		{
			var duration = await ProbeDurationAsync(path, cancellationToken);
			var psi = NewFfmpegProcess();
			psi.ArgumentList.Add("-i");
			psi.ArgumentList.Add(path);
			psi.ArgumentList.Add("-map");
			psi.ArgumentList.Add("0:a:0");
			psi.ArgumentList.Add("-vn");
			psi.ArgumentList.Add("-ac");
			psi.ArgumentList.Add("1");
			psi.ArgumentList.Add("-ar");
			psi.ArgumentList.Add(PcmSampleRate.ToString(CultureInfo.InvariantCulture));
			psi.ArgumentList.Add("-f");
			psi.ArgumentList.Add("f32le");
			psi.ArgumentList.Add("pipe:1");

			var accumulator = new PulseWaveformAccumulator(1, PcmSampleRate);
			var buffer = new byte[64 * 1024];
			var totalBytes = 0L;
			var totalSeconds = duration?.TotalSeconds ?? 0;

			await PulseProcess.RunAsync(psi, async (stream, token) =>
			{
				var remainder = 0;
				while (true)
				{
					var read = await stream.ReadAsync(buffer.AsMemory(remainder), token).ConfigureAwait(false);
					if (read <= 0)
						break;

					totalBytes += read;
					var available = read + remainder;
					var floatCount = available / sizeof(float);
					var floats = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, floatCount * sizeof(float)));
					accumulator.Add(floats);
					remainder = available % sizeof(float);
					if (remainder > 0) buffer.AsSpan(available - remainder, remainder).CopyTo(buffer);

					if (progress is not null && totalSeconds > 0)
						progress.Report(Math.Min(1, totalBytes / (double)(totalSeconds * PcmBytesPerSecond)));
				}
				return true;
			}, cancellationToken);
			progress?.Report(1);

			return accumulator.HasData ? accumulator.Build() : null;
		}

		// ---- audio export / trim / convert ---------------------------------------

		/// <summary>
		/// Re-encodes the <c>[start, end)</c> region of the file's first audio stream into an audio-only
		/// clip, optionally applying <see cref="PulseAudioEffects"/>. Progress is 0..1 while encoding.
		/// The output file is created only on success (temp file moved into place afterwards).
		/// </summary>
		public Task<bool> ExportAudioAsync(string inputPath, TimeSpan start, TimeSpan end, PulseAudioFormat format, string outputPath, PulseAudioEffects? effects = null, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
		{
			var spec = PulseAudioFormatSpec.For(format);
			var duration = end - start;
			if (duration <= TimeSpan.Zero)
				return Task.FromResult(false);

			var args = new List<string> { "-y", "-hide_banner", "-nostdin", "-ss", FormatSeconds(start), "-t", FormatSeconds(duration), "-i", inputPath, "-map", "0:a:0", "-vn" };
			if (effects?.BuildFilterChain(duration) is { } filterChain)
			{
				args.Add("-af");
				args.Add(filterChain);
			}
			args.Add("-c:a");
			args.Add(spec.Codec);
			AddOptions(args, spec.CodecOptions);

			return RunWithProgressAsync(args, outputPath, duration.TotalSeconds / Math.Clamp(effects?.Speed ?? 1, 0.5, 2), cancellationToken, progress);
		}

		/// <summary>Converts the whole audio track to another audio format (same pipeline as a full-length trim).</summary>
		public async Task<bool> ConvertAudioAsync(string inputPath, PulseAudioFormat format, string outputPath, PulseAudioEffects? effects = null, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
		{
			var duration = await ProbeDurationAsync(inputPath, cancellationToken);
			if (duration is not { TotalSeconds: > 0 })
				return false;

			return await ExportAudioAsync(inputPath, TimeSpan.Zero, duration.Value, format, outputPath, effects, cancellationToken, progress);
		}

		// ---- video cut / convert --------------------------------------------------

		/// <summary>
		/// Cuts the <c>[start, end)</c> region of a video and re-encodes it into the chosen container
		/// (video + audio, or video only when the source has no audio track). Progress is 0..1.
		/// </summary>
		public Task<bool> ExportVideoAsync(string inputPath, TimeSpan start, TimeSpan end, PulseVideoFormat format, string outputPath, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
		{
			var spec = PulseVideoFormatSpec.For(format);
			var duration = end - start;
			if (duration <= TimeSpan.Zero)
				return Task.FromResult(false);

			var args = new List<string> { "-y", "-hide_banner", "-nostdin", "-ss", FormatSeconds(start), "-i", inputPath, "-t", FormatSeconds(duration), "-map", "0:v:0", "-map", "0:a:0?" };
			args.Add("-c:v");
			args.Add(spec.VideoCodec);
			AddOptions(args, spec.VideoOptions);
			if (format != PulseVideoFormat.Webm)
				args.AddRange(["-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2"]);
			args.Add("-c:a");
			args.Add(spec.AudioCodec);
			AddOptions(args, spec.AudioOptions);
			AddOptions(args, spec.MuxOptions);

			return RunWithProgressAsync(args, outputPath, duration.TotalSeconds, cancellationToken, progress);
		}

		/// <summary>Converts the whole video to another container/codec.</summary>
		public async Task<bool> ConvertVideoAsync(string inputPath, PulseVideoFormat format, string outputPath, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
		{
			var duration = await ProbeDurationAsync(inputPath, cancellationToken);
			if (duration is not { TotalSeconds: > 0 })
				return false;

			return await ExportVideoAsync(inputPath, TimeSpan.Zero, duration.Value, format, outputPath, cancellationToken, progress);
		}

		// ---- images ---------------------------------------------------------------

		/// <summary>Converts an image file to another format; <paramref name="quality"/> applies to lossy formats.</summary>
		public Task<bool> ConvertImageAsync(string inputPath, PulseImageFormat format, int? quality, string outputPath, CancellationToken cancellationToken = default)
		{
			var spec = PulseImageFormatSpec.For(format);
			var args = new List<string> { "-y", "-hide_banner", "-nostdin", "-i", inputPath, "-frames:v", "1", "-c:v", spec.Codec };
			AddImageQuality(args, spec, quality);
			return RunSilentAsync(args, outputPath, cancellationToken);
		}

		/// <summary>Applies rotate/crop/scale operations to an image and writes it in the chosen format.</summary>
		public Task<bool> EditImageAsync(string inputPath, PulseImageOps ops, PulseImageFormat format, int? quality, string outputPath, CancellationToken cancellationToken = default)
		{
			var spec = PulseImageFormatSpec.For(format);
			var args = new List<string> { "-y", "-hide_banner", "-nostdin", "-i", inputPath };
			if (ops.BuildFilterChain() is { } filterChain)
			{
				args.Add("-vf");
				args.Add(filterChain);
			}
			args.Add("-frames:v");
			args.Add("1");
			args.Add("-c:v");
			args.Add(spec.Codec);
			AddImageQuality(args, spec, quality);
			return RunSilentAsync(args, outputPath, cancellationToken);
		}

		/// <summary>Extracts a single frame at <paramref name="timestamp"/> as an image file.</summary>
		public Task<bool> ExtractFrameAsync(string inputPath, TimeSpan timestamp, PulseImageFormat format, int? quality, string outputPath, CancellationToken cancellationToken = default)
		{
			var spec = PulseImageFormatSpec.For(format);
			var args = new List<string> { "-y", "-hide_banner", "-nostdin", "-ss", FormatSeconds(timestamp), "-i", inputPath, "-frames:v", "1", "-c:v", spec.Codec };
			AddImageQuality(args, spec, quality);
			return RunSilentAsync(args, outputPath, cancellationToken);
		}

		/// <summary>Creates an animated GIF from the <c>[start, end)</c> video region.</summary>
		public Task<bool> CreateGifAsync(string inputPath, TimeSpan start, TimeSpan end, int fps, int width, string outputPath, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
		{
			var duration = end - start;
			if (duration <= TimeSpan.Zero)
				return Task.FromResult(false);

			fps = Math.Clamp(fps, 5, 30);
			width = Math.Clamp(width, 80, 1280);
			var filter = $"fps={fps},scale={width}:-1:flags=lanczos,split[s0][s1];[s0]palettegen=stats_mode=diff[p];[s1][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle";

			var args = new List<string> { "-y", "-hide_banner", "-nostdin", "-ss", FormatSeconds(start), "-t", FormatSeconds(duration), "-i", inputPath, "-vf", filter, "-loop", "0" };
			return RunWithProgressAsync(args, outputPath, duration.TotalSeconds, cancellationToken, progress);
		}

		// ---- process plumbing -----------------------------------------------------

		private ProcessStartInfo NewFfmpegProcess()
			=> NewProcess(FfmpegPath);

		private static ProcessStartInfo NewProbeProcess(string executable)
			=> NewProcess(executable);

		private static ProcessStartInfo NewProcess(string executable)
			=> new(executable)
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = true,
			};

		private static void AddOptions(List<string> args, string? options)
		{
			if (string.IsNullOrWhiteSpace(options))
				return;
			foreach (var option in options.Split(' ', StringSplitOptions.RemoveEmptyEntries))
				args.Add(option);
		}

		private static void AddImageQuality(List<string> args, PulseImageFormatSpec spec, int? quality)
		{
			if (quality is not int value || spec.Quality is not { } range)
				return;

			value = Math.Clamp(value, range.Min, range.Max);
			args.Add("-q:v");
			args.Add(value.ToString(CultureInfo.InvariantCulture));
		}

		/// <summary>Runs ffmpeg with <c>-progress pipe:1</c> reporting and moves the temp output into place on success.</summary>
		private async Task<bool> RunWithProgressAsync(List<string> args, string outputPath, double progressSeconds, CancellationToken cancellationToken, IProgress<double>? progress)
		{
			var tempOutput = TemporaryOutputPath(outputPath);
			var psi = NewFfmpegProcess();
			foreach (var arg in args)
				psi.ArgumentList.Add(arg);
			psi.ArgumentList.Add("-progress");
			psi.ArgumentList.Add("pipe:1");
			psi.ArgumentList.Add("-nostats");
			psi.ArgumentList.Add(tempOutput);

			try
			{
				await PulseProcess.RunAsync(psi, async (stream, token) =>
				{
					using var reader = new StreamReader(stream, leaveOpen: true);
					string? line;
					while ((line = await reader.ReadLineAsync(token).ConfigureAwait(false)) is not null)
					{
						if (progress is not null && progressSeconds > 0 && line.StartsWith("out_time_us=", StringComparison.OrdinalIgnoreCase)
							&& long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
						{
							progress.Report(Math.Min(1, micros / (progressSeconds * 1_000_000)));
						}
					}
					return true;
				}, cancellationToken);
				progress?.Report(1);

				if (!File.Exists(tempOutput))
					throw new IOException(string.Format(Strings.PulseMediaOutputMissing.GetLocalizedResource(), outputPath));

				File.Move(tempOutput, outputPath, overwrite: true);
				return true;
			}
			finally
			{
				TryDelete(tempOutput);
			}
		}

		/// <summary>Runs ffmpeg without progress reporting (images/short jobs) and moves the temp output into place on success.</summary>
		private async Task<bool> RunSilentAsync(List<string> args, string outputPath, CancellationToken cancellationToken)
		{
			var tempOutput = TemporaryOutputPath(outputPath);
			var psi = NewFfmpegProcess();
			foreach (var arg in args)
				psi.ArgumentList.Add(arg);
			psi.ArgumentList.Add(tempOutput);

			try
			{
				await PulseProcess.TextAsync(psi, cancellationToken, TimeSpan.FromMinutes(10));
				if (!File.Exists(tempOutput))
					throw new IOException(string.Format(Strings.PulseMediaOutputMissing.GetLocalizedResource(), outputPath));

				File.Move(tempOutput, outputPath, overwrite: true);
				return true;
			}
			finally
			{
				TryDelete(tempOutput);
			}
		}

		/// <summary>Extracts the audio track as MP3 (the whole duration).</summary>
		public Task<bool> ExtractAudioAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
			=> ConvertAudioAsync(inputPath, PulseAudioFormat.Mp3, outputPath, null, cancellationToken, progress);

		/// <summary>Copies the video stream without audio.</summary>
		public Task<bool> MuteVideoAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
			=> RunSilentAsync(new List<string>
			{
				"-y", "-hide_banner", "-nostdin", "-i", inputPath,
				"-map", "0:v:0?", "-c:v", "copy", "-an",
			}, outputPath, cancellationToken);

		/// <summary>Rotates the video clockwise by the given quarter turns (1..3).</summary>
		public Task<bool> RotateVideoAsync(string inputPath, string outputPath, int quarterTurns, CancellationToken cancellationToken = default)
		{
			var filter = (quarterTurns % 4) switch
			{
				1 => "transpose=1",
				2 => "transpose=1,transpose=1",
				3 => "transpose=2",
				_ => null,
			};

			return filter is null
				? Task.FromResult(false)
				: RunSilentAsync(new List<string>
				{
					"-y", "-hide_banner", "-nostdin", "-i", inputPath,
					"-map", "0:v:0?", "-map", "0:a:0?", "-vf", filter, "-c:a", "aac", "-b:a", "192k",
				}, outputPath, cancellationToken);
		}

		/// <summary>Flips the video horizontally or vertically.</summary>
		public Task<bool> FlipVideoAsync(string inputPath, string outputPath, bool horizontal, CancellationToken cancellationToken = default)
			=> RunSilentAsync(new List<string>
			{
				"-y", "-hide_banner", "-nostdin", "-i", inputPath,
				"-map", "0:v:0?", "-map", "0:a:0?", "-vf", horizontal ? "hflip" : "vflip", "-c:a", "aac", "-b:a", "192k",
			}, outputPath, cancellationToken);

		/// <summary>Runs arbitrary ffmpeg arguments (output path is appended by the runner) and moves the result into place.</summary>
		public Task<bool> RunCustomAsync(IReadOnlyList<string> args, string outputPath, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
			=> RunWithProgressAsync(args.ToList(), outputPath, 0, cancellationToken, progress);

		/// <summary>Runs an analysis pass (`-f null -`) and returns ffmpeg's combined log.</summary>
		public async Task<string> RunAnalysisAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
		{
			var psi = NewFfmpegProcess();
			foreach (var arg in args)
				psi.ArgumentList.Add(arg);
			psi.ArgumentList.Add("-f");
			psi.ArgumentList.Add("null");
			psi.ArgumentList.Add("-");
			return await PulseProcess.TextAsync(psi, cancellationToken, TimeSpan.FromMinutes(15));
		}

		/// <summary>Probes the binary's capability list (`-filters`, `-encoders`, `-hwaccels`).</summary>
		public async Task<IReadOnlyList<string>> ProbeCapabilitiesAsync(string switchName, CancellationToken cancellationToken = default)
		{
			var psi = NewFfmpegProcess();
			psi.ArgumentList.Add(switchName);
			var text = await PulseProcess.TextAsync(psi, cancellationToken, TimeSpan.FromSeconds(30));
			return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		}

		private static string FormatSeconds(TimeSpan value)
			=> value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

		private static string TemporaryOutputPath(string output)
			=> Path.Combine(Path.GetDirectoryName(output) ?? string.Empty,
				$".{Path.GetFileNameWithoutExtension(output)}.{Guid.NewGuid():N}.pulse{Path.GetExtension(output)}");

		// ---- media preview gate (FFmpeg fallback for non-MediaFoundation codecs) ---------

		/// <summary>
		/// Whether the file is a container/codec pair MediaFoundation can decode natively (so the
		/// preview can play it directly and stay seekable). Unknown/hostile inputs are treated as
		/// needing a transcode so the preview never silently fails.
		/// </summary>
		public async Task<bool> IsMediaFoundationPlayableAsync(string path, CancellationToken cancellationToken = default)
		{
			var psi = NewProbeProcess(FfprobePath);
			psi.ArgumentList.Add("-v");
			psi.ArgumentList.Add("error");
			psi.ArgumentList.Add("-show_entries");
			psi.ArgumentList.Add("stream=codec_name,codec_type");
			psi.ArgumentList.Add("-of");
			psi.ArgumentList.Add("csv=p=0");
			psi.ArgumentList.Add(path);

			var output = await PulseProcess.TextAsync(psi, cancellationToken, TimeSpan.FromSeconds(20));

			var hasVideo = false;
			var hasAudio = false;
			foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var parts = line.Split(',');
				if (parts.Length < 2)
					continue;
				var type = parts[1];
				var codec = parts[0];
				if (type is "video")
				{
					hasVideo = true;
					if (!IsMfVideoCodec(codec))
						return false;
				}
				else if (type is "audio")
				{
					hasAudio = true;
					if (!IsMfAudioCodec(codec))
						return false;
				}
			}

			// At least one playable stream, and every video/audio codec MF knows.
			return hasVideo || hasAudio;
		}

		private static bool IsMfVideoCodec(string codec) => codec switch
		{
			"h264" or "avc1" or "mpeg4" or "mjpeg" => true,
			_ => false,
		};

		private static bool IsMfAudioCodec(string codec) => codec switch
		{
			"aac" or "mp3" or "pcm_s16le" or "pcm_s24le" or "pcm_f32le" or "flac" => true,
			_ => false,
		};

		/// <summary>
		/// Transcodes the file to a playable, seekable MP4 (H.264 + AAC) so the preview player can
		/// render codecs MediaFoundation cannot decode (HEVC without extension, VP9, AV1, Opus-only,
		/// MKV, ...). Returns the temp output path on success; the caller owns its deletion.
		/// </summary>
		public async Task<string?> TranscodeForPreviewAsync(string inputPath, string tempOutputPath, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
		{
			var duration = await ProbeDurationAsync(inputPath, cancellationToken);
			var args = new List<string>
			{
				"-y", "-hide_banner", "-nostdin", "-i", inputPath,
				"-map", "0:v:0?", "-map", "0:a:0?",
				"-c:v", "libx264", "-preset", "veryfast", "-crf", "24", "-pix_fmt", "yuv420p",
				"-vf", "scale=ceil(iw/2)*2:ceil(ih/2)*2", "-profile:v", "main",
				"-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart"
			};
			return await RunWithProgressAsync(args, tempOutputPath, duration?.TotalSeconds ?? 0, cancellationToken, progress)
				? tempOutputPath : null;
		}

		/// <summary>Runs <c>-version</c> on one candidate with a bounded wait; null when it fails.</summary>
		private static string? TryGetVersion(string executable, CancellationToken cancellationToken)
		{
			try
			{
				var psi = NewProcess(executable);
				psi.ArgumentList.Add("-version");
				var output = PulseProcess.TextAsync(psi, cancellationToken, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
				return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
			}
			catch (OperationCanceledException) { throw; }
			catch (Exception ex)
			{
				LastLocateError = ex.Message;
				return null;
			}
		}

		private static IEnumerable<string> GetNaiOsRoots()
		{
			var roots = new List<string>();
			try
			{
				if (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { } local)
				{
					roots.Add(Path.Combine(local, "Programs", "NAI OS"));
					roots.Add(Path.Combine(local, "NAI OS"));
				}
			}
			catch { }
			try
			{
				if (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) is { } programs)
					roots.Add(Path.Combine(programs, "NAI OS"));
			}
			catch { }
			return roots;
		}

		private static void Kill(Process process)
		{
			try { process.Kill(entireProcessTree: true); } catch { }
			try { process.WaitForExit(2000); } catch { }
		}

		private static void TryDelete(string path)
		{
			try { if (File.Exists(path)) File.Delete(path); } catch { }
		}
	}
}
