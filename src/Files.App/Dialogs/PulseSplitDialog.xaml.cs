// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Helpers;
using Files.App.Services.Pulse;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinRT;

namespace Files.App.Dialogs
{
	/// <summary>
	/// Audio split dialog for the Pulse view. Separates the audio of the source
	/// (audio or video — the video's audio track is extracted first) into stems
	/// with the bundled Demucs 4 runtime (htdemucs model): vocals + instrumental,
	/// or the full four-stem split (vocals/drums/bass/other). Stems are written
	/// next to the source as "&lt;name&gt; - &lt;Stem&gt;.&lt;ext&gt;" (WAV/FLAC/MP3),
	/// which the Pulse feed then shows as their own audio cards.
	/// </summary>
	public sealed partial class PulseSplitDialog : ContentDialog
	{
		private readonly PulseCardViewModel card;
		private readonly string sourcePath;
		private readonly CancellationTokenSource lifetime = new();

		private PulseDemucs? demucs;
		private CancellationTokenSource? runCts;
		private bool isRunning;
		private bool isClosed;
		private bool isInitializing;
		private readonly string primaryLabel;

		private FrameworkElement RootAppElement
		{
			[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
			get => (FrameworkElement)MainWindow.Instance.Content;
		}

		public PulseSplitDialog(PulseCardViewModel card)
		{
			this.card = card;
			sourcePath = card.Item.GetRequiredPath();
			InitializeComponent();
			primaryLabel = PrimaryButtonText;
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
			=> await InitializeRuntimeAsync();

		private async Task InitializeRuntimeAsync()
		{
			if (isClosed || isInitializing) return;
			isInitializing = true;
			// Runtime discovery loads torch/demucs and can take many seconds (first launch stages the
			// bundled runtime, then imports PyTorch). Surface it instead of leaving the dialog silent.
			ExportRing.IsActive = true;
			ExportRing.IsIndeterminate = true;
			ExportStatusText.Text = Strings.PulseSplitPreparing.GetLocalizedResource();
			IsPrimaryButtonEnabled = false;

			try
			{
				demucs = await PulseDemucs.RequireAsync(lifetime.Token);
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				demucs = null;
				App.Logger.LogWarning(ex, "Demucs initialization failed");
				if (!isClosed) ExportStatusText.Text = ex.Message;
			}
			finally { isInitializing = false; }
			if (isClosed) return;

			ExportRing.IsActive = false;
			ExportRing.IsIndeterminate = false;

			if (demucs is null)
			{
				ModeComboBox.IsEnabled = false;
				FormatComboBox.IsEnabled = false;
				PrimaryButtonText = Strings.Retry.GetLocalizedResource();
				IsPrimaryButtonEnabled = true;
				return;
			}

			ModelText.Text = string.Format(
				Strings.PulseSplitSourceInfo.GetLocalizedResource(),
				demucs.DemucsVersion,
				demucs.TorchVersion,
				demucs.DeviceLabel);

			PrimaryButtonText = primaryLabel;
			ExportStatusText.Text = PulseDemucs.IsBusy ? Strings.PulseSplitBusy.GetLocalizedResource() : Strings.PulseMediaReady.GetLocalizedResource();
			RestoreEnabledState();
		}

		private async void PrimaryButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
		{
			args.Cancel = true;
			if (isClosed || isRunning || isInitializing) return;
			if (demucs is null) { await InitializeRuntimeAsync(); return; }
			if (PulseDemucs.IsBusy)
			{
				ExportStatusText.Text = Strings.PulseSplitBusy.GetLocalizedResource();
				return;
			}

			isRunning = true;
			runCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);

			IsPrimaryButtonEnabled = false;
			ModeComboBox.IsEnabled = false;
			FormatComboBox.IsEnabled = false;
			ExportRing.IsActive = true;
			ExportRing.IsIndeterminate = true;
			ExportRing.Value = 0;
			ExportStatusText.Text = Strings.PulseSplitPreparing.GetLocalizedResource();

			var options = new PulseSplitOptions(
				ModeComboBox.SelectedIndex == 1 ? PulseSplitMode.FourStems : PulseSplitMode.TwoStems,
				FormatComboBox.SelectedIndex switch { 1 => PulseSplitFormat.Flac, 2 => PulseSplitFormat.Mp3, _ => PulseSplitFormat.Wav });
			var progress = new Progress<PulseSplitProgress>(value =>
			{
				if (isClosed) return;
				ExportRing.IsIndeterminate = value.Percent is null;
				if (value.Percent is { } percent)
					ExportRing.Value = percent;
				if (!string.IsNullOrEmpty(value.Stage))
					ExportStatusText.Text = value.Stage;
			});

			try
			{
				var stems = await demucs.SplitAsync(sourcePath, options, progress, runCts.Token);
				if (isClosed) return;

				ExportRing.IsActive = false;
				ExportRing.IsIndeterminate = false;
				ExportStatusText.Text = string.Format(Strings.PulseSplitDone.GetLocalizedResource(), stems.Count);
			}
			catch (OperationCanceledException)
			{
				if (isClosed) return;
				ExportRing.IsActive = false;
				ExportRing.IsIndeterminate = false;
				ExportStatusText.Text = Strings.PulseSplitCancelled.GetLocalizedResource();
				RestoreEnabledState();
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Pulse split failed");
				if (isClosed) return;
				ExportRing.IsActive = false;
				ExportRing.IsIndeterminate = false;
				ExportStatusText.Text = ex.Message;
				RestoreEnabledState();
			}
			finally
			{
				isRunning = false;
				runCts?.Dispose();
				runCts = null;
				RestoreEnabledState();
			}
		}

		private void SecondaryButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
		{
			if (!isRunning)
				return;

			// Keep the dialog open so the cancellation is visible; the run reports
			// Cancelled and restores the controls.
			args.Cancel = true;
			runCts?.Cancel();
		}

		private void RestoreEnabledState()
		{
			if (isClosed || demucs is null)
				return;

			IsPrimaryButtonEnabled = !isRunning;
			ModeComboBox.IsEnabled = true;
			FormatComboBox.IsEnabled = true;
		}

		private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
		{
			isClosed = true;
			lifetime.Cancel();
			runCts?.Cancel();
		}
	}
}
