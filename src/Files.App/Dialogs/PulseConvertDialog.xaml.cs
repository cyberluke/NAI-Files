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
	/// Format conversion for Pulse media cards, backed by the bundled FFmpeg 9 toolset:
	/// audio → MP3/M4A/WAV/OGG/FLAC/OPUS, video → the same audio formats or re-encoded
	/// MP4/WebM/MKV/MOV, images → PNG/JPG/WebP/BMP/TIFF with a quality slider for lossy
	/// formats. The result is written next to the source as "&lt;name&gt; (converted).&lt;ext&gt;",
	/// which the Pulse feed stacks as a new version of the source.
	/// </summary>
	public sealed partial class PulseConvertDialog : ContentDialog
	{
		private readonly PulseCardViewModel card;
		private readonly string sourcePath;
		private readonly CancellationTokenSource lifetime = new();

		private PulseFfmpeg? ffmpeg;
		private bool isConverting;
		private bool isImage;
		private bool isReady;
		private bool isClosed;
		private bool isInitialized;

		private FrameworkElement RootAppElement
		{
			[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
			get => (FrameworkElement)MainWindow.Instance.Content;
		}

		public PulseConvertDialog(PulseCardViewModel card)
		{
			this.card = card;
			sourcePath = card.Item.GetRequiredPath();
			isImage = card.IsImage;
			InitializeComponent();
			isInitialized = true;
			IsPrimaryButtonEnabled = false;

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
				if (isClosed) return;
				if (ffmpeg is null)
				{
					StatusText.Text = Strings.PulseTrimUnavailable.GetLocalizedResource();
					IsPrimaryButtonEnabled = false;
					return;
				}

				var sourceInfo = await GetSourceInfoAsync();
				if (isClosed) return;
				SourceInfoText.Text = string.Format(
					Strings.PulseTrimSourceInfo.GetLocalizedResource(),
					sourceInfo ?? string.Empty,
					ffmpeg.Version);

				isImage = card.IsImage;
				if (card.IsVideo)
				{
					// Video cards can extract the audio track or convert the whole video
					OutputTypeComboBox.Visibility = Visibility.Visible;
					OutputTypeComboBox.SelectedIndex = 1;
				}
				UpdateFormatList();
				isReady = true;
				IsPrimaryButtonEnabled = true;
				StatusText.Text = Strings.PulseMediaReady.GetLocalizedResource();
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Pulse convert dialog initialization failed");
				if (!isClosed) { StatusText.Text = ex.Message; IsPrimaryButtonEnabled = false; }
			}
		}

		private async Task<string?> GetSourceInfoAsync()
		{
			if (card.IsImage)
				return await ffmpeg!.ProbeImageSizeAsync(sourcePath, lifetime.Token) is { } size
					? $"{size.Width} × {size.Height} px"
					: null;

			return await ffmpeg!.ProbeDurationAsync(sourcePath, lifetime.Token) is { } duration
				? FormatTime(duration)
				: null;
		}

		private void OutputTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
			=> UpdateFormatList();

		private void FormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
			=> UpdateQualityRow();

		private void QualitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
		{
			if (isInitialized && !isClosed) QualityText.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture);
		}

		/// <summary>Fills the format combo for the current source kind / output type and re-evaluates the quality row.</summary>
		private void UpdateFormatList()
		{
			if (!isInitialized || isClosed) return;
			FormatComboBox.Items.Clear();

			if (isImage)
			{
				foreach (var (label, _) in ImageFormatItems)
					FormatComboBox.Items.Add(new ComboBoxItem { Content = label });
			}
			else if (card.IsVideo && OutputTypeComboBox.SelectedIndex == 1)
			{
				foreach (var (label, _) in VideoFormatItems)
					FormatComboBox.Items.Add(new ComboBoxItem { Content = label });
			}
			else
			{
				foreach (var (label, _) in AudioFormatItems)
					FormatComboBox.Items.Add(new ComboBoxItem { Content = label });
			}

			FormatComboBox.SelectedIndex = 0;
			UpdateQualityRow();
		}

		private void UpdateQualityRow()
		{
			if (!isInitialized || isClosed) return;
			var show = isImage && CurrentImageFormat is PulseImageFormat.Jpg or PulseImageFormat.Webp;
			QualityRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
			if (show)
				QualityText.Text = ((int)QualitySlider.Value).ToString(CultureInfo.InvariantCulture);
		}

		private static readonly (string Label, PulseAudioFormat Format)[] AudioFormatItems =
		[
			("MP3", PulseAudioFormat.Mp3),
			("M4A (AAC)", PulseAudioFormat.M4a),
			("WAV", PulseAudioFormat.Wav),
			("OGG", PulseAudioFormat.Ogg),
			("FLAC", PulseAudioFormat.Flac),
			("OPUS", PulseAudioFormat.Opus),
		];

		private static readonly (string Label, PulseVideoFormat Format)[] VideoFormatItems =
		[
			("MP4 (H.264 + AAC)", PulseVideoFormat.Mp4),
			("WebM (VP9 + Opus)", PulseVideoFormat.Webm),
			("MKV (H.264 + Opus)", PulseVideoFormat.Mkv),
			("MOV (H.264 + AAC)", PulseVideoFormat.Mov),
		];

		private static readonly (string Label, PulseImageFormat Format)[] ImageFormatItems =
		[
			("PNG", PulseImageFormat.Png),
			("JPG", PulseImageFormat.Jpg),
			("WebP", PulseImageFormat.Webp),
			("BMP", PulseImageFormat.Bmp),
			("TIFF", PulseImageFormat.Tiff),
		];

		private PulseAudioFormat CurrentAudioFormat => AudioFormatItems[Math.Clamp(FormatComboBox.SelectedIndex, 0, AudioFormatItems.Length - 1)].Format;
		private PulseVideoFormat CurrentVideoFormat => VideoFormatItems[Math.Clamp(FormatComboBox.SelectedIndex, 0, VideoFormatItems.Length - 1)].Format;
		private PulseImageFormat CurrentImageFormat => ImageFormatItems[Math.Clamp(FormatComboBox.SelectedIndex, 0, ImageFormatItems.Length - 1)].Format;

		private async void PrimaryButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
		{
			args.Cancel = true;
			if (!isReady || isClosed || isConverting || ffmpeg is null)
			{
				args.Cancel = true;
				return;
			}

			isConverting = true;
			IsPrimaryButtonEnabled = false;
			OutputTypeComboBox.IsEnabled = false;
			FormatComboBox.IsEnabled = false;
			QualitySlider.IsEnabled = false;
			ProgressRing.IsActive = true;
			ProgressRing.IsIndeterminate = isImage;
			ProgressRing.Value = 0;
			StatusText.Text = Strings.PulseConvertConverting.GetLocalizedResource();

			var progress = new Progress<double>(value => { if (!isClosed) ProgressRing.Value = value * 100; });

			try
			{
				bool succeeded;
				string outputPath;

				if (isImage)
				{
					var format = CurrentImageFormat;
					var quality = PulseImageFormatSpec.MapQuality(format, (int)QualitySlider.Value);
					outputPath = ResolveOutputPath(PulseImageFormatSpec.For(format).Extension);
					succeeded = await ffmpeg.ConvertImageAsync(sourcePath, format, quality, outputPath, lifetime.Token);
				}
				else if (card.IsVideo && OutputTypeComboBox.SelectedIndex == 1)
				{
					var format = CurrentVideoFormat;
					outputPath = ResolveOutputPath(PulseVideoFormatSpec.For(format).Extension);
					succeeded = await ffmpeg.ConvertVideoAsync(sourcePath, format, outputPath, lifetime.Token, progress);
				}
				else
				{
					var format = CurrentAudioFormat;
					outputPath = ResolveOutputPath(PulseAudioFormatSpec.For(format).Extension);
					succeeded = await ffmpeg.ConvertAudioAsync(sourcePath, format, outputPath, cancellationToken: lifetime.Token, progress: progress);
				}

				if (isClosed) return;
				if (succeeded)
					Hide();
				else
				{
					ProgressRing.IsActive = false;
					StatusText.Text = Strings.PulseTrimExportFailed.GetLocalizedResource();
				}
			}
			catch (OperationCanceledException)
			{
				// Dialog closed mid-conversion
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Pulse convert failed");
				if (!isClosed) { ProgressRing.IsActive = false; StatusText.Text = ex.Message; }
			}
			finally
			{
				isConverting = false;
				if (!isClosed)
				{
					IsPrimaryButtonEnabled = isReady;
					OutputTypeComboBox.IsEnabled = true;
					FormatComboBox.IsEnabled = true;
					QualitySlider.IsEnabled = true;
				}
			}
		}

		/// <summary>"song.mp3" -> "song (converted).mp3" next to the source; "(2)", "(3)" … on collision.</summary>
		private string ResolveOutputPath(string extension)
		{
			var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
			var stem = Path.GetFileNameWithoutExtension(sourcePath);

			var candidate = Path.Combine(directory, $"{stem} (converted).{extension}");
			var counter = 2;
			while (File.Exists(candidate))
				candidate = Path.Combine(directory, $"{stem} (converted) ({counter++}).{extension}");

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
