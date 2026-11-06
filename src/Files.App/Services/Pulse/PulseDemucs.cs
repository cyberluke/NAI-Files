// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace Files.App.Services.Pulse
{
	public enum PulseSplitMode
	{
		TwoStems,
		FourStems,
	}

	public enum PulseSplitFormat
	{
		Wav,
		Flac,
		Mp3,
	}

	/// <summary>Split options: stem count and output encoding.</summary>
	public sealed record PulseSplitOptions(PulseSplitMode Mode, PulseSplitFormat Format);

	/// <summary>One separated stem written next to the source file.</summary>
	public sealed record PulseStemResult(string DisplayName, string Path);

	/// <summary>Progress of a split run; <see cref="Percent"/> is null while the stage is indeterminate.</summary>
	public sealed record PulseSplitProgress(double? Percent, string Stage);

	/// <summary>
	/// Thin wrapper around the Demucs 4 source-separation runtime bundled with the app
	/// (and, as a fallback, the copy shipped inside NAI OS, then any Python on PATH that
	/// can import demucs). Powers the Pulse "Split" feature: the audio of any
	/// FFmpeg-readable file (audio or video — the video's audio track is extracted
	/// first) is separated with the htdemucs model into stems (vocals/drums/bass/other,
	/// or vocals + instrumental). The bundled runtime is an offline embeddable Python +
	/// PyTorch CPU environment; every process runs hidden and cancellation kills the
	/// whole child tree.
	/// </summary>
	public sealed class PulseDemucs
	{
		private const string ModelName = "htdemucs";
		private static PulseDemucs? located;
		private static int busy;

		public string PythonPath { get; }
		public string HfHome { get; }
		public string RootPath { get; }
		public string DemucsVersion { get; }
		public string TorchVersion { get; }
		/// <summary>Raw device probe: "cpu", or the NVIDIA GPU name on a CUDA build with a visible GPU.</summary>
		public string? DeviceName { get; }
		public bool IsBundled { get; }

		/// <summary>Whether separation runs on the NVIDIA GPU (CUDA build + working driver).</summary>
		public bool IsCuda => !string.IsNullOrEmpty(DeviceName) && !string.Equals(DeviceName, "cpu", StringComparison.OrdinalIgnoreCase);

		/// <summary>Human label for the dialog, e.g. "CPU" or "CUDA (NVIDIA GeForce RTX 4090)".</summary>
		public string DeviceLabel => IsCuda ? $"CUDA ({DeviceName})" : "CPU";

		private PulseDemucs(string pythonPath, string hfHome, string rootPath, string demucsVersion, string torchVersion, string? deviceName, bool isBundled)
		{
			PythonPath = pythonPath;
			HfHome = hfHome;
			RootPath = rootPath;
			DemucsVersion = demucsVersion;
			TorchVersion = torchVersion;
			DeviceName = deviceName;
			IsBundled = isBundled;
		}

		/// <summary>Whether a split is currently running (the runtime is CPU-heavy; only one runs at a time).</summary>
		public static bool IsBusy => Volatile.Read(ref busy) != 0;
		private static readonly PulseRuntimeDiscovery<PulseDemucs> Discovery = new(Locate);
		public static string? LastLocateError { get; private set; }
		public static async Task<PulseDemucs> RequireAsync(CancellationToken cancellationToken = default)
		{
			return await Discovery.GetAsync(cancellationToken)
				?? throw new IOException(string.Format(Strings.PulseMediaRuntimeUnavailable.GetLocalizedResource(), LastLocateError ?? "Demucs / PyTorch"));
		}

		/// <summary>
		/// Resolves the Demucs runtime once per session. Order: the copy inside this
		/// package, the copy inside an installed NAI OS, then whatever Python resolves
		/// on PATH. Every candidate must import demucs + torch within a bounded run;
		/// the bundled candidate must already carry the htdemucs weights.
		/// </summary>
		public static PulseDemucs? Locate(CancellationToken cancellationToken = default)
		{
			if (located is not null)
				return located;

			// Packaged: spawn python/demucs from WindowsApps is ACL-denied; stage to LocalCache first.
			var failures = new List<string>();
			try { PulseRuntimeStager.StageDemucs(cancellationToken); }
			catch (OperationCanceledException) { throw; }
			catch (Exception ex) { failures.Add(ex.Message); }

			var candidates = new List<(string Python, string? Root)>();
			var packaged = false;

			// Staged writable copy (packaged run) — most reliable.
			if (PulseRuntimeStager.IsDemucsStaged && PulseRuntimeStager.GetStagedRoot() is { } stagedRoot)
			{
				candidates.Add((Path.Combine(stagedRoot, "demucs", "python", "python.exe"), Path.Combine(stagedRoot, "demucs")));
			}

			// Packaged: content items land next to the app binaries.
			try
			{
				if (Package.Current is { } package)
				{
					packaged = true;
					var root = package.InstalledLocation.Path;
					candidates.Add((Path.Combine(root, "demucs", "python", "python.exe"), Path.Combine(root, "demucs")));
				}
			}
			catch
			{
				// Unpackaged (dev) run: Package.Current throws.
			}

			if (!packaged)
			{
				candidates.Add((Path.Combine(AppContext.BaseDirectory, "demucs", "python", "python.exe"), Path.Combine(AppContext.BaseDirectory, "demucs")));
				foreach (var root in GetNaiOsRoots())
					candidates.Add((Path.Combine(root, "static", "demucs", "python", "python.exe"), Path.Combine(root, "static", "demucs")));
				candidates.Add(("python.exe", null));
			}

			foreach (var (python, root) in candidates)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var isBareName = !python.Contains(Path.DirectorySeparatorChar) && !python.Contains(Path.AltDirectorySeparatorChar);
				if (!isBareName && !File.Exists(python))
					continue;

				var hfHome = root is null ? null : Path.Combine(root, "hf_home");
				if (TryGetVersions(python, hfHome, cancellationToken) is not { } versions)
				{
					if (LastLocateError is not null) failures.Add(LastLocateError);
					continue;
				}

				// The bundled runtime must be fully offline: weights already on disk
				// in the HuggingFace cache (demucs 4.1 loads htdemucs from the hub).
				if (hfHome is not null)
				{
					var modelCache = Path.Combine(hfHome, "hub", "models--adefossez--HTDemucs");
					if (!Directory.Exists(modelCache) || !Directory.EnumerateFiles(modelCache, "*.safetensors", SearchOption.AllDirectories).Any())
					{
						failures.Add(modelCache);
						continue;
					}
				}

				return located = new PulseDemucs(python, hfHome ?? string.Empty, root ?? string.Empty, versions.Demucs, versions.Torch, versions.Device, root is not null);
			}

			LastLocateError = string.Join(Environment.NewLine, failures.Distinct());
			return null;
		}

		/// <summary>
		/// Separates the audio of <paramref name="sourcePath"/> (any FFmpeg-readable
		/// audio or video) into stems written next to the source as
		/// "&lt;name&gt; - &lt;Stem&gt;.&lt;ext&gt;". Only one split runs at a time.
		/// </summary>
		public async Task<IReadOnlyList<PulseStemResult>> SplitAsync(string sourcePath, PulseSplitOptions options, IProgress<PulseSplitProgress>? progress = null, CancellationToken cancellationToken = default)
		{
			if (Interlocked.Exchange(ref busy, 1) != 0)
				throw new InvalidOperationException(Strings.PulseSplitBusy.GetLocalizedResource());

			try
			{
				return await Task.Run(() => SplitCoreAsync(sourcePath, options, progress, cancellationToken), cancellationToken);
			}
			finally
			{
				Volatile.Write(ref busy, 0);
			}
		}

		private async Task<IReadOnlyList<PulseStemResult>> SplitCoreAsync(string sourcePath, PulseSplitOptions options, IProgress<PulseSplitProgress>? progress, CancellationToken cancellationToken)
		{
			var ffmpeg = await PulseFfmpeg.RequireAsync(cancellationToken);

			var tempRoot = Path.Combine(Path.GetTempPath(), "PulseDemucs", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(tempRoot);

			try
			{
				// Demucs reads WAV natively; normalize any audio/video to 44.1 kHz
				// stereo PCM through the bundled FFmpeg first (covers video tracks).
				var inputWav = Path.Combine(tempRoot, "input.wav");
				progress?.Report(new PulseSplitProgress(3, Strings.PulseSplitExtracting.GetLocalizedResource()));
				if (!await ffmpeg.ConvertAudioAsync(sourcePath, PulseAudioFormat.Wav, inputWav, null, cancellationToken))
					throw new InvalidOperationException(Strings.PulseSplitNoAudio.GetLocalizedResource());

				var stemsDir = Path.Combine(tempRoot, "stems");
				Directory.CreateDirectory(stemsDir);

				progress?.Report(new PulseSplitProgress(8, Strings.PulseSplitLoadingModel.GetLocalizedResource()));

				var args = new List<string>
				{
					"-m", "demucs",
					"-n", ModelName,
					"-d", IsCuda ? "cuda" : "cpu",
					"-o", stemsDir,
				};
				if (options.Mode is PulseSplitMode.TwoStems)
					args.Add("--two-stems=vocals");
				args.Add(inputWav);

				progress?.Report(new PulseSplitProgress(null, Strings.PulseSplitSeparating.GetLocalizedResource()));
				var (exitCode, details) = await RunPythonAsync(args, cancellationToken, progress);
				if (exitCode != 0)
					throw new InvalidOperationException($"{Strings.PulseSplitFailed.GetLocalizedResource()} {Truncate(details, 400)}");

				// demucs writes <out>/htdemucs/<track>/<stem>.wav (track name = "input").
				var trackDir = Path.Combine(stemsDir, ModelName, "input");
				var stems = Directory.Exists(trackDir)
					? Directory.GetFiles(trackDir, "*.wav").OrderBy(Path.GetFileName).ToList()
					: [];
				if (stems.Count == 0)
					throw new InvalidOperationException(Strings.PulseSplitFailed.GetLocalizedResource());

				var spec = PulseAudioFormatSpec.For(ToAudioFormat(options.Format));
				var outputDir = Path.GetDirectoryName(sourcePath) ?? string.Empty;
				var baseName = Path.GetFileNameWithoutExtension(sourcePath);
				var results = new List<PulseStemResult>(stems.Count);

				progress?.Report(new PulseSplitProgress(92, Strings.PulseSplitConverting.GetLocalizedResource()));
				for (int i = 0; i < stems.Count; i++)
				{
					cancellationToken.ThrowIfCancellationRequested();

					var label = ToDisplayName(Path.GetFileNameWithoutExtension(stems[i]));
					var outputPath = ResolveOutputPath(outputDir, $"{baseName} - {label}", spec.Extension);
					if (options.Format is PulseSplitFormat.Wav)
						File.Move(stems[i], outputPath);
					else if (!await ffmpeg.ConvertAudioAsync(stems[i], ToAudioFormat(options.Format), outputPath, null, cancellationToken))
						throw new InvalidOperationException(Strings.PulseSplitFailed.GetLocalizedResource());

					results.Add(new PulseStemResult(label, outputPath));
					progress?.Report(new PulseSplitProgress(92 + (8 * (i + 1) / Math.Max(1, stems.Count)), Strings.PulseSplitConverting.GetLocalizedResource()));
				}

				return results;
			}
			finally
			{
				try { Directory.Delete(tempRoot, recursive: true); } catch { }
			}
		}

		// ---- process plumbing -----------------------------------------------------

		private async Task<(int ExitCode, string Details)> RunPythonAsync(List<string> args, CancellationToken cancellationToken, IProgress<PulseSplitProgress>? progress)
		{
			var psi = NewPythonProcess(PythonPath, HfHome);
			foreach (var arg in args)
				psi.ArgumentList.Add(arg);

			await PulseProcess.RunAsync(psi, async (stream, token) =>
			{
				using var reader = new StreamReader(stream, leaveOpen: true);
				string? line;
				while ((line = await reader.ReadLineAsync(token).ConfigureAwait(false)) is not null)
					TryParseDemucsProgress(line, progress);
				return true;
			}, cancellationToken, errorOutput: line => TryParseDemucsProgress(line, progress));
			return (0, string.Empty);
		}

		private static ProcessStartInfo NewPythonProcess(string python, string? hfHome)
		{
			var psi = new ProcessStartInfo(python)
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = true,
			};
			psi.Environment.Remove("PYTHONHOME");
			psi.Environment.Remove("PYTHONPATH");
			psi.Environment["PYTHONNOUSERSITE"] = "1";
			psi.Environment["PYTHONUNBUFFERED"] = "1";
			psi.Environment["PYTHONUTF8"] = "1";
			if (!string.IsNullOrEmpty(hfHome))
			{
				// Offline model resolution: the htdemucs weights live in the bundled
				// HuggingFace cache; never let the runtime reach out to the hub.
				psi.Environment["HF_HOME"] = hfHome;
				psi.Environment["HF_HUB_OFFLINE"] = "1";
			}
			return psi;
		}

		/// <summary>Parses demucs <c>--json</c> progress lines ("loading model", "separating track", "done").</summary>
		private static void TryParseDemucsProgress(string line, IProgress<PulseSplitProgress>? progress)
		{
			if (progress is null)
				return;
			if (!line.TrimStart().StartsWith('{'))
			{
				var match = System.Text.RegularExpressions.Regex.Match(line, @"(\d{1,3})%\|");
				if (match.Success && double.TryParse(match.Groups[1].Value, out var percent))
					progress.Report(new PulseSplitProgress(8 + 0.84 * Math.Clamp(percent, 0, 100), Strings.PulseSplitSeparating.GetLocalizedResource()));
				return;
			}

			try
			{
				using var doc = JsonDocument.Parse(line);
				var root = doc.RootElement;
				if (!root.TryGetProperty("status", out var statusProperty) || statusProperty.ValueKind is not JsonValueKind.String)
					return;

				var status = statusProperty.GetString();
				string? stage = status switch
				{
					"loading model" => Strings.PulseSplitLoadingModel.GetLocalizedResource(),
					"separating track" or "separating" => Strings.PulseSplitSeparating.GetLocalizedResource(),
					_ => null,
				};
				if (stage is null)
					return;

				double? percent = status switch
				{
					"loading model" => 8,
					_ => 50,
				};
				if (root.TryGetProperty("progress", out var progressProperty) && progressProperty.ValueKind is JsonValueKind.Number)
					percent = Math.Clamp(progressProperty.GetDouble() * 100, 0, 100);

				progress.Report(new PulseSplitProgress(percent, stage));
			}
			catch
			{
				// Not a JSON progress line; ignore.
			}
		}

		// ---- helpers ---------------------------------------------------------------

		/// <summary>Bounded import probe: demucs + torch must import and print versions (plus the device).</summary>
		private static (string Demucs, string Torch, string? Device)? TryGetVersions(string python, string? hfHome, CancellationToken cancellationToken)
		{
			try
			{
				var psi = NewPythonProcess(python, hfHome);
				psi.ArgumentList.Add("-c");
				psi.ArgumentList.Add("import demucs, torch; import importlib.metadata as m; print(m.version('demucs')); print(torch.__version__); print(torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'cpu')");

				var stdout = PulseProcess.TextAsync(psi, cancellationToken, TimeSpan.FromMinutes(2)).GetAwaiter().GetResult();
				var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (lines.Length < 2)
					return null;
				return (lines[0], lines[1], lines.Length >= 3 ? lines[2] : null);
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

		private static PulseAudioFormat ToAudioFormat(PulseSplitFormat format) => format switch
		{
			PulseSplitFormat.Flac => PulseAudioFormat.Flac,
			PulseSplitFormat.Mp3 => PulseAudioFormat.Mp3,
			_ => PulseAudioFormat.Wav,
		};

		private static string ToDisplayName(string stem)
		{
			var text = stem.Replace('_', ' ');
			return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLower(CultureInfo.CurrentCulture));
		}

		/// <summary>"song - Vocals.wav" -> "song - Vocals (2).wav" on collision.</summary>
		private static string ResolveOutputPath(string directory, string stem, string extension)
		{
			var candidate = Path.Combine(directory, $"{stem}.{extension}");
			var counter = 2;
			while (File.Exists(candidate))
				candidate = Path.Combine(directory, $"{stem} ({counter++}).{extension}");
			return candidate;
		}

		private static string Truncate(string text, int max)
			=> text.Length <= max ? text : text[..max];
	}
}
