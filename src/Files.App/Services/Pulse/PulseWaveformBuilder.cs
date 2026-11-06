// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Services.Pulse
{
	/// <summary>
	/// Shared spectral-waveform analysis kernel. Consumes raw PCM frames (float, interleaved, any
	/// channel count) streamed in arbitrary chunks, mixes them to mono, measures each 512-sample
	/// block's peak and its energy in three bands (bass / mids / highs) with a small FFT, and folds
	/// the blocks into <see cref="PulseWaveform.DefaultColumns"/> time slices.
	/// Used by both analysis paths: the Media Foundation source reader
	/// (<see cref="PulseWaveformAnalyzer"/>) and the FFmpeg PCM pipe used by the Pulse trim dialog
	/// (which also covers video containers like MP4/WebM/MKV that MF cannot always decode).
	/// </summary>
	public sealed class PulseWaveformAccumulator
	{
		private const int BlockSize = 512;
		private const double LowCutoffHz = 250;
		private const double HighCutoffHz = 4000;

		private static readonly double[] hannWindow = CreateHann(BlockSize);

		private readonly List<BlockStats> blocks = new(4096);
		private readonly double[] fftReal = new double[BlockSize];
		private readonly double[] fftImag = new double[BlockSize];
		private readonly float[] pending = new float[BlockSize];
		private int channels;
		private int sampleRate;
		private int lowBins;
		private int highBins;
		private int pendingCount;
		private float pendingPeak;
		private long frames;
		private int frameChannel;
		private float frameSum;

		public PulseWaveformAccumulator(int channels, int sampleRate)
		{
			UpdateFormat(channels, sampleRate);
		}

		/// <summary>True once at least one sample was added (or is buffered).</summary>
		public bool HasData => blocks.Count > 0 || pendingCount > 0;

		/// <summary>Adjusts for a stream format change (e.g. a Media Foundation media-type switch).</summary>
		public void UpdateFormat(int channels, int sampleRate)
		{
			this.channels = Math.Max(1, channels);
			this.sampleRate = Math.Max(8000, sampleRate);
			lowBins = Math.Max(1, (int)(LowCutoffHz * BlockSize / this.sampleRate));
			highBins = Math.Max(lowBins + 1, (int)(HighCutoffHz * BlockSize / this.sampleRate));
		}

		/// <summary>Adds interleaved float samples, mixing channels to mono per frame.</summary>
		public void Add(ReadOnlySpan<float> samples)
		{
			foreach (var sample in samples)
			{
				frameSum += sample;
				if (++frameChannel < channels)
					continue;

				var mono = frameSum / channels;
				frameChannel = 0;
				frameSum = 0;
				frames++;

				var magnitude = Math.Abs(mono);
				if (magnitude > pendingPeak)
					pendingPeak = magnitude;

				pending[pendingCount++] = mono;
				if (pendingCount == BlockSize)
					FlushBlock();
			}
		}

		/// <summary>Folds the accumulated blocks into a <see cref="PulseWaveform"/>.</summary>
		public PulseWaveform Build()
		{
			if (pendingCount > 0)
				FlushBlock();

			var columns = PulseWaveform.DefaultColumns;
			var peaks = new float[columns];
			var low = new double[columns];
			var mid = new double[columns];
			var high = new double[columns];

			// Fold the block list onto the columns: max peak, summed band energy per slice
			var blockCount = blocks.Count;
			for (int column = 0; column < columns; column++)
			{
				var start = (int)((long)column * blockCount / columns);
				var end = (int)((long)(column + 1) * blockCount / columns);
				if (end <= start)
					end = Math.Min(blockCount, start + 1);

				for (int b = start; b < end; b++)
				{
					var block = blocks[b];
					peaks[column] = Math.Max(peaks[column], block.Peak);
					low[column] += block.Low;
					mid[column] += block.Mid;
					high[column] += block.High;
				}
			}

			var maxPeak = 0f;
			foreach (var peak in peaks)
				maxPeak = Math.Max(maxPeak, peak);
			if (maxPeak <= 0)
				maxPeak = 1;

			var peakBytes = new byte[columns];
			var lowBytes = new byte[columns];
			var midBytes = new byte[columns];
			var highBytes = new byte[columns];

			for (int i = 0; i < columns; i++)
			{
				// Perceptual peak: sqrt compresses the dynamic range the way DJ software does
				peakBytes[i] = (byte)Math.Clamp(Math.Sqrt(peaks[i] / maxPeak) * 255, 0, 255);

				// Band shares weighted so highs (naturally low energy) still register
				var l = low[i];
				var m = mid[i] * 1.6;
				var h = high[i] * 4.0;
				var total = l + m + h;
				if (total <= 0)
				{
					lowBytes[i] = midBytes[i] = highBytes[i] = 85;
					continue;
				}

				lowBytes[i] = (byte)Math.Clamp(l / total * 255, 0, 255);
				midBytes[i] = (byte)Math.Clamp(m / total * 255, 0, 255);
				highBytes[i] = (byte)Math.Clamp(h / total * 255, 0, 255);
			}

			var duration = TimeSpan.FromSeconds(frames / (double)sampleRate);
			return new PulseWaveform(columns, peakBytes, lowBytes, midBytes, highBytes, duration);
		}

		private void FlushBlock()
		{
			for (int i = 0; i < BlockSize; i++)
			{
				fftReal[i] = i < pendingCount ? pending[i] * hannWindow[i] : 0;
				fftImag[i] = 0;
			}

			Fft(fftReal, fftImag);

			double l = 0, m = 0, h = 0;
			for (int bin = 1; bin < BlockSize / 2; bin++)
			{
				var energy = fftReal[bin] * fftReal[bin] + fftImag[bin] * fftImag[bin];
				if (bin <= lowBins)
					l += energy;
				else if (bin <= highBins)
					m += energy;
				else
					h += energy;
			}

			blocks.Add(new BlockStats(pendingPeak, (float)l, (float)m, (float)h));
			pendingCount = 0;
			pendingPeak = 0;
		}

		private static double[] CreateHann(int size)
		{
			var window = new double[size];
			for (int i = 0; i < size; i++)
				window[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (size - 1)));
			return window;
		}

		/// <summary>In-place iterative radix-2 FFT.</summary>
		private static void Fft(double[] real, double[] imag)
		{
			int n = real.Length;
			for (int i = 1, j = 0; i < n; i++)
			{
				int bit = n >> 1;
				for (; (j & bit) != 0; bit >>= 1)
					j ^= bit;
				j ^= bit;
				if (i < j)
				{
					(real[i], real[j]) = (real[j], real[i]);
					(imag[i], imag[j]) = (imag[j], imag[i]);
				}
			}

			for (int length = 2; length <= n; length <<= 1)
			{
				double angle = -2 * Math.PI / length;
				double wReal = Math.Cos(angle), wImag = Math.Sin(angle);
				for (int i = 0; i < n; i += length)
				{
					double curReal = 1, curImag = 0;
					for (int j = 0; j < length / 2; j++)
					{
						int a = i + j, b = i + j + length / 2;
						double tReal = real[b] * curReal - imag[b] * curImag;
						double tImag = real[b] * curImag + imag[b] * curReal;
						real[b] = real[a] - tReal;
						imag[b] = imag[a] - tImag;
						real[a] += tReal;
						imag[a] += tImag;
						(curReal, curImag) = (curReal * wReal - curImag * wImag, curReal * wImag + curImag * wReal);
					}
				}
			}
		}

		private readonly record struct BlockStats(float Peak, float Low, float Mid, float High);
	}
}