// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Files.App.UserControls.Pulse
{
	/// <summary>
	/// Playhead over an audio card's waveform. The position does not jump between the player's position
	/// reports: a linear composition animation glides a "Progress" scalar from the current position to the
	/// end of the track over the remaining time, and expression animations map it onto the 1 px line
	/// (pixel-snapped with Floor so the line stays crisp) and the played-part overlay. Position reports only
	/// re-synchronize the glide when the player drifted away from the predicted position (seek, stall).
	/// </summary>
	public sealed partial class PulsePlayhead : UserControl
	{
		// A report this far from the predicted position restarts the glide; smaller deltas are measurement jitter
		private static readonly TimeSpan ResyncTolerance = TimeSpan.FromMilliseconds(200);

		public static readonly DependencyProperty ProgressProperty =
			DependencyProperty.Register(nameof(Progress), typeof(double), typeof(PulsePlayhead), new PropertyMetadata(0d, OnStateChanged));

		public static readonly DependencyProperty IsPlayingProperty =
			DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(PulsePlayhead), new PropertyMetadata(false, OnStateChanged));

		public static readonly DependencyProperty DurationProperty =
			DependencyProperty.Register(nameof(Duration), typeof(TimeSpan), typeof(PulsePlayhead), new PropertyMetadata(TimeSpan.Zero, OnStateChanged));

		/// <summary>Position reported by the player, 0..1 of the track.</summary>
		public double Progress
		{
			get => (double)GetValue(ProgressProperty);
			set => SetValue(ProgressProperty, value);
		}

		/// <summary>Whether the track advances; while true the playhead glides on its own clock.</summary>
		public bool IsPlaying
		{
			get => (bool)GetValue(IsPlayingProperty);
			set => SetValue(IsPlayingProperty, value);
		}

		/// <summary>Total track length; with it the glide knows how long the remaining distance takes.</summary>
		public TimeSpan Duration
		{
			get => (TimeSpan)GetValue(DurationProperty);
			set => SetValue(DurationProperty, value);
		}

		private Compositor? compositor;
		private CompositionPropertySet? state;
		private bool isGliding;
		private DateTimeOffset glideStartedAt;
		private double glideStartProgress;

		public PulsePlayhead()
		{
			InitializeComponent();
			Loaded += (_, _) => Sync();
			Unloaded += (_, _) => StopGlide(Progress);
			SizeChanged += (_, e) => state?.InsertScalar("Width", (float)Math.Max(1, e.NewSize.Width));
		}

		private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
			=> ((PulsePlayhead)d).Sync();

		private void EnsureComposition()
		{
			if (state is not null)
				return;

			compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
			state = compositor.CreatePropertySet();
			state.InsertScalar("Progress", (float)Math.Clamp(Progress, 0, 1));
			state.InsertScalar("Width", (float)Math.Max(1, ActualWidth));

			// Line and halo: translate to the pixel-snapped position
			Line.StartAnimation(CreateTranslation());
			LineHalo.StartAnimation(CreateTranslation());

			// Played overlay: scale from the left edge to the same snapped position
			var scale = compositor.CreateExpressionAnimation("Vector3(Floor(s.Progress * s.Width) / Max(s.Width, 1), 1, 1)");
			scale.SetReferenceParameter("s", state);
			scale.Target = "Scale";
			PlayedOverlay.StartAnimation(scale);
		}

		private ExpressionAnimation CreateTranslation()
		{
			var translation = compositor!.CreateExpressionAnimation("Vector3(Floor(s.Progress * s.Width), 0, 0)");
			translation.SetReferenceParameter("s", state);
			translation.Target = "Translation";
			return translation;
		}

		private void Sync()
		{
			if (!IsLoaded)
				return;

			EnsureComposition();

			var progress = Math.Clamp(Progress, 0, 1);
			var duration = Duration;

			if (!IsPlaying || duration <= TimeSpan.Zero || progress >= 1)
			{
				StopGlide(progress);
				return;
			}

			if (isGliding)
			{
				// Still on track: the player's report agrees with where the glide already is
				var predicted = glideStartProgress + (DateTimeOffset.UtcNow - glideStartedAt).TotalSeconds / duration.TotalSeconds;
				if (Math.Abs(predicted - progress) * duration.TotalSeconds <= ResyncTolerance.TotalSeconds)
					return;
			}

			StartGlide(progress, duration * (1 - progress));
		}

		private void StartGlide(double from, TimeSpan remaining)
		{
			if (compositor is null || state is null)
				return;

			// Composition keyframe durations must stay within 1 ms .. 24 days
			if (remaining < TimeSpan.FromMilliseconds(1))
				remaining = TimeSpan.FromMilliseconds(1);
			else if (remaining > TimeSpan.FromDays(24))
				remaining = TimeSpan.FromDays(24);

			var glide = compositor.CreateScalarKeyFrameAnimation();
			glide.InsertKeyFrame(0f, (float)from);
			glide.InsertKeyFrame(1f, 1f, compositor.CreateLinearEasingFunction());
			glide.Duration = remaining;

			state.StartAnimation("Progress", glide);
			isGliding = true;
			glideStartedAt = DateTimeOffset.UtcNow;
			glideStartProgress = from;
		}

		private void StopGlide(double progress)
		{
			if (state is null)
				return;

			state.StopAnimation("Progress");
			state.InsertScalar("Progress", (float)Math.Clamp(progress, 0, 1));
			isGliding = false;
		}
	}
}
