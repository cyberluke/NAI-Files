// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Helpers;
using Files.App.Services.Pulse;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System.Globalization;
using System.IO;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using WinRT;

namespace Files.App.Dialogs
{
	/// <summary>
	/// Audio trim dialog for the Pulse view. Shows the file's spectral waveform (decoded through the
	/// bundled FFmpeg 9 toolset, so it also covers video containers: MP4/WebM/MKV), lets the user pick
	/// a region with two sliders (or by dragging directly on the waveform), previews the selection with
	/// optional looping, and re-encodes only the audio track of that region into a new clip
	/// (MP3 / M4A / WAV / OGG). The clip is written next to the source as "&lt;name&gt; (trimmed).&lt;ext&gt;",
	/// which the Pulse feed then stacks as a new version of the source.
	/// </summary>
	public sealed partial class PulseTrimDialog : ContentDialog
	{
		private static readonly TimeSpan MinimumSelection = TimeSpan.FromMilliseconds(200);

		private readonly PulseCardViewModel card;
		private readonly string sourcePath;
		private readonly CancellationTokenSource lifetime = new();

		private PulseFfmpeg? ffmpeg;
		private TimeSpan duration;
		private TimeSpan start;
		private TimeSpan end;

		private MediaPlayer? player;
		private MediaSource? source;
		private IStorageFile? sourceFile;
		private PulseMediaPlayback? usedMediaPlayback;
		private DateTimeOffset lastPositionUpdate;
		private bool isExporting;
		private bool isPointerScrubbing;
		private bool isPointerDraggingBoundary;
		private bool lastEditedWasStart;
		private bool isPlaying;
		private double pointerRatio;
		private bool isInitialized;
		private bool isReady;
		private bool isClosed;
		private bool syncingSelection;
		private bool previewReady;

		private FrameworkElement RootAppElement
		{
			[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
			get => (FrameworkElement)MainWindow.Instance.Content;
		}

		public PulseTrimDialog(PulseCardViewModel card)
		{
			this.card = card;
			sourcePath = card.Item.GetRequiredPath();
			InitializeComponent();
			PulseDialogChrome.EnableDrag(this);
			isInitialized = true;
			IsPrimaryButtonEnabled = false;
			PlayPauseButton.IsEnabled = false;
			StopButton.IsEnabled = false;

			FileNameText.Text = Path.GetFileName(sourcePath);
			LoopToggle.IsOn = true;
			AnalyzingRing.IsIndeterminate = false;
			WaveformHost.SizeChanged += (_, _) => UpdateSelectionOverlay();
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
				ExportStatusText.Text = Strings.PulseMediaPreparing.GetLocalizedResource();
				ffmpeg = await PulseFfmpeg.RequireAsync(lifetime.Token);
				if (lifetime.IsCancellationRequested) return;
				if (ffmpeg is null)
				{
					ShowUnavailable(Strings.PulseTrimUnavailable.GetLocalizedResource());
					return;
				}

				var probed = await ffmpeg.ProbeDurationAsync(sourcePath, lifetime.Token);
				if (isClosed) return;
				if (probed is not { TotalSeconds: > 0 } mediaDuration)
				{
					ShowUnavailable(Strings.PulseTrimUnsupported.GetLocalizedResource());
					return;
				}

				duration = mediaDuration;
				start = TimeSpan.Zero;
				end = duration;

				SourceDurationText.Text = string.Format(
					Strings.PulseTrimSourceInfo.GetLocalizedResource(),
					FormatTime(duration),
					ffmpeg.Version);

				StartSlider.Maximum = duration.TotalSeconds;
				EndSlider.Maximum = duration.TotalSeconds;
				EndSlider.Value = duration.TotalSeconds;
				StartSlider.Value = 0;
				StartTimeText.Text = FormatTime(start);
				EndTimeText.Text = FormatTime(end);
				UpdateSelectionOverlay();

				// Video sources can export a video cut instead of an audio clip
				if (card.IsVideo)
				{
					OutputModeComboBox.Visibility = Visibility.Visible;
					OutputModeComboBox.SelectedIndex = 1;
					VideoPreview.Visibility = Visibility.Visible;
				}
				UpdateModeUi();
				isReady = true;
				RestoreEnabledState();
				ExportStatusText.Text = Strings.PulseMediaReady.GetLocalizedResource();

				_ = LoadWaveformAsync();
				_ = LoadPlayerAsync();
			}
			catch (OperationCanceledException)
			{
				// Dialog closed during initialization
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Pulse trim dialog initialization failed");
				if (!isClosed) ShowUnavailable(ex.Message);
			}
		}

