// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Globalization;

namespace Files.App.Services.Pulse
{
	/// <summary>
	/// Presentation state of one Pulse card. Wraps the <see cref="ListedItem"/> (which keeps
	/// owning name, thumbnail, dates, selection) and adds the Pulse-only parts: card family,
	/// footer texts, markdown excerpt and the version stack the item represents.
	/// Instances are cached per item by the layout page and reused across container recycling.
	/// </summary>
	public sealed partial class PulseCardViewModel : ObservableObject
	{
		private const string TimestampFormat = "dd.MM.yy HH:mm:ss";

		public ListedItem Item { get; }

		public PulseCardKind Kind { get; }

		public string TypeBadge { get; }

		public bool IsImage => Kind is PulseCardKind.Image;
		public bool IsVideo => Kind is PulseCardKind.Video;
		public bool IsAudio => Kind is PulseCardKind.Audio;
		public bool IsMarkdown => Kind is PulseCardKind.Markdown;
		public bool IsDocument => Kind is PulseCardKind.Document;
		public bool IsArchive => Kind is PulseCardKind.Archive;
		public bool IsGeneric => Kind is PulseCardKind.Generic or PulseCardKind.Archive;

		/// <summary>Edge-to-edge thumbnail families: the preview fills the card.</summary>
		public bool IsEdgeToEdge => Kind is PulseCardKind.Image or PulseCardKind.Video or PulseCardKind.Document;

		/// <summary>Media families that show a duration badge and support hover playback.</summary>
		public bool IsMedia => Kind is PulseCardKind.Video or PulseCardKind.Audio;

		/// <summary>Tooltip of the card's trim button.</summary>
		public string TrimButtonTooltip => IsVideo
			? Strings.PulseTrimVideo.GetLocalizedResource()
			: Strings.PulseTrimAudio.GetLocalizedResource();

		/// <summary>Short label of the card's trim button.</summary>
		public string TrimButtonText => Strings.PulseTrim.GetLocalizedResource();

		/// <summary>Tooltip of the card's convert button (audio/video/image conversion).</summary>
		public string ConvertButtonTooltip => IsImage
			? Strings.PulseConvertImage.GetLocalizedResource()
			: IsVideo
				? Strings.PulseConvertVideo.GetLocalizedResource()
				: Strings.PulseConvertAudio.GetLocalizedResource();

		/// <summary>Tooltip of the image card's edit button.</summary>
		public string EditButtonTooltip => Strings.PulseEditImage.GetLocalizedResource();

		/// <summary>Tooltip of the video card's tools button (frame extraction, GIF).</summary>
		public string VideoToolsButtonTooltip => Strings.PulseVideoTools.GetLocalizedResource();

		/// <summary>Tooltip of the card's split button (Demucs stem separation for audio/video).</summary>
		public string SplitButtonTooltip => IsVideo
			? Strings.PulseSplitVideo.GetLocalizedResource()
			: Strings.PulseSplitAudio.GetLocalizedResource();

		/// <summary>Absolute modification timestamp, always visible on the card.</summary>
		public string Timestamp => Item.ItemDateModifiedReal.ToLocalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

		/// <summary>Date half of <see cref="Timestamp"/>; the portrait box stacks date and time on two lines.</summary>
		public string TimestampDate => Item.ItemDateModifiedReal.ToLocalTime().ToString("dd.MM.yy", CultureInfo.InvariantCulture);

		/// <summary>Time half of <see cref="Timestamp"/>.</summary>
		public string TimestampTime => Item.ItemDateModifiedReal.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

		// Orientation: a portrait photo or video (9:16, 3:4) gets its own box, the picture uncropped at full card
		// height with the footer overlaid on its bottom edge, the way landscape video gets a wide box.

		private const double PortraitThreshold = 0.85;
		private static readonly Microsoft.UI.Xaml.Media.LinearGradientBrush PortraitFooterBackground = CreatePortraitFooterBackground();

		private double? imageAspectRatio;
		private Microsoft.UI.Xaml.Media.Imaging.BitmapImage? openingImage;
		private bool isSelected;

