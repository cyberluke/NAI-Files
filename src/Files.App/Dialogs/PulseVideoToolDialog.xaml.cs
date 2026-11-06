// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Helpers;
using Files.App.Services.Pulse;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Globalization;
using System.IO;
using WinRT;

namespace Files.App.Dialogs
{
	/// <summary>
	/// Video tools for Pulse video cards, backed by the bundled FFmpeg 9 toolset:
	/// single-frame extraction (PNG/JPG/WebP at a chosen timestamp) and animated GIF creation
	/// from a selected segment (palette-based, with FPS and width control). Outputs are written
	/// next to the source as "&lt;name&gt; (frame).&lt;ext&gt;" and "&lt;name&gt; (gif).gif", which the
	/// Pulse feed stacks as new versions of the source.
	/// </summary>
	public sealed partial class PulseVideoToolDialog : ContentDialog
	{
		private static readonly TimeSpan MinimumSegment = TimeSpan.FromSeconds(0.2);

		private readonly PulseCardViewModel card;
		private readonly string sourcePath;
		private readonly CancellationTokenSource lifetime = new();

		private PulseFfmpeg? ffmpeg;
		private TimeSpan duration;
		private bool isBusy;
		private bool gifLastEditedWasStart;
		private bool isInitialized;
		private bool isReady;
		private bool isClosed;
		private bool syncingRange;