		private async Task LoadWaveformAsync()
		{
			var progress = new Progress<double>(value => { if (!isClosed) AnalyzingRing.Value = value * 100; });
			// XAML properties must be read on the dispatcher, not inside Task.Run.
			var width = Math.Max(640, (int)WaveformHost.ActualWidth);
			var height = Math.Max(1, (int)WaveformHost.ActualHeight);

			PulseWaveform? waveform = null;
			byte[]? png = null;
			try
			{
				waveform = await ffmpeg!.AnalyzeAsync(sourcePath, lifetime.Token, progress);
				if (waveform is not null)
					png = await Task.Run(() => PulseWaveformRenderer.RenderPng(waveform, width, height), lifetime.Token);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Waveform preparation failed");
				if (!isClosed) ExportStatusText.Text = ex.Message;
			}

			if (lifetime.IsCancellationRequested) return;
			AnalyzingPanel.Visibility = Visibility.Collapsed;
			AnalyzingRing.IsActive = false;

			var image = png is null ? null : await png.ToBitmapAsync();
			if (isClosed) return;
			if (image is null)
			{
				UnavailableText.Text = Strings.PulseTrimNoAudio.GetLocalizedResource();
				UnavailableText.Visibility = Visibility.Visible;
				return;
			}

			WaveformImage.Source = image;
			WaveformImage.Visibility = Visibility.Visible;
			UpdateSelectionOverlay();
		}

		private async Task LoadPlayerAsync()
		{
			PulseMediaPlayback? prepared = null;
			MediaSource? preparedSource = null;
			MediaPlayer? preparedPlayer = null;
			try
			{
				var tools = ffmpeg ?? await PulseFfmpeg.RequireAsync(lifetime.Token);
				prepared = await new PulseMediaGate(tools).PrepareAsync(sourcePath, lifetime.Token);
				if (isClosed) return;
				if (prepared is null) throw new IOException(Strings.PulseTrimPreviewFailed.GetLocalizedResource());
				var file = await StorageFileExtensions.DangerousGetFileFromPathAsync(prepared.Path);
				if (isClosed) return;
				if (file is null) throw new FileNotFoundException(prepared.Path);
				preparedSource = MediaSource.CreateFromStorageFile(file);
				preparedPlayer = new MediaPlayer
				{
					AudioCategory = MediaPlayerAudioCategory.Media,
					IsLoopingEnabled = false,
					AutoPlay = false,
				};
				sourceFile = file;
				source = preparedSource;
				preparedSource = null;
				usedMediaPlayback = prepared;
				prepared = null;
				player = preparedPlayer;
				preparedPlayer = null;
				player.MediaOpened += Player_MediaOpened;
				player.MediaEnded += Player_MediaEnded;
				player.MediaFailed += Player_MediaFailed;
				player.PlaybackSession.PositionChanged += Player_PositionChanged;
				if (card.IsVideo) VideoPreview.SetMediaPlayer(player);
				player.Source = source;
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Pulse trim preview could not load");
				if (!isClosed) ExportStatusText.Text = Strings.PulseTrimPreviewFailed.GetLocalizedResource() + Environment.NewLine + ex.Message;
			}
			finally
			{
				preparedPlayer?.Dispose();
				preparedSource?.Dispose();
				prepared?.Dispose();
			}
		}

		private void ShowUnavailable(string message)
		{
			if (isClosed) return;
			isReady = false;
			AnalyzingPanel.Visibility = Visibility.Collapsed;
			AnalyzingRing.IsActive = false;
			UnavailableText.Text = message;
			UnavailableText.Visibility = Visibility.Visible;
			StartSlider.IsEnabled = false;
			EndSlider.IsEnabled = false;
			PlayPauseButton.IsEnabled = false;
			StopButton.IsEnabled = false;
			LoopToggle.IsEnabled = false;
			OutputModeComboBox.IsEnabled = false;
			FormatComboBox.IsEnabled = false;
			VideoFormatComboBox.IsEnabled = false;
			NormalizeToggle.IsEnabled = false;
			SpeedSlider.IsEnabled = false;
			FadeInBox.IsEnabled = false;
			FadeOutBox.IsEnabled = false;
			IsPrimaryButtonEnabled = false;
		}

		// ---- output mode (audio clip vs video cut) --------------------------------

		private void OutputModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
			=> UpdateModeUi();

		private void UpdateModeUi()
		{
			// SelectionChanged can fire while InitializeComponent is constructing later controls.
			if (OutputModeComboBox is null || FormatComboBox is null || VideoFormatComboBox is null || EffectsRow is null)
				return;
			var videoMode = OutputModeComboBox.Visibility == Visibility.Visible && OutputModeComboBox.SelectedIndex == 1;
			FormatComboBox.Visibility = videoMode ? Visibility.Collapsed : Visibility.Visible;
			VideoFormatComboBox.Visibility = videoMode ? Visibility.Visible : Visibility.Collapsed;
			EffectsRow.Visibility = videoMode ? Visibility.Collapsed : Visibility.Visible;
		}

		private void SpeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
		{
			if (SpeedText is not null)
				SpeedText.Text = $"{e.NewValue:0.00}x";
		}

		// ---- selection: sliders + waveform drag ---------------------------------

		private void StartSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
		{
			if (!isReady || isClosed || syncingSelection) return;
			lastEditedWasStart = true;
			start = TimeSpan.FromSeconds(Math.Max(0, e.NewValue));
			ClampSelection();
			SyncSelectionUi();
		}

		private void EndSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
		{
			if (!isReady || isClosed || syncingSelection) return;
			lastEditedWasStart = false;
			end = TimeSpan.FromSeconds(Math.Min(duration.TotalSeconds, e.NewValue));
			ClampSelection();
			SyncSelectionUi();
		}

		/// <summary>Keeps start &lt; end (with a minimum length): the other boundary yields.</summary>
		private void ClampSelection()
		{
			var maximum = Math.Max(0, duration.TotalSeconds);
			var minimum = Math.Min(MinimumSelection.TotalSeconds, maximum);
			var first = Math.Clamp(start.TotalSeconds, 0, maximum);
			var last = Math.Clamp(end.TotalSeconds, 0, maximum);
			if (last - first < minimum)
			{
				if (lastEditedWasStart) { last = Math.Min(maximum, first + minimum); first = Math.Max(0, last - minimum); }
				else { first = Math.Max(0, last - minimum); last = Math.Min(maximum, first + minimum); }
			}
			start = TimeSpan.FromSeconds(first);
			end = TimeSpan.FromSeconds(last);
		}

		private void SyncSelectionUi()
		{
			if (!isInitialized || isClosed) return;
			syncingSelection = true;
			try { StartSlider.Value = start.TotalSeconds; EndSlider.Value = end.TotalSeconds; }
			finally { syncingSelection = false; }
			StartTimeText.Text = FormatTime(start);
			EndTimeText.Text = FormatTime(end);
			UpdateSelectionOverlay();
		}

		private void UpdateSelectionOverlay()
		{
			if (!isInitialized || isClosed) return;
			var width = SelectionCanvas.ActualWidth;
			if (width <= 0 || duration <= TimeSpan.Zero)
				return;

			var startRatio = Math.Clamp(start.TotalSeconds / duration.TotalSeconds, 0, 1);
			var endRatio = Math.Clamp(end.TotalSeconds / duration.TotalSeconds, startRatio, 1);
			var startX = startRatio * width;
			var selectionWidth = Math.Max(0, (endRatio - startRatio) * width);

			Canvas.SetLeft(LeftDimRect, 0);
			LeftDimRect.Width = startX;
			LeftDimRect.Height = SelectionCanvas.ActualHeight;

			Canvas.SetLeft(SelectionBox, startX);
			SelectionBox.Width = selectionWidth;
			SelectionBox.Height = SelectionCanvas.ActualHeight;

			Canvas.SetLeft(RightDimRect, startX + selectionWidth);
			RightDimRect.Width = Math.Max(0, width - startX - selectionWidth);
			RightDimRect.Height = SelectionCanvas.ActualHeight;

			UpdatePlayhead();
		}

		private void WaveformHost_PointerPressed(object sender, PointerRoutedEventArgs e)
		{
			if (duration <= TimeSpan.Zero || SelectionCanvas.ActualWidth <= 0)
				return;

			isPointerScrubbing = true;
			pointerRatio = GetPointerRatio(e);
			UpdatePointerAction();
			WaveformHost.CapturePointer(e.Pointer);
		}

