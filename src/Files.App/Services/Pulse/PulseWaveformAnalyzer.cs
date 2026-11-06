// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Runtime.InteropServices.Marshalling;
using System.Security.Cryptography;
using System.Text;
using Windows.Storage;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace Files.App.Services.Pulse
{
	/// <summary>
	/// Decodes an audio file offline through the Media Foundation source reader (any format the Windows
	/// media stack can read, as fast as the CPU decodes rather than in real time), measures each 512-sample
	/// block's peak and its energy in three bands (bass / mids / highs) with a small FFT, and folds the blocks
	/// into <see cref="PulseWaveform.DefaultColumns"/> time slices. Results are cached on disk per file
	/// (path + size + modified time), so a folder is analyzed once and painted instantly afterwards.
	/// The analysis kernel lives in <see cref="PulseWaveformAccumulator"/> so the FFmpeg-based trim dialog
	/// path (which also covers video containers) shares the exact same rendering.
	/// </summary>
	public static partial class PulseWaveformAnalyzer
	{
		private const int CacheVersion = 2;
		private const int BlockSize = 512;

		// MF_SDK_VERSION << 16 | MF_API_VERSION
		private const uint MediaFoundationVersion = 0x00020070;

		private static readonly string cacheRoot = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "pulse-waveforms");
		private static readonly Lazy<bool> mediaFoundationReady = new(() => PInvoke.MFStartup(MediaFoundationVersion, PInvoke.MFSTARTUP_FULL).Succeeded);

		/// <summary>Returns the cached waveform or analyzes the file; null when the file cannot be decoded.</summary>
		public static Task<PulseWaveform?> GetOrAnalyzeAsync(string path, CancellationToken token)
		{
			var info = new FileInfo(path);
			if (!info.Exists)
				return Task.FromResult<PulseWaveform?>(null);

			var cacheFile = GetCachePath(path, info);
			if (TryReadCache(cacheFile) is { } cached)
				return Task.FromResult<PulseWaveform?>(cached);

			token.ThrowIfCancellationRequested();

			var waveform = Analyze(path, token);
			if (waveform is not null)
				WriteCache(cacheFile, waveform);

			return Task.FromResult(waveform);
		}

		// Cache

		private static string GetCachePath(string path, FileInfo info)
		{
			var key = $"{CacheVersion}|{path.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
			var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
			return Path.Combine(cacheRoot, hash[..2], hash + ".pwf");
		}

		private static PulseWaveform? TryReadCache(string cacheFile)
		{
			try
			{
				if (!File.Exists(cacheFile))
					return null;

				using var stream = File.OpenRead(cacheFile);
				using var reader = new BinaryReader(stream);
				if (reader.ReadInt32() != CacheVersion)
					return null;

				var columns = reader.ReadInt32();
				if (columns is <= 0 or > 4096)
					return null;

				var duration = TimeSpan.FromTicks(reader.ReadInt64());
				var peaks = reader.ReadBytes(columns);
				var low = reader.ReadBytes(columns);
				var mid = reader.ReadBytes(columns);
				var high = reader.ReadBytes(columns);
				if (peaks.Length != columns || low.Length != columns || mid.Length != columns || high.Length != columns)
					return null;

				return new PulseWaveform(columns, peaks, low, mid, high, duration);
			}
			catch
			{
				return null;
			}
		}

		private static void WriteCache(string cacheFile, PulseWaveform waveform)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
				var temp = cacheFile + ".tmp";
				using (var stream = File.Create(temp))
				using (var writer = new BinaryWriter(stream))
				{
					writer.Write(CacheVersion);
					writer.Write(waveform.Columns);
					writer.Write(waveform.Duration.Ticks);
					writer.Write(waveform.Peaks);
					writer.Write(waveform.Low);
					writer.Write(waveform.Mid);
					writer.Write(waveform.High);
				}
				File.Move(temp, cacheFile, overwrite: true);
			}
			catch
			{
				// A missing cache entry only costs a re-analysis
			}
		}

		// Decoding (Media Foundation source reader, synchronous, on the calling worker thread)

		private static unsafe PulseWaveform? Analyze(string path, CancellationToken token)
		{
			if (!mediaFoundationReady.Value)
				return null;

			IMFSourceReader? reader = null;
			IMFMediaType? requestedType = null;
			IMFMediaType? currentType = null;
			try
			{
				if (PInvoke.MFCreateSourceReaderFromURL(path, null, out reader).Failed)
					return null;

				var audioStream = unchecked((uint)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_FIRST_AUDIO_STREAM);
				reader.SetStreamSelection(unchecked((uint)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_ALL_STREAMS), false);
				if (reader.SetStreamSelection(audioStream, true).Failed)
					return null;

				// Ask the reader for 32-bit float PCM at the source's own rate/channels: it inserts the decoder itself
				if (PInvoke.MFCreateMediaType(out requestedType).Failed)
					return null;
				requestedType.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
				requestedType.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFAudioFormat_Float);
				if (reader.SetCurrentMediaType(audioStream, requestedType).Failed)
					return null;

				if (reader.GetCurrentMediaType(audioStream, out currentType).Failed)
					return null;

				currentType.GetUINT32(PInvoke.MF_MT_AUDIO_NUM_CHANNELS, out var channels);
				currentType.GetUINT32(PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, out var sampleRate);
				if (channels == 0 || sampleRate == 0)
					return null;

				var accumulator = new PulseWaveformAccumulator((int)channels, (int)sampleRate);
				var blocksSinceCancelCheck = 0;

				while (true)
				{
					IMFSample? sample = null;
					try
					{
						if (reader.ReadSample(audioStream, 0, out _, out var flags, out _, out sample).Failed)
							return accumulator.HasData ? accumulator.Build() : null;

						var readerFlags = (MF_SOURCE_READER_FLAG)flags;
						if (readerFlags.HasFlag(MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM))
							break;
						if (readerFlags.HasFlag(MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR))
							return accumulator.HasData ? accumulator.Build() : null;

						if (readerFlags.HasFlag(MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED))
						{
							Release(currentType);
							if (reader.GetCurrentMediaType(audioStream, out currentType).Succeeded)
							{
								currentType.GetUINT32(PInvoke.MF_MT_AUDIO_NUM_CHANNELS, out channels);
								currentType.GetUINT32(PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, out sampleRate);
								accumulator.UpdateFormat((int)channels, (int)sampleRate);
							}
						}

						if (sample is null)
							continue;

						ReadSample(sample, accumulator);
					}
					finally
					{
						Release(sample);
					}

					if (++blocksSinceCancelCheck >= 64)
					{
						blocksSinceCancelCheck = 0;
						token.ThrowIfCancellationRequested();
					}
				}

				return accumulator.HasData ? accumulator.Build() : null;
			}
			finally
			{
				Release(currentType);
				Release(requestedType);
				Release(reader);
			}
		}

		private static unsafe void ReadSample(IMFSample sample, PulseWaveformAccumulator accumulator)
		{
			IMFMediaBuffer? buffer = null;
			try
			{
				if (sample.ConvertToContiguousBuffer(out buffer).Failed)
					return;

				if (buffer.Lock(out var data, out _, out var currentLength).Failed)
					return;
				try
				{
					accumulator.Add(new ReadOnlySpan<float>(data, (int)(currentLength / sizeof(float))));
				}
				finally
				{
					buffer.Unlock();
				}
			}
			finally
			{
				Release(buffer);
			}
		}

		private static void Release(object? comObject)
		{
			// Source-generated COM wrappers are ComObjects: release the native reference now instead of at finalization
			if (comObject is ComObject com)
				com.FinalRelease();
		}
	}
}