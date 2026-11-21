// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using Files.App.AI.Vision;
using Windows.Storage;

namespace Files.App.Helpers
{
	public sealed record NaiVisionProgress(int Total, int Completed, int Cached, int Skipped, int Failed, string CurrentFile);

	internal static class NaiVisionIndexer
	{
		private static readonly SemaphoreSlim inference = new(1, 1);
		public static NaiVisionCache Cache { get; } = new(Path.Combine(ApplicationData.Current.LocalFolder.Path, "nai-vision", "records"));
		public static string PreviewDirectory { get; } = Path.Combine(ApplicationData.Current.LocalFolder.Path, "nai-vision", "previews");

		public static async Task AnalyzeFolderAsync(string folder, string model, string language, bool consent,
			IProgress<NaiVisionProgress> progress, CancellationToken token)
		{
			if (!consent) throw new InvalidOperationException("Explicit image-analysis consent is required");
			var paths = await Task.Run(() => Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
				.Where(NaiVisionImage.Supports).Take(10000).ToArray(), token);
			var existing = (await Cache.ReadAsync(folder, token)).Where(entry => entry.Model == model && entry.Language == language && entry.PromptVersion == "nai-vision-v1")
				.GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
			var completed = 0; var cached = 0; var skipped = 0; var failed = 0;
			Directory.CreateDirectory(PreviewDirectory);
			foreach (var path in paths)
			{
				token.ThrowIfCancellationRequested();
				progress.Report(new(paths.Length, completed, cached, skipped, failed, Path.GetFileName(path)));
				try
				{
					var info = new FileInfo(path);
					if (existing.TryGetValue(path, out var old) && old.Length == info.Length && old.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks)
					{ cached++; continue; }
					if (!NaiThumbnailAvailability.CanReadContent(path) || info.LinkTarget is not null) { skipped++; continue; }
					var image = await NaiVisionImage.PrepareAsync(path, token);
					await inference.WaitAsync(token);
					string result;
					try { result = await VisionAnalyzer.AnalyzeAsync(image.Jpeg, model, language, token); }
					finally { inference.Release(); }
					token.ThrowIfCancellationRequested();
					info.Refresh();
					if (!info.Exists || info.Length != image.Length || info.LastWriteTimeUtc.Ticks != image.ModifiedUtcTicks) { skipped++; continue; }
					var preview = image.Hash + ".jpg";
					var entry = NaiVisionAnalysisParser.Apply(result, new NaiImageAnalysis
					{
						Path = path, ContentSha256 = image.Hash, Length = image.Length, LastWriteUtcTicks = image.ModifiedUtcTicks,
						Model = model, Language = language, AnalyzedAt = DateTimeOffset.UtcNow, PreviewFile = preview,
					});
					await File.WriteAllBytesAsync(Path.Combine(PreviewDirectory, preview), image.Jpeg, token);
					await Cache.SaveAsync(entry, token);
					completed++;
				}
				catch (OperationCanceledException) { throw; }
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Runtime.InteropServices.COMException)
				{ failed++; }
			}
			progress.Report(new(paths.Length, completed, cached, skipped, failed, string.Empty));
		}
	}
}