		private void WaveformHost_PointerMoved(object sender, PointerRoutedEventArgs e)
		{
			if (!isPointerScrubbing)
				return;

			pointerRatio = GetPointerRatio(e);
			UpdatePointerAction();
		}

		private void WaveformHost_PointerReleased(object sender, PointerRoutedEventArgs e)
		{
			if (!isPointerScrubbing)
				return;

			isPointerScrubbing = false;
			isPointerDraggingBoundary = false;
			WaveformHost.ReleasePointerCapture(e.Pointer);
		}

		private double GetPointerRatio(PointerRoutedEventArgs e)
		{
			var position = e.GetCurrentPoint(WaveformHost).Position;
			return Math.Clamp(position.X / Math.Max(1, WaveformHost.ActualWidth), 0, 1);
		}

		/// <summary>
		/// While playing, a press/drag scrubs the preview; otherwise the nearest selection boundary moves
		/// (decided on press, held for the drag).
		/// </summary>
		private void UpdatePointerAction()
		{
			if (isPointerDraggingBoundary)
			{
				MoveNearestBoundary(pointerRatio);
				return;
			}

			if (isPlaying && player is not null)
			{
				SeekToRatio(pointerRatio);
				return;
			}

			// First press on a stopped preview: decide which boundary is closer
			var startRatio = start.TotalSeconds / duration.TotalSeconds;
			var endRatio = end.TotalSeconds / duration.TotalSeconds;
			var moveStart = Math.Abs(pointerRatio - startRatio) <= Math.Abs(pointerRatio - endRatio);
			isPointerDraggingBoundary = true;
			MoveBoundary(moveStart, pointerRatio);
		}

		private void MoveNearestBoundary(double ratio)
		{
			var startRatio = start.TotalSeconds / duration.TotalSeconds;
			var endRatio = end.TotalSeconds / duration.TotalSeconds;
			MoveBoundary(Math.Abs(ratio - startRatio) <= Math.Abs(ratio - endRatio), ratio);
		}

		private void MoveBoundary(bool moveStart, double ratio)
		{
			if (!isReady || isClosed) return;
			lastEditedWasStart = moveStart;
			if (moveStart) start = TimeSpan.FromSeconds(Math.Clamp(ratio, 0, 1) * duration.TotalSeconds);
			else end = TimeSpan.FromSeconds(Math.Clamp(ratio, 0, 1) * duration.TotalSeconds);
			ClampSelection();
			SyncSelectionUi();
		}

		// ---- preview transport ---------------------------------------------------

		private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
		{
			if (isClosed || !previewReady || player is null)
				return;
			try
			{
			if (isPlaying)
			{
				player.Pause();
				SetPlaying(false);
			}
			else
			{
				var position = SafePosition();
				if (position < start || position >= end)
					player.PlaybackSession.Position = start;
				player.Play();
				SetPlaying(true);
			}
			}
			catch (Exception ex) { ExportStatusText.Text = ex.Message; }
		}

		private void StopButton_Click(object sender, RoutedEventArgs e)
		{
			if (isClosed || player is null)
				return;

			try { player.Pause(); }
			catch (Exception ex) { ExportStatusText.Text = ex.Message; }
			SetPlaying(false);
			TrySetPosition(start);
			UpdatePositionUi(start);
		}

		private void SeekToRatio(double ratio)
		{
			if (player is null)
				return;

			var position = TimeSpan.FromSeconds(ratio * duration.TotalSeconds);
			TrySetPosition(position);
			UpdatePositionUi(position);
		}

		private void Player_PositionChanged(MediaPlaybackSession sender, object args)
		{
			DispatcherQueue.TryEnqueue(() =>
			{
				if (isClosed || player is null) return;
				try
				{
					var position = sender.Position;
					if (position >= end && end > start)
					{
						if (!LoopToggle.IsOn) { player.Pause(); SetPlaying(false); }
						TrySetPosition(start);
						position = start;
					}
					var now = DateTimeOffset.UtcNow;
					if (now - lastPositionUpdate < TimeSpan.FromMilliseconds(50)) return;
					lastPositionUpdate = now;
					UpdatePositionUi(position);
				}
				catch (Exception ex) { ExportStatusText.Text = ex.Message; }
			});
		}

