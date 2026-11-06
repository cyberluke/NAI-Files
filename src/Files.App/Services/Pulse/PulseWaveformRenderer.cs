// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;

namespace Files.App.Services.Pulse
{
	/// <summary>
	/// Paints a <see cref="PulseWaveform"/> the way Traktor colours its decks: each column is a symmetric
	/// bar whose hue follows the spectral balance of that slice (bass → violet/blue, mids → the rainbow
	/// through green and yellow, highs → silver-white), on a dark navy ground. The output is a PNG byte
	/// array produced with no WinRT objects, so it can be rendered on a worker thread and handed to a
	/// BitmapImage on the UI thread like any other thumbnail.
	/// </summary>
	public static class PulseWaveformRenderer
	{
		private const int BarGap = 1;

		private static readonly (byte R, byte G, byte B) Background = (0x12, 0x0F, 0x2E);

		// Anchor colours along the spectral axis: bass → treble
		private static readonly (double R, double G, double B)[] Palette =
		[
			(0.55, 0.15, 0.95),   // violet (deep bass)
			(0.25, 0.35, 1.00),   // blue
			(0.10, 0.85, 1.00),   // cyan
			(0.20, 1.00, 0.35),   // green
			(1.00, 0.95, 0.20),   // yellow
			(1.00, 0.55, 0.15),   // orange
			(0.92, 0.92, 0.98),   // silver-white (highs)
		];

		public static byte[] RenderPng(PulseWaveform waveform, int width, int height)
		{
			width = Math.Max(16, width);
			height = Math.Max(8, height);

			// RGBA rows for the PNG encoder
			var pixels = new byte[width * height * 4];
			FillBackground(pixels);

			var columnWidth = width / (double)waveform.Columns;
			var center = height / 2.0;

			for (int column = 0; column < waveform.Columns; column++)
			{
				var x0 = (int)Math.Floor(column * columnWidth);
				var x1 = (int)Math.Floor((column + 1) * columnWidth) - BarGap;
				if (x1 < x0)
					x1 = x0;

				var peak = waveform.Peaks[column] / 255.0;
				var halfBar = Math.Max(1, (int)Math.Round(peak * (center - 1)));

				var (r, g, b) = ColorFor(waveform.Low[column], waveform.Mid[column], waveform.High[column]);

				// Slight vertical shading: brighter core, darker tips, like a lit bar
				for (int y = (int)center - halfBar; y <= (int)center + halfBar; y++)
				{
					if (y < 0 || y >= height)
						continue;

					var distance = Math.Abs(y - center) / Math.Max(1, halfBar);
					var shade = 1.0 - 0.35 * distance * distance;
					var pr = (byte)Math.Clamp(r * shade * 255, 0, 255);
					var pg = (byte)Math.Clamp(g * shade * 255, 0, 255);
					var pb = (byte)Math.Clamp(b * shade * 255, 0, 255);

					for (int x = x0; x <= x1 && x < width; x++)
					{
						var i = (y * width + x) * 4;
						pixels[i] = pr;
						pixels[i + 1] = pg;
						pixels[i + 2] = pb;
						pixels[i + 3] = 255;
					}
				}
			}

			return EncodePng(pixels, width, height);
		}

		private static void FillBackground(byte[] pixels)
		{
			for (int i = 0; i < pixels.Length; i += 4)
			{
				pixels[i] = Background.R;
				pixels[i + 1] = Background.G;
				pixels[i + 2] = Background.B;
				pixels[i + 3] = 255;
			}
		}

		/// <summary>
		/// Maps the band shares of a slice onto the spectral palette. The spectral centroid (0 = all bass,
		/// 1 = all highs) picks the hue; a strongly dominant band saturates the colour, a balanced slice
		/// desaturates slightly so busy passages read as lighter.
		/// </summary>
		private static (double R, double G, double B) ColorFor(byte low, byte mid, byte high)
		{
			double l = low, m = mid, h = high;
			var total = l + m + h;
			if (total <= 0)
				return Palette[3];

			l /= total;
			m /= total;
			h /= total;

			// Centroid along the axis: bass at 0, mids at 0.5, highs at 1
			var centroid = Math.Clamp(m * 0.5 + h, 0, 1);
			var color = Sample(centroid);

			// Dominance: how far the strongest band is above an even split
			var dominance = Math.Clamp((Math.Max(l, Math.Max(m, h)) - 1.0 / 3) * 1.5, 0, 1);
			var lift = (1 - dominance) * 0.25;

			return (
				Math.Clamp(color.R + (1 - color.R) * lift, 0, 1),
				Math.Clamp(color.G + (1 - color.G) * lift, 0, 1),
				Math.Clamp(color.B + (1 - color.B) * lift, 0, 1));
		}

		private static (double R, double G, double B) Sample(double position)
		{
			var scaled = position * (Palette.Length - 1);
			var index = (int)Math.Floor(scaled);
			if (index >= Palette.Length - 1)
				return Palette[^1];

			var t = scaled - index;
			var a = Palette[index];
			var b = Palette[index + 1];
			return (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
		}

		// Minimal PNG writer (8-bit RGBA, no filtering, zlib deflate). Keeps the renderer free of WinRT.

		private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

		private static byte[] EncodePng(byte[] rgba, int width, int height)
		{
			// Raw scanlines: filter byte (0 = None) + RGBA row
			var stride = width * 4;
			var raw = new byte[(stride + 1) * height];
			for (int y = 0; y < height; y++)
			{
				raw[y * (stride + 1)] = 0;
				Buffer.BlockCopy(rgba, y * stride, raw, y * (stride + 1) + 1, stride);
			}

			byte[] compressed;
			using (var compressedStream = new MemoryStream())
			{
				using (var zlib = new ZLibStream(compressedStream, CompressionLevel.Fastest, leaveOpen: true))
					zlib.Write(raw, 0, raw.Length);
				compressed = compressedStream.ToArray();
			}

			using var output = new MemoryStream(compressed.Length + 128);
			output.Write(PngSignature);

			var header = new byte[13];
			BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
			BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
			header[8] = 8;   // bit depth
			header[9] = 6;   // colour type: RGBA
			header[10] = 0;  // compression
			header[11] = 0;  // filter
			header[12] = 0;  // interlace
			WriteChunk(output, "IHDR", header);
			WriteChunk(output, "IDAT", compressed);
			WriteChunk(output, "IEND", []);

			return output.ToArray();
		}

		private static void WriteChunk(Stream output, string type, byte[] data)
		{
			var length = new byte[4];
			BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
			output.Write(length);

			var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
			output.Write(typeBytes);
			output.Write(data);

			var crc = new Crc32();
			crc.Append(typeBytes);
			crc.Append(data);
			var crcBytes = new byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc.GetCurrentHashAsUInt32());
			output.Write(crcBytes);
		}
	}
}