		/// <summary>
		/// Selection state driven by the layout page (the hidden list is the source of truth).
		/// The card chrome binds to this so every card family shows the same selection feedback.
		/// </summary>
		public bool IsSelected
		{
			get => isSelected;
			set => SetProperty(ref isSelected, value);
		}

		/// <summary>Width/height of the loaded thumbnail, or null until it is decoded.</summary>
		public double? ImageAspectRatio
		{
			get => imageAspectRatio;
			private set
			{
				if (SetProperty(ref imageAspectRatio, value))
				{
					OnPropertyChanged(nameof(IsPortrait));
					OnPropertyChanged(nameof(IsNotPortrait));
					OnPropertyChanged(nameof(FooterRow));
					OnPropertyChanged(nameof(PreviewRowSpan));
					OnPropertyChanged(nameof(FooterBackground));
					OnPropertyChanged(nameof(FooterTheme));
				}
			}
		}

		public bool IsPortrait => Kind is PulseCardKind.Image or PulseCardKind.Video && imageAspectRatio is { } ratio && ratio < PortraitThreshold;
		public bool IsNotPortrait => !IsPortrait;

		/// <summary>Grid row of the footer inside the card: below the preview (1) or over its bottom edge (0) for portraits.</summary>
		public int FooterRow => IsPortrait ? 0 : 1;

		/// <summary>Rows the preview spans: both rows for a portrait box (the footer floats over it), one otherwise.</summary>
		public int PreviewRowSpan => IsPortrait ? 2 : 1;

		/// <summary>Dark gradient under the overlaid footer of a portrait box; null (transparent) otherwise.</summary>
		public Microsoft.UI.Xaml.Media.Brush? FooterBackground => IsPortrait ? PortraitFooterBackground : null;

		/// <summary>Overlaid footer text sits on a dark gradient, so it renders with dark-theme (light) brushes.</summary>
		public Microsoft.UI.Xaml.ElementTheme FooterTheme => IsPortrait ? Microsoft.UI.Xaml.ElementTheme.Dark : Microsoft.UI.Xaml.ElementTheme.Default;

