// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.ViewModels.Previews;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.ComponentModel;
using Windows.Media.Playback;

namespace Files.App.UserControls.FilePreviews
{
	public sealed partial class MediaPreview : UserControl
	{
		private IUserSettingsService UserSettingsService { get; } = Ioc.Default.GetRequiredService<IUserSettingsService>();
		private MediaPlayer? player;
		private bool closed;
		public MediaPreviewViewModel ViewModel { get; }

		public MediaPreview(MediaPreviewViewModel model)
		{
			ViewModel = model;
			InitializeComponent();
			try
			{
				player = new MediaPlayer { AutoPlay = false, Volume = UserSettingsService.InfoPaneSettingsService.MediaVolume };
				player.VolumeChanged += MediaPlayer_VolumeChanged;
				player.MediaFailed += MediaPlayer_MediaFailed;
				PlayerContext.SetMediaPlayer(player);
				player.Source = ViewModel.Source;
			}
			catch (Exception ex) { ViewModel.ReportError(ex.Message); App.Logger.LogWarning(ex, "Media player initialization failed"); }
			ViewModel.PropertyChanged += ViewModel_PropertyChanged;
			ViewModel.TogglePlaybackRequested += TogglePlaybackRequestInvoked;
			Unloaded += MediaPreview_Unloaded;
		}

		private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (!closed && e.PropertyName == nameof(ViewModel.Source) && player is not null)
			{
				try { player.Source = ViewModel.Source; }
				catch (Exception ex) { ViewModel.ReportError(ex.Message); }
			}
		}

		private void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
		{
			var message = $"{args.ErrorMessage} (0x{args.ExtendedErrorCode?.HResult ?? 0:X8})";
			DispatcherQueue.TryEnqueue(async () =>
			{
				if (closed || !ReferenceEquals(player, sender)) return;
				await ViewModel.PrepareCompatibleAsync(message);
			});
		}

		private void MediaPreview_Unloaded(object sender, RoutedEventArgs e)
		{
			if (closed) return;
			closed = true;
			ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
			ViewModel.TogglePlaybackRequested -= TogglePlaybackRequestInvoked;
			Unloaded -= MediaPreview_Unloaded;
			if (player is { } previous)
			{
				player = null;
				previous.VolumeChanged -= MediaPlayer_VolumeChanged;
				previous.MediaFailed -= MediaPlayer_MediaFailed;
				try { previous.Pause(); previous.Source = null; PlayerContext.SetMediaPlayer(null); }
				catch (Exception ex) { App.Logger.LogDebug(ex, "Media preview teardown"); }
				finally
				{
					try { previous.Dispose(); }
					catch (Exception ex) { App.Logger.LogDebug(ex, "Media player disposal"); }
				}
			}
			ViewModel.PreviewControlBase_Unloaded(sender, e);
		}

		private void MediaPlayer_VolumeChanged(MediaPlayer sender, object args)
			=> DispatcherQueue.TryEnqueue(() =>
			{
				if (!closed && ReferenceEquals(player, sender))
					UserSettingsService.InfoPaneSettingsService.MediaVolume = sender.Volume;
			});

		private void TogglePlaybackRequestInvoked(object? sender, EventArgs e)
		{
			if (closed || player?.Source is null || ViewModel.IsPreparing) return;
			try
			{
				if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) player.Pause();
				else player.Play();
			}
			catch (Exception ex) { ViewModel.ReportError(ex.Message); }
		}

		private void TogglePlaybackAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
		{
			TogglePlaybackRequestInvoked(sender, EventArgs.Empty);
			args.Handled = true;
		}
	}
}