		private void Player_MediaOpened(MediaPlayer sender, object args)
			=> DispatcherQueue.TryEnqueue(() =>
			{
				if (isClosed || !ReferenceEquals(player, sender)) return;
				previewReady = true;
				if (!isExporting) RestoreEnabledState();
			});

		private void Player_MediaEnded(MediaPlayer sender, object args)
		{
			DispatcherQueue.TryEnqueue(() =>
			{
				if (isClosed || !ReferenceEquals(player, sender)) return;
				try
				{
				if (LoopToggle.IsOn)
				{
					TrySetPosition(start);
					sender.Play();
				}
				else
				{
					SetPlaying(false);
					TrySetPosition(start);
					UpdatePositionUi(start);
				}
				}
				catch (Exception ex) { ExportStatusText.Text = ex.Message; }
			});
		}

		private void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
		{
			var detail = $"{args.ErrorMessage} (0x{args.ExtendedErrorCode?.HResult ?? 0:X8})";
			DispatcherQueue.TryEnqueue(() =>
			{
				if (isClosed || !ReferenceEquals(player, sender)) return;
				SetPlaying(false);
				previewReady = false;
				PlayPauseButton.IsEnabled = false;
				StopButton.IsEnabled = false;
				ExportStatusText.Text = Strings.PulseTrimPreviewFailed.GetLocalizedResource() + Environment.NewLine + detail;
			});
		}

		private void SetPlaying(bool playing)
		{
			if (isClosed) return;
			if (isPlaying == playing)
				return;

			isPlaying = playing;
			PlayPauseGlyph.Glyph = playing ? "\uE769" : "\uE768";
		}

		private TimeSpan SafePosition()
		{
			try
			{
				var position = player?.PlaybackSession.Position;
				return position is { } value && value > TimeSpan.Zero ? value : start;
			}
			catch
			{
				return start;
			}
		}

		private void TrySetPosition(TimeSpan position)
		{
			try
			{
				if (player?.PlaybackSession.CanSeek == true)
					player.PlaybackSession.Position = position;
			}
			catch
			{
				// Mid-transition; the next report corrects the playhead
			}
		}

		private void UpdatePositionUi(TimeSpan position)
		{
			if (isClosed || !isInitialized) return;
			PositionText.Text = $"{FormatTime(position)} / {FormatTime(duration)}";

			// Playhead + played overlay confined to the selection region
			var width = SelectionCanvas.ActualWidth;
			if (width <= 0 || end <= start)
				return;

			var selectionStartX = (float)(start.TotalSeconds / duration.TotalSeconds) * width;
			var selectionWidth = (float)((end - start).TotalSeconds / duration.TotalSeconds) * width;
			var clamped = position < start ? start : position > end ? end : position;
			var ratio = (clamped - start) / (end - start);

			Canvas.SetLeft(PlayedOverlay, selectionStartX);
			PlayedOverlay.Width = (float)ratio * selectionWidth;
			PlayedOverlay.Height = SelectionCanvas.ActualHeight;

			Canvas.SetLeft(PlayheadLine, selectionStartX + (float)ratio * selectionWidth);
			PlayheadLine.Height = SelectionCanvas.ActualHeight;
		}

		private void UpdatePlayhead() => UpdatePositionUi(SafePosition());

		// ---- export ---------------------------------------------------------------

