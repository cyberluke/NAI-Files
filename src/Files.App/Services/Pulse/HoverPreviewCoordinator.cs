// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Files.App.Services.Pulse
{
	/// <summary>
	/// One <see cref="MediaPlayer"/> for the whole Pulse feed. Hovering a video card for
	/// 250 ms attaches the player to that card's <see cref="MediaPlayerElement"/> and starts a
	/// muted autoplay; moving the pointer across the card scrubs proportionally (YouTube-style);
	/// leaving the card pauses and detaches. An unsupported file leaves the static thumbnail in place.
	/// </summary>
	public sealed class HoverPreviewCoordinator : IDisposable
	{
		private static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(250);
		private static readonly TimeSpan SeekThrottle = TimeSpan.FromMilliseconds(120);

		private readonly DispatcherQueue dispatcherQueue;
		private readonly DispatcherQueueTimer hoverTimer;
		private MediaPlayer? player;

		private PulseCardViewModel? pendingCard;
		private MediaPlayerElement? pendingHost;
		private PulseCardViewModel? activeCard;
		private MediaPlayerElement? activeHost;
		private MediaSource? activeSource;
		private PulseMediaPlayback? mediaPlayback;
		private int startGeneration;
		private DateTimeOffset lastSeek;
		private bool isDisposed;
		private CancellationTokenSource? preparation;
		private PulseCardViewModel? loadingCard;

		public HoverPreviewCoordinator(DispatcherQueue dispatcherQueue)
		{
			this.dispatcherQueue = dispatcherQueue;

			hoverTimer = dispatcherQueue.CreateTimer();
			hoverTimer.Interval = HoverDelay;
			hoverTimer.IsRepeating = false;
			hoverTimer.Tick += HoverTimer_Tick;
		}

		/// <summary>Pointer entered a video card; the preview starts after the hover delay.</summary>
		public void Enter(PulseCardViewModel card, MediaPlayerElement host)
		{
			if (isDisposed || card.Kind is not PulseCardKind.Video)
				return;

			if (ReferenceEquals(card, activeCard))
				return;

			pendingCard = card;
			pendingHost = host;
			hoverTimer.Stop();
			hoverTimer.Start();
		}

		/// <summary>Pointer left the card: cancel a pending start or stop the running preview immediately.</summary>
		public void Leave(PulseCardViewModel card)
		{
			if (ReferenceEquals(card, loadingCard))
			{
				startGeneration++;
				preparation?.Cancel();
				loadingCard = null;
			}
			if (ReferenceEquals(card, pendingCard))
			{
				hoverTimer.Stop();
				pendingCard = null;
				pendingHost = null;
			}

			if (ReferenceEquals(card, activeCard))
				Detach();
		}

		/// <summary>Pointer moved across the active card; <paramref name="ratio"/> is 0..1 of the card width.</summary>
		public void Scrub(PulseCardViewModel card, double ratio)
		{
			if (player is null || !ReferenceEquals(card, activeCard))
				return;

			try
			{
				var session = player.PlaybackSession;
				if (!session.CanSeek || session.NaturalDuration <= TimeSpan.Zero)
					return;

				var now = DateTimeOffset.UtcNow;
				if (now - lastSeek < SeekThrottle)
					return;
				lastSeek = now;

				var clamped = Math.Clamp(ratio, 0d, 0.999d);
				session.Position = TimeSpan.FromTicks((long)(session.NaturalDuration.Ticks * clamped));
			}
			catch
			{
				// Seeking a source that is still opening is not an error worth surfacing
			}
		}

		/// <summary>Stops any preview, e.g. when the page navigates away or the feed rebuilds.</summary>
		public void Reset()
		{
			hoverTimer.Stop();
			pendingCard = null;
			pendingHost = null;
			startGeneration++;
			preparation?.Cancel();
			preparation?.Dispose();
			preparation = null;
			loadingCard = null;
			Detach();
		}

		private MediaPlayer GetPlayer()
		{
			if (player is null)
			{
				player = new MediaPlayer
				{
					IsMuted = true,
					IsLoopingEnabled = true,
					AutoPlay = true,
				};
				// Previews must never fight with the system now-playing session
				player.CommandManager.IsEnabled = false;
				player.MediaFailed += Player_MediaFailed;
			}

			return player;
		}

		private async void HoverTimer_Tick(DispatcherQueueTimer sender, object args)
		{
			if (isDisposed || pendingCard is null || pendingHost is null)
				return;

			var card = pendingCard;
			var host = pendingHost;
			pendingCard = null;
			pendingHost = null;

			Detach();
			var generation = ++startGeneration;
			preparation?.Cancel();
			preparation?.Dispose();
			preparation = new CancellationTokenSource();
			var token = preparation.Token;
			loadingCard = card;
			PulseMediaPlayback? prepared = null;

			try
			{
				// Same acquisition the info-pane preview uses: a storage file, never a raw file URI
				var path = card.Item.GetRequiredPath();
				var root = await FilesystemTasks.WrapNullable(() => DriveHelpers.GetRootFromPathAsync(path));
				var file = await StorageFileExtensions.DangerousGetFileFromPathAsync(path, root.Result);

				// The pointer may have left while the file was being resolved
				if (isDisposed || generation != startGeneration || file is null)
					return;

				var ffmpeg = await PulseFfmpeg.RequireAsync(token);
				prepared = await new PulseMediaGate(ffmpeg).PrepareAsync(path, token);
				if (isDisposed || generation != startGeneration || prepared is null) return;
				var playableFile = await StorageFileExtensions.DangerousGetFileFromPathAsync(prepared.Path);
				if (isDisposed || generation != startGeneration || playableFile is null || !host.IsLoaded) return;

				var source = MediaSource.CreateFromStorageFile(playableFile);
				var mediaPlayer = GetPlayer();
				mediaPlayback = prepared;
				prepared = null;

				activeSource = source;
				activeCard = card;
				activeHost = host;

				host.SetMediaPlayer(mediaPlayer);
				mediaPlayer.Source = source;
				card.IsHoverPreviewActive = true;
				mediaPlayer.Play();
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Pulse hover preview could not start");
				if (!isDisposed && generation == startGeneration) Detach();
			}
			finally
			{
				prepared?.Dispose();
				if (generation == startGeneration) loadingCard = null;
			}
		}

		private void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
		{
			// Unsupported container/codec: fall back to the static thumbnail without blocking the feed
			var request = startGeneration;
			dispatcherQueue.TryEnqueue(() =>
			{
				if (!isDisposed && request == startGeneration && ReferenceEquals(player, sender)) Detach();
			});
		}

		private void Detach()
		{
			if (activeCard is not null)
				activeCard.IsHoverPreviewActive = false;

			try
			{
				if (player is not null)
				{
					player.Pause();
					player.Source = null;
				}
				activeHost?.SetMediaPlayer(null);
			}
			catch
			{
				// The host may already be unloaded or recycled
			}

			activeSource?.Dispose();
			activeSource = null;
			mediaPlayback?.Dispose();
			mediaPlayback = null;
			activeCard = null;
			activeHost = null;
		}

		public void Dispose()
		{
			if (isDisposed)
				return;
			isDisposed = true;

			Reset();
			if (player is not null)
			{
				player.MediaFailed -= Player_MediaFailed;
				player.Dispose();
				player = null;
			}
		}
	}
}