		private FrameworkElement RootAppElement
		{
			[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
			get => (FrameworkElement)MainWindow.Instance.Content;
		}

		public PulseVideoToolDialog(PulseCardViewModel card)
		{
			this.card = card;
			sourcePath = card.Item.GetRequiredPath();
			InitializeComponent();
			isInitialized = true;
			ExtractButton.IsEnabled = false;
			CreateGifButton.IsEnabled = false;

			FileNameText.Text = Path.GetFileName(sourcePath);
		}

		public new async Task<DialogResult> ShowAsync()
		{
			Opened += Dialog_Opened;
			Closing += Dialog_Closing;
			return (DialogResult)await base.ShowAsync();
		}

		private async void Dialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
		{
			try
			{
				StatusText.Text = Strings.PulseMediaPreparing.GetLocalizedResource();
				ffmpeg = await PulseFfmpeg.RequireAsync(lifetime.Token);
				var probed = await ffmpeg.ProbeDurationAsync(sourcePath, lifetime.Token);
				if (isClosed) return;
				if (probed is not { TotalSeconds: > 0 } mediaDuration)
				{
					StatusText.Text = Strings.PulseTrimUnavailable.GetLocalizedResource();
					ExtractButton.IsEnabled = false;
					CreateGifButton.IsEnabled = false;
					return;
				}

				duration = mediaDuration;
				SourceInfoText.Text = string.Format(Strings.PulseTrimSourceInfo.GetLocalizedResource(), FormatTime(duration), ffmpeg.Version);

				FrameSlider.Maximum = duration.TotalSeconds;
				FrameSlider.Value = 0;

				GifStartSlider.Maximum = duration.TotalSeconds;
				GifEndSlider.Maximum = duration.TotalSeconds;
				GifStartSlider.Value = 0;
				GifEndSlider.Value = duration.TotalSeconds;
				UpdateGifTimes();
				isReady = true;
				SetBusy(false);
				StatusText.Text = Strings.PulseMediaReady.GetLocalizedResource();
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Pulse video tools initialization failed");
				if (!isClosed) { StatusText.Text = ex.Message; ExtractButton.IsEnabled = false; CreateGifButton.IsEnabled = false; }
			}
		}

		private void FrameSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
		{
			if (isInitialized && !isClosed) FrameTimeText.Text = FormatTime(TimeSpan.FromSeconds(Math.Max(0, e.NewValue)));
		}

		private void GifStartSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
		{
			gifLastEditedWasStart = true;
			UpdateGifTimes();
		}

		private void GifEndSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
		{
			gifLastEditedWasStart = false;
			UpdateGifTimes();
		}

		private void UpdateGifTimes()
		{
			if (!isInitialized || isClosed || syncingRange) return;
			var maximum = Math.Max(0, duration.TotalSeconds);
			var minimum = Math.Min(MinimumSegment.TotalSeconds, maximum);
			var start = Math.Clamp(GifStartSlider.Value, 0, maximum);
			var end = Math.Clamp(GifEndSlider.Value, 0, maximum);
			if (end - start < minimum)
			{
				if (gifLastEditedWasStart) { end = Math.Min(maximum, start + minimum); start = Math.Max(0, end - minimum); }
				else { start = Math.Max(0, end - minimum); end = Math.Min(maximum, start + minimum); }
			}
			syncingRange = true;
			try { GifStartSlider.Value = start; GifEndSlider.Value = end; }
			finally { syncingRange = false; }
			GifStartTimeText.Text = FormatTime(TimeSpan.FromSeconds(start));
			GifEndTimeText.Text = FormatTime(TimeSpan.FromSeconds(end));
		}

		private async void ExtractButton_Click(object sender, RoutedEventArgs e)
		{
			if (!isReady || isClosed || isBusy || ffmpeg is null)
				return;

			SetBusy(true);
			var timestamp = TimeSpan.FromSeconds(Math.Max(0, FrameSlider.Value));
			var format = (PulseImageFormat)Math.Clamp(FrameFormatComboBox.SelectedIndex, 0, 2);
			var outputPath = ResolveOutputPath("frame", PulseImageFormatSpec.For(format).Extension);

			try
			{
				StatusText.Text = Strings.PulseVideoExtracting.GetLocalizedResource();
				var succeeded = await ffmpeg.ExtractFrameAsync(sourcePath, timestamp, format, PulseImageFormatSpec.MapQuality(format, 92), outputPath, lifetime.Token);
				if (isClosed) return;
				StatusText.Text = succeeded ? Strings.PulseVideoExtractDone.GetLocalizedResource() : Strings.PulseTrimExportFailed.GetLocalizedResource();
			}
			catch (OperationCanceledException)
			{
				// Dialog closed mid-extraction
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Pulse frame extraction failed");
				if (!isClosed) StatusText.Text = ex.Message;
			}
			finally
			{
				SetBusy(false);
			}
		}

		private async void CreateGifButton_Click(object sender, RoutedEventArgs e)
		{
			if (!isReady || isClosed || isBusy || ffmpeg is null)
				return;

			SetBusy(true);
			var start = TimeSpan.FromSeconds(Math.Max(0, GifStartSlider.Value));
			var end = TimeSpan.FromSeconds(Math.Min(duration.TotalSeconds, GifEndSlider.Value));
			var fps = (int)FpsBox.Value;
			var width = (int)WidthBox.Value;
			var outputPath = ResolveOutputPath("gif", "gif");
			var progress = new Progress<double>(value => { if (!isClosed) { ProgressRing.IsIndeterminate = false; ProgressRing.Value = value * 100; } });

			try
			{
				StatusText.Text = Strings.PulseVideoCreatingGif.GetLocalizedResource();
				var succeeded = await ffmpeg.CreateGifAsync(sourcePath, start, end, fps, width, outputPath, lifetime.Token, progress);
				if (isClosed) return;
				StatusText.Text = succeeded ? Strings.PulseVideoGifDone.GetLocalizedResource() : Strings.PulseTrimExportFailed.GetLocalizedResource();
			}
			catch (OperationCanceledException)
			{
				// Dialog closed mid-creation
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Pulse GIF creation failed");
				if (!isClosed) StatusText.Text = ex.Message;
			}
			finally
			{
				SetBusy(false);
			}
		}

		private void SetBusy(bool busy)
		{
			if (isClosed) return;
			isBusy = busy;
			ExtractButton.IsEnabled = isReady && !busy;
			CreateGifButton.IsEnabled = isReady && !busy;
			FrameSlider.IsEnabled = !busy;
			GifStartSlider.IsEnabled = !busy;
			GifEndSlider.IsEnabled = !busy;
			FrameFormatComboBox.IsEnabled = !busy;
			FpsBox.IsEnabled = !busy;
			WidthBox.IsEnabled = !busy;
			ProgressRing.IsActive = busy;
			ProgressRing.IsIndeterminate = true;
			ProgressRing.Value = 0;
		}

		/// <summary>"clip.mp4" -> "clip (frame).png" / "clip (gif).gif" next to the source; "(2)" … on collision.</summary>
		private string ResolveOutputPath(string marker, string extension)
		{
			var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
			var stem = Path.GetFileNameWithoutExtension(sourcePath);

			var candidate = Path.Combine(directory, $"{stem} ({marker}).{extension}");
			var counter = 2;
			while (File.Exists(candidate))
				candidate = Path.Combine(directory, $"{stem} ({marker}) ({counter++}).{extension}");

			return candidate;
		}

		private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
		{
			isClosed = true;
			lifetime.Cancel();
		}

		private static string FormatTime(TimeSpan value)
		{
			if (value < TimeSpan.Zero)
				value = TimeSpan.Zero;

			return value.TotalHours >= 1
				? value.ToString(@"h\:mm\:ss\.f", CultureInfo.InvariantCulture)
				: value.ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);
		}
	}
}
