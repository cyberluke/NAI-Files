// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Security.Cryptography;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Files.App.Helpers
{
	public sealed record NaiPreparedImage(byte[] Jpeg, string Hash, long Length, long ModifiedUtcTicks);

	internal static class NaiVisionImage
	{
		public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp";

		public static async Task<NaiPreparedImage> PrepareAsync(string path, CancellationToken token)
		{
			if (!Supports(path) || !NaiThumbnailAvailability.CanReadContent(path)) throw new IOException("Image is unavailable without downloading");
			var info = new FileInfo(path);
			if (info.LinkTarget is not null) throw new IOException("Linked images require explicit source selection");
			var length = info.Length;
			var modified = info.LastWriteTimeUtc.Ticks;
			if (length is < 1 or > 64 * 1024 * 1024) throw new IOException("Image exceeds the local analysis limit");
			var bytes = await File.ReadAllBytesAsync(path, token);
			info.Refresh();
			if (info.Length != length || info.LastWriteTimeUtc.Ticks != modified) throw new IOException("Image changed during analysis preparation");
			using var stream = new InMemoryRandomAccessStream();
			using (var writer = new DataWriter(stream))
			{
				writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream();
			}
			stream.Seek(0);
			var decoder = await BitmapDecoder.CreateAsync(stream);
			if ((ulong)decoder.PixelWidth * decoder.PixelHeight > 150_000_000) throw new IOException("Image dimensions exceed the analysis limit");
			token.ThrowIfCancellationRequested();
			// Exact qualified Xe geometry; contain-fit preserves source aspect ratio.
			const uint width = 1280, height = 584;
			var scale = Math.Min((double)width / decoder.OrientedPixelWidth, (double)height / decoder.OrientedPixelHeight);
			var scaledWidth = Math.Max(1, (uint)Math.Round(decoder.OrientedPixelWidth * scale));
			var scaledHeight = Math.Max(1, (uint)Math.Round(decoder.OrientedPixelHeight * scale));
			var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
				new BitmapTransform { ScaledWidth = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale)), ScaledHeight = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale)), InterpolationMode = BitmapInterpolationMode.Fant },
				ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
			var source = pixels.DetachPixelData();
			var target = new byte[width * height * 4];
			for (var index = 3; index < target.Length; index += 4) target[index] = 255;
			var x = (width - scaledWidth) / 2;
			var y = (height - scaledHeight) / 2;
			for (uint row = 0; row < scaledHeight; row++)
				System.Buffer.BlockCopy(source, checked((int)(row * scaledWidth * 4)), target, checked((int)(((row + y) * width + x) * 4)), checked((int)(scaledWidth * 4)));
			token.ThrowIfCancellationRequested();
			using var output = new InMemoryRandomAccessStream();
			var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);
			encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, target);
			await encoder.FlushAsync();
			output.Seek(0);
			using var reader = new DataReader(output);
			await reader.LoadAsync((uint)output.Size);
			var jpeg = new byte[(int)output.Size]; reader.ReadBytes(jpeg);
			return new(jpeg, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), length, modified);
		}
	}
}