		private async void PrimaryButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
		{
			args.Cancel = true;
			if (!isReady || isClosed || isExporting || ffmpeg is null)
			{
				args.Cancel = true;
				return;
			}

			isExporting = true;
			IsPrimaryButtonEnabled = false;
			OutputModeComboBox.IsEnabled = false;
			FormatComboBox.IsEnabled = false;
			VideoFormatComboBox.IsEnabled = false;
			NormalizeToggle.IsEnabled = false;
			SpeedSlider.IsEnabled = false;
			FadeInBox.IsEnabled = false;
			FadeOutBox.IsEnabled = false;
			StartSlider.IsEnabled = false;
			EndSlider.IsEnabled = false;
			PlayPauseButton.IsEnabled = false;
			StopButton.IsEnabled = false;
			LoopToggle.IsEnabled = false;
			ExportRing.IsActive = true;
			ExportRing.IsIndeterminate = false;
			ExportRing.Value = 0;
			ExportStatusText.Text = Strings.PulseTrimExporting.GetLocalizedResource();

			var videoMode = OutputModeComboBox.Visibility == Visibility.Visible && OutputModeComboBox.SelectedIndex == 1;
			var progress = new Progress<double>(value => { if (!isClosed) ExportRing.Value = value * 100; });

			try
			{
				bool succeeded;
				string outputPath;

				if (videoMode)
				{
					var format = (PulseVideoFormat)Math.Clamp(VideoFormatComboBox.SelectedIndex, 0, 3);
					outputPath = ResolveOutputPath(PulseVideoFormatSpec.For(format).Extension);
					succeeded = await ffmpeg.ExportVideoAsync(sourcePath, start, end, format, outputPath, lifetime.Token, progress);
				}
				else
				{
					var format = (PulseAudioFormat)Math.Clamp(FormatComboBox.SelectedIndex, 0, 5);
					var effects = new PulseAudioEffects(NormalizeToggle.IsOn, SpeedSlider.Value, FadeInBox.Value, FadeOutBox.Value);
					outputPath = ResolveOutputPath(PulseAudioFormatSpec.For(format).Extension);
					succeeded = await ffmpeg.ExportAudioAsync(sourcePath, start, end, format, outputPath, effects, lifetime.Token, progress);
				}

				if (isClosed) return;
				if (succeeded)
				{
					player?.Pause();
					Hide();
				}
				else
				{
					ExportRing.IsActive = false;
					ExportStatusText.Text = Strings.PulseTrimExportFailed.GetLocalizedResource();
				}
			}
			catch (OperationCanceledException)
			{
				// Dialog closed mid-export: the temp file is cleaned up by the exporter
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Pulse trim export failed");
				if (!isClosed)
				{
					ExportRing.IsActive = false;
					ExportStatusText.Text = Strings.PulseTrimExportFailed.GetLocalizedResource() + Environment.NewLine + ex.Message;
				}
			}
			finally
			{
				isExporting = false;
				RestoreEnabledState();
			}
		}

		private void RestoreEnabledState()
		{
			if (!isReady || isClosed)
				return;

			IsPrimaryButtonEnabled = true;
			OutputModeComboBox.IsEnabled = true;
			FormatComboBox.IsEnabled = true;
			VideoFormatComboBox.IsEnabled = true;
			NormalizeToggle.IsEnabled = true;
			SpeedSlider.IsEnabled = true;
			FadeInBox.IsEnabled = true;
			FadeOutBox.IsEnabled = true;
			StartSlider.IsEnabled = true;
			EndSlider.IsEnabled = true;
			PlayPauseButton.IsEnabled = previewReady;
			StopButton.IsEnabled = previewReady;
			LoopToggle.IsEnabled = true;
		}

		/// <summary>
		/// "song.mp3" -> "song (trimmed).mp3" next to the source; "(2)", "(3)" … on collision.
		/// The " (trimmed)" suffix is a version marker, so the Pulse feed stacks the clip
		/// with the source file automatically.
		/// </summary>
		private string ResolveOutputPath(string extension)
		{
			var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
			var stem = Path.GetFileNameWithoutExtension(sourcePath);

			var candidate = Path.Combine(directory, $"{stem} (trimmed).{extension}");
			var counter = 2;
			while (File.Exists(candidate))
				candidate = Path.Combine(directory, $"{stem} (trimmed) ({counter++}).{extension}");

			return candidate;
		}

		private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
		{
			if (isClosed) return;
			isClosed = true;
			previewReady = false;
			lifetime.Cancel();

			if (player is not null)
			{
				player.MediaOpened -= Player_MediaOpened;
				player.MediaEnded -= Player_MediaEnded;
				player.MediaFailed -= Player_MediaFailed;
				player.PlaybackSession.PositionChanged -= Player_PositionChanged;
				try { player.Pause(); player.Source = null; VideoPreview.SetMediaPlayer(null); }
				catch (Exception ex) { App.Logger.LogDebug(ex, "Trim preview teardown"); }
				finally
				{
					try { player.Dispose(); }
					catch (Exception ex) { App.Logger.LogDebug(ex, "Trim player disposal"); }
				}
				player = null;
			}
			source?.Dispose();
			source = null;
			sourceFile = null;

			usedMediaPlayback?.Dispose();
			usedMediaPlayback = null;
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
