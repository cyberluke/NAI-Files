// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;

namespace Files.App.Services.Pulse
{
	/// <summary>
	/// Materializes a playable, seekable media file for the preview players and exposes it as a
	/// <see cref="MediaSource"/>. Files MediaFoundation can decode are played directly (fast, seekable);
	/// everything else (HEVC, VP9, AV1, Opus-only, MKV, ...) is transcoded in the background to a
	/// standards-compliant H.264+AAC MP4 with the bundled FFmpeg so the user sees a preview no matter what.
	/// </summary>
	/// <remarks>
	/// The caller owns the returned <see cref="PulseMediaPlayback"/>. Dispose it after the surface is
	/// unloaded to delete the temp file and release the source. The gate is safe to reuse per surface.
	/// </remarks>
	internal sealed class PulseMediaGate
	{
		private readonly PulseFfmpeg? ffmpeg;

		public PulseMediaGate(PulseFfmpeg? ffmpeg)
			=> this.ffmpeg = ffmpeg;

		/// <summary>
		/// Prepares playback for <paramref name="path"/>: returns a direct source when MF supports the
		/// file, otherwise transcodes to a temp MP4 and returns that. May report 0..1 progress while
		/// transcoding. Returns null when neither path works or FFmpeg is unavailable.
		/// </summary>
		public async Task<PulseMediaPlayback?> PrepareAsync(
			string path,
			CancellationToken cancellationToken = default,
			IProgress<double>? progress = null,
			bool forceCompatible = false)
		{
			string? tempPath = null;
			try
			{
				if (!forceCompatible && ffmpeg is not null && await ffmpeg.IsMediaFoundationPlayableAsync(path, cancellationToken))
					return PulseMediaPlayback.Direct(path);

				if (ffmpeg is null)
					return PulseMediaPlayback.Direct(path);

				// Transcode to a temp MP4 that MediaFoundation can always play.
				var tempDir = Path.Combine(Path.GetTempPath(), "PulseMediaPreview");
				Directory.CreateDirectory(tempDir);
				tempPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}.mp4");

				var transcoded = await ffmpeg.TranscodeForPreviewAsync(path, tempPath, cancellationToken, progress);
				if (transcoded is null || !File.Exists(transcoded))
				{
					if (transcoded is not null)
					{
						try { File.Delete(transcoded); } catch { }
					}
					return null;
				}
				tempPath = transcoded;

				cancellationToken.ThrowIfCancellationRequested();
				var playback = PulseMediaPlayback.FromFile(tempPath);
				tempPath = null; // Ownership transfers to the playback lease, never a deletion timer.
				return playback;
			}
			finally
			{
				if (tempPath is not null)
				{
					try { File.Delete(tempPath); }
					catch (IOException) { }
					catch (UnauthorizedAccessException) { }
				}
			}
		}
	}

	/// <summary>A prepared preview: either the original file (MF plays it) or a temp MP4 that must be deleted on dispose.</summary>
	internal sealed class PulseMediaPlayback : IDisposable
	{
		public bool IsTranscoded { get; }
		public string Path { get; }
		public string? OwnedTempPath { get; }

		private PulseMediaPlayback(string path, bool isTranscoded, string? ownedTempPath)
		{
			Path = path;
			IsTranscoded = isTranscoded;
			OwnedTempPath = ownedTempPath;
		}

		public static PulseMediaPlayback Direct(string path)
			=> new(path, isTranscoded: false, ownedTempPath: null);

		public static PulseMediaPlayback FromFile(string tempPath)
			=> new(tempPath, isTranscoded: true, ownedTempPath: tempPath);

		public void Dispose()
		{
			if (OwnedTempPath is not null)
			{
				try { File.Delete(OwnedTempPath); } catch { }
			}
		}
	}
}