		private static Microsoft.UI.Xaml.Media.LinearGradientBrush CreatePortraitFooterBackground()
		{
			var brush = new Microsoft.UI.Xaml.Media.LinearGradientBrush
			{
				StartPoint = new Windows.Foundation.Point(0, 0),
				EndPoint = new Windows.Foundation.Point(0, 1),
			};
			brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = Windows.UI.Color.FromArgb(0, 0, 0, 0), Offset = 0 });
			brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = Windows.UI.Color.FromArgb(150, 0, 0, 0), Offset = 0.45 });
			brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop { Color = Windows.UI.Color.FromArgb(210, 0, 0, 0), Offset = 1 });
			return brush;
		}

		private void UpdateImageAspectRatio()
		{
			if (openingImage is not null)
			{
				openingImage.ImageOpened -= OpeningImage_ImageOpened;
				openingImage = null;
			}

			var image = Item.FileImage;
			if (image is null)
			{
				ImageAspectRatio = null;
				return;
			}

			if (image.PixelWidth > 0 && image.PixelHeight > 0)
			{
				ImageAspectRatio = (double)image.PixelWidth / image.PixelHeight;
				return;
			}

			// Not decoded yet: the dimensions arrive with ImageOpened
			openingImage = image;
			image.ImageOpened += OpeningImage_ImageOpened;
		}

		private void OpeningImage_ImageOpened(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
		{
			if (sender is Microsoft.UI.Xaml.Media.Imaging.BitmapImage image)
			{
				image.ImageOpened -= OpeningImage_ImageOpened;
				if (ReferenceEquals(image, openingImage))
					openingImage = null;
				if (ReferenceEquals(image, Item.FileImage) && image.PixelWidth > 0 && image.PixelHeight > 0)
					ImageAspectRatio = (double)image.PixelWidth / image.PixelHeight;
			}
		}

		private PulseMarkdownExcerpt? markdownExcerpt;
		public PulseMarkdownExcerpt? MarkdownExcerpt
		{
			get => markdownExcerpt;
			set
			{
				if (SetProperty(ref markdownExcerpt, value))
				{
					OnPropertyChanged(nameof(HasMarkdownExcerpt));
					OnPropertyChanged(nameof(IsAgentSpec));
				}
			}
		}

		public bool HasMarkdownExcerpt => markdownExcerpt is not null;
		public bool IsAgentSpec => markdownExcerpt?.IsAgentSpec ?? false;

		private FileStack? stack;
		/// <summary>Set when this card is the visible representative (latest file) of a version stack.</summary>
		public FileStack? Stack
		{
			get => stack;
			set
			{
				if (SetProperty(ref stack, value))
					NotifyStackStateChanged();
			}
		}

		/// <summary>True whenever the card represents a version stack, collapsed or expanded.</summary>
		public bool HasStack => stack is not null;

		/// <summary>Collapsed stack: the card stands in for all its versions.</summary>
		public bool IsStack => stack is not null && !isStackExpanded;

		/// <summary>Expanded stack: the versions are shown as individual cards and this one offers to collapse them.</summary>
		public bool IsExpandedStack => stack is not null && isStackExpanded;

		public string StackCountText => stack is null ? string.Empty : string.Format(Strings.PulseVersionsCount.GetLocalizedResource(), stack.Count);

		public string StackLatestText => stack is null ? string.Empty : $"{Strings.PulseLatestVersion.GetLocalizedResource()} {Timestamp}";

		private bool isStackExpanded;
		/// <summary>True while the stack is opened inline and its members are shown as individual cards.</summary>
		public bool IsStackExpanded
		{
			get => isStackExpanded;
			set
			{
				if (SetProperty(ref isStackExpanded, value))
					NotifyStackStateChanged();
			}
		}

		private void NotifyStackStateChanged()
		{
			OnPropertyChanged(nameof(HasStack));
			OnPropertyChanged(nameof(IsStack));
			OnPropertyChanged(nameof(IsExpandedStack));
			OnPropertyChanged(nameof(StackCountText));
			OnPropertyChanged(nameof(StackLatestText));
		}

		private bool isHoverPreviewActive;
		/// <summary>True while the shared hover player is attached to this card.</summary>
		public bool IsHoverPreviewActive
		{
			get => isHoverPreviewActive;
			set => SetProperty(ref isHoverPreviewActive, value);
		}

		private bool showsSize;
		/// <summary>
		/// Whether the footer has room for the file size next to the timestamp. Set by the flow layout
		/// from the card's aspect ratio: the absolute timestamp is never sacrificed for the size.
		/// </summary>
		public bool ShowsSize
		{
			get => showsSize;
			set => SetProperty(ref showsSize, value);
		}

		// Audio: spectral waveform painted by PulseWaveformService

		private PulseWaveform? waveform;
		/// <summary>Analyzed spectral waveform of an audio card; null until the batch queue has processed it.</summary>
		public PulseWaveform? Waveform
		{
			get => waveform;
			set => SetProperty(ref waveform, value);
		}

		private Microsoft.UI.Xaml.Media.ImageSource? waveformImage;
		/// <summary>Rendered waveform strip (Traktor-style colours) bound by the audio card.</summary>
		public Microsoft.UI.Xaml.Media.ImageSource? WaveformImage
		{
			get => waveformImage;
			set
			{
				if (SetProperty(ref waveformImage, value))
					OnPropertyChanged(nameof(HasWaveform));
			}
		}

		public bool HasWaveform => waveformImage is not null;

		private bool waveformUnavailable;
		/// <summary>True when the file could not be decoded; the card keeps its static art and is not re-queued.</summary>
		public bool WaveformUnavailable
		{
			get => waveformUnavailable;
			set => SetProperty(ref waveformUnavailable, value);
		}

		// Audio playback (driven by PulseAudioPlayer on the UI thread)

		private bool isPlaybackActive;
		/// <summary>True while this card's track is loaded in the shared player (playing or paused).</summary>
		public bool IsPlaybackActive
		{
			get => isPlaybackActive;
			private set
			{
				if (SetProperty(ref isPlaybackActive, value))
					OnPropertyChanged(nameof(PlaybackBadgeText));
			}
		}

		private bool isPlaying;
		public bool IsPlaying
		{
			get => isPlaying;
			private set
			{
				if (SetProperty(ref isPlaying, value))
				{
					OnPropertyChanged(nameof(PlayPauseGlyph));
					OnPropertyChanged(nameof(PlayPauseLabel));
				}
			}
		}

		/// <summary>Segoe Fluent glyph: Play while idle/paused, Pause while playing.</summary>
		public string PlayPauseGlyph => isPlaying ? "\uE769" : "\uE768";

		public string PlayPauseLabel => (isPlaying ? Strings.PulsePause : Strings.Play).GetLocalizedResource();

		private double playbackProgress;
		/// <summary>0..1 position within the track reported by the player; the playhead glides between reports.</summary>
		public double PlaybackProgress
		{
			get => playbackProgress;
			private set => SetProperty(ref playbackProgress, value);
		}

		private TimeSpan playbackDuration;
		/// <summary>Natural duration of the loaded track; lets the playhead pace its glide.</summary>
		public TimeSpan PlaybackDuration
		{
			get => playbackDuration;
			private set => SetProperty(ref playbackDuration, value);
		}

		private string? playbackPositionText;
		/// <summary>Badge text: "m:ss / m:ss" while the track is loaded, the file's duration otherwise.</summary>
		public string? PlaybackBadgeText => isPlaybackActive && playbackPositionText is not null ? playbackPositionText : Item.MediaDuration;

		/// <summary>Called by the player on the UI thread when this card's track starts, pauses, resumes or stops.</summary>
		public void SetPlaybackState(bool active, bool playing)
		{
			IsPlaybackActive = active;
			IsPlaying = active && playing;
			if (!active)
			{
				PlaybackProgress = 0;
				PlaybackDuration = TimeSpan.Zero;
				playbackPositionText = null;
				OnPropertyChanged(nameof(PlaybackBadgeText));
			}
		}

		/// <summary>Called by the player on the UI thread with the current position.</summary>
		public void UpdatePlaybackPosition(TimeSpan position, TimeSpan duration)
		{
			PlaybackDuration = duration;
			PlaybackProgress = duration > TimeSpan.Zero ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1) : 0;
			playbackPositionText = $"{Format(position)} / {Format(duration)}";
			OnPropertyChanged(nameof(PlaybackBadgeText));

			static string Format(TimeSpan value)
				=> value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);
		}

		public PulseCardViewModel(ListedItem item)
		{
			Item = item;
			Kind = PulseCardClassifier.Classify(item);
			TypeBadge = PulseCardClassifier.GetTypeBadge(item);

			item.PropertyChanged += Item_PropertyChanged;
			UpdateImageAspectRatio();
		}

		private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(ListedItem.ItemDateModifiedReal) or nameof(ListedItem.ItemDateModified))
			{
				OnPropertyChanged(nameof(Timestamp));
				OnPropertyChanged(nameof(TimestampDate));
				OnPropertyChanged(nameof(TimestampTime));
				OnPropertyChanged(nameof(StackLatestText));
			}
			else if (e.PropertyName is nameof(ListedItem.FileImage))
			{
				UpdateImageAspectRatio();
			}
			else if (e.PropertyName is nameof(ListedItem.MediaDuration))
			{
				OnPropertyChanged(nameof(PlaybackBadgeText));
			}
		}

		/// <summary>True once the card's item left the feed; a playing track must then stop.</summary>
		public bool IsDetached { get; private set; }

		public void Detach()
		{
			IsDetached = true;
			Item.PropertyChanged -= Item_PropertyChanged;
			if (openingImage is not null)
			{
				openingImage.ImageOpened -= OpeningImage_ImageOpened;
				openingImage = null;
			}
		}
	}
}
