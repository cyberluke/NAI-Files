// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System.IO;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Files.App.Services.Pulse
{
	/// <summary>
	/// One audible <see cref="MediaPlayer"/> for the whole Pulse feed: the audio card's play button loads its
	/// track here, so starting another card stops the previous one. Position updates are throttled and
	/// marshalled to the UI thread, where the card paints its playhead over the waveform. Stop pauses and
	/// rewinds to the beginning; the end of the track does the same.
	/// </summary>
	public sealed class PulseAudioPlayer : IDisposable
	{
		private static readonly TimeSpan PositionUpdateInterval = TimeSpan.FromMilliseconds(100);

		private readonly DispatcherQueue dispatcherQueue;
		private MediaPlayer? player;
		private PulseCardViewModel? activeCard;
		private MediaSource? activeSource;
		private PulseMediaPlayback? mediaPlayback;
		private DateTimeOffset lastPositionUpdate;
		private int generation;
		private bool isDisposed;
		private CancellationTokenSource? preparation;
		private PulseCardViewModel? loadingCard;
		public event EventHandler<string>? PlaybackFailed;

		public PulseAudioPlayer(DispatcherQueue dispatcherQueue)
		{
			this.dispatcherQueue = dispatcherQueue;
		}

		/// <summary>Card whose track is currently loaded (playing or paused), if any.</summary>
		public PulseCardViewModel? ActiveCard => activeCard;

		/// <summary>Play/pause for the card's track: resumes/pauses when it is loaded, otherwise loads and starts it.</summary>
		public async Task TogglePlayPauseAsync(PulseCardViewModel card)
		{
			if (isDisposed || !card.IsAudio)
				return;

			if (ReferenceEquals(card, activeCard) && player is not null)
			{
				if (player.PlaybackSession.PlaybackState is MediaPlaybackState.Playing)
				{
					player.Pause();
					card.SetPlaybackState(active: true, playing: false);
				}
				else
				{
					player.Play();
					card.SetPlaybackState(active: true, playing: true);
				}
				return;
			}

			// Another card is active: release it first
			Stop();
			var requestGeneration = ++generation;
			preparation = new CancellationTokenSource();
			var token = preparation.Token;
			loadingCard = card;
			PulseMediaPlayback? prepared = null;

			try
			{
				// Same acquisition the previews use: a storage file, never a raw file URI
				var path = card.Item.GetRequiredPath();
				var root = await FilesystemTasks.WrapNullable(() => DriveHelpers.GetRootFromPathAsync(path));
				var file = await StorageFileExtensions.DangerousGetFileFromPathAsync(path, root.Result);

				if (isDisposed || requestGeneration != generation || file is null)
					return;

				var ffmpeg = await PulseFfmpeg.RequireAsync(token);
				prepared = await new PulseMediaGate(ffmpeg).PrepareAsync(path, token);
				if (isDisposed || requestGeneration != generation || prepared is null) return;
				var playableFile = await StorageFileExtensions.DangerousGetFileFromPathAsync(prepared.Path);
				if (isDisposed || requestGeneration != generation || playableFile is null) return;
				var mediaPlayer = GetPlayer();
				mediaPlayback = prepared;
				prepared = null;
				activeSource = MediaSource.CreateFromStorageFile(playableFile);
				activeCard = card;

				mediaPlayer.Source = activeSource;
				card.SetPlaybackState(active: true, playing: true);
				mediaPlayer.Play();
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Pulse audio playback could not start");
				if (!isDisposed && requestGeneration == generation) { Stop(); throw; }
			}
			finally
			{
				prepared?.Dispose();
				if (requestGeneration == generation) loadingCard = null;
			}
		}

		/// <summary>Stops the card's track and rewinds it; no-op for a card that is not loaded.</summary>
		public void Stop(PulseCardViewModel card)
		{
			if (ReferenceEquals(card, activeCard) || ReferenceEquals(card, loadingCard))
				Stop();
		}

		/// <summary>Jumps to <paramref name="ratio"/> (0..1) of the loaded track; no-op for a card that is not loaded.</summary>
		public void Seek(PulseCardViewModel card, double ratio)
		{
			if (player is null || !ReferenceEquals(card, activeCard))
				return;

			try
			{
				var session = player.PlaybackSession;
				if (!session.CanSeek || session.NaturalDuration <= TimeSpan.Zero)
					return;

				var position = TimeSpan.FromTicks((long)(session.NaturalDuration.Ticks * Math.Clamp(ratio, 0, 0.999)));
				session.Position = position;

				// Reflect the jump immediately; the session's own PositionChanged confirms it a moment later
				card.UpdatePlaybackPosition(position, session.NaturalDuration);
			}
			catch
			{
				// The source may be mid-transition; the next position report corrects the playhead
			}
		}

		/// <summary>Stops whatever is loaded and clears the card's playback state.</summary>
		public void Stop()
		{
			generation++;
			preparation?.Cancel();
			preparation?.Dispose();
			preparation = null;
			loadingCard = null;

			var card = activeCard;
			activeCard = null;

			try
			{
				if (player is not null)
				{
					player.Pause();
					player.PlaybackSession.Position = TimeSpan.Zero;
					player.Source = null;
				}
			}
			catch
			{
				// The source may already be gone
			}

			activeSource?.Dispose();
			activeSource = null;
			mediaPlayback?.Dispose();
			mediaPlayback = null;

			card?.SetPlaybackState(active: false, playing: false);
		}

		private MediaPlayer GetPlayer()
		{
			if (player is null)
			{
				player = new MediaPlayer
				{
					AudioCategory = MediaPlayerAudioCategory.Media,
					IsLoopingEnabled = false,
					AutoPlay = false,
				};
				player.MediaEnded += Player_MediaEnded;
				player.MediaFailed += Player_MediaFailed;
				player.PlaybackSession.PositionChanged += PlaybackSession_PositionChanged;
			}

			return player;
		}

		private void PlaybackSession_PositionChanged(MediaPlaybackSession sender, object args)
		{
			var now = DateTimeOffset.UtcNow;
			if (now - lastPositionUpdate < PositionUpdateInterval)
				return;
			lastPositionUpdate = now;

			TimeSpan position, duration;
			try
			{
				position = sender.Position;
				duration = sender.NaturalDuration;
			}
			catch
			{
				return;
			}

			var request = generation;
			var card = activeCard;
			dispatcherQueue.TryEnqueue(() =>
			{
				if (!isDisposed && request == generation && ReferenceEquals(card, activeCard)) card?.UpdatePlaybackPosition(position, duration);
			});
		}

		private void Player_MediaEnded(MediaPlayer sender, object args)
		{
			var request = generation;
			dispatcherQueue.TryEnqueue(() => { if (!isDisposed && request == generation && ReferenceEquals(player, sender)) Stop(); });
		}

		private void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
		{
			var request = generation;
			var message = $"{args.ErrorMessage} (0x{args.ExtendedErrorCode?.HResult ?? 0:X8})";
			dispatcherQueue.TryEnqueue(() =>
			{
				if (isDisposed || request != generation || !ReferenceEquals(player, sender)) return;
				Stop();
				PlaybackFailed?.Invoke(this, message);
			});
		}

		public void Dispose()
		{
			if (isDisposed)
				return;
			isDisposed = true;

			Stop();
			if (player is not null)
			{
				player.MediaEnded -= Player_MediaEnded;
				player.MediaFailed -= Player_MediaFailed;
				player.PlaybackSession.PositionChanged -= PlaybackSession_PositionChanged;
				player.Dispose();
				player = null;
			}
		}
	}
}
