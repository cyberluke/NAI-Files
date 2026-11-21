// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Services.Pulse;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Globalization;
using Windows.System;
using WinRT;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Files.App.UserControls.Pulse
{
	/// <summary>
	/// Vertical date scrubber for the Pulse feed. Each day section becomes a tick on a rail.
	/// Dragging along the rail previews the day under the pointer and asks the feed to jump
	/// there, so a folder with thousands of files is navigated by time, not by scrolling. A
	/// position thumb follows the feed's own scroll so the rail doubles as a timeline indicator,
	/// and the keyboard (Up/Down/PageUp/PageDown/Home/End) moves the active day when focused.
	///
	/// The rail is deliberately wide (a 22 px grab track, larger dots and thumb) so it is easy
	/// to hit on 4K. Navigation is buffered: while scrubbing only the preview follows the pointer,
	/// and the actual jump is committed after a short pause or on release, which keeps the feed
	/// from lurching on every pixel of movement.
	/// </summary>
	public sealed partial class PulseTimeRail : UserControl
	{
		private const double MinTickSpacing = 22;
		private const int MaxLabels = 14;
		private const double DotSize = 8;
		private const double HoverDotSize = 12;
		private const double SpineCenterFromRight = 10.5;
		private const int NavigateDebounceMs = 120;

		private readonly record struct Tick(double Y, PulseSectionHeader Section, Ellipse Dot);

		private readonly DispatcherQueueTimer? navigationTimer;
		private IReadOnlyList<PulseSectionHeader> sections = [];
		private readonly List<Tick> ticks = [];
		private bool isScrubbing;
		private bool isPointerOver;
		private double hoverY = -1;
		private int activeIndex = -1;
		private double activeProgress;
		private DateTime? pendingDay;
		private DateTime? lastRaisedDay;

		/// <summary>Raised when the buffered day selection settles (after a short pause or on release).</summary>
		public event EventHandler<DateTime>? DayRequested;

		public IReadOnlyList<PulseSectionHeader> Sections
		{
			get => sections;
			set
			{
				sections = value ?? [];
				if (activeIndex >= sections.Count)
					activeIndex = sections.Count - 1;
				Rebuild();
			}
		}

		public PulseTimeRail()
		{
			InitializeComponent();

			navigationTimer = DispatcherQueue.CreateTimer();
			navigationTimer.Interval = TimeSpan.FromMilliseconds(NavigateDebounceMs);
			navigationTimer.IsRepeating = false;
			navigationTimer.Tick += (_, _) => RaisePendingDay();

			SizeChanged += (_, _) => Rebuild();
			Loaded += (_, _) => Rebuild();
		}

		private double ContentHeight => Math.Max(0, ActualHeight - 24);

		[DynamicWindowsRuntimeCast(typeof(Brush))]
		private void Rebuild()
		{
			TicksCanvas.Children.Clear();
			ticks.Clear();

			var height = ContentHeight;
			if (sections.Count == 0 || height <= 0)
			{
				Visibility = sections.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
				ActiveThumb.Visibility = Visibility.Collapsed;
				return;
			}

			Visibility = Visibility.Visible;

			var secondaryBrush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
			var labelWidth = Math.Max(0, Width - 30);

			// Ticks are spread by index (feed order), not by elapsed time: a gap of months must not
			// collapse the busy recent days into a few pixels.
			var step = sections.Count == 1 ? 0 : height / (sections.Count - 1);
			var labelEvery = Math.Max(1, (int)Math.Ceiling(sections.Count / (double)MaxLabels));
			var lastLabelY = double.NegativeInfinity;

			for (int i = 0; i < sections.Count; i++)
			{
				var section = sections[i];
				var y = sections.Count == 1 ? height / 2 : i * step;

				var dot = new Ellipse
				{
					Width = DotSize,
					Height = DotSize,
					Fill = secondaryBrush,
					IsHitTestVisible = false,
				};
				Canvas.SetLeft(dot, Width - SpineCenterFromRight - DotSize / 2);
				Canvas.SetTop(dot, y - DotSize / 2);
				TicksCanvas.Children.Add(dot);
				ticks.Add(new Tick(y, section, dot));

				if (i % labelEvery != 0 || y - lastLabelY < MinTickSpacing)
					continue;
				lastLabelY = y;

				var label = new TextBlock
				{
					Text = FormatLabel(section),
					FontSize = 11,
					Foreground = secondaryBrush,
					TextAlignment = TextAlignment.Right,
					Width = labelWidth,
					IsHitTestVisible = false,
				};
				Canvas.SetLeft(label, 0);
				Canvas.SetTop(label, y - 8);
				TicksCanvas.Children.Add(label);
			}

			ActiveThumb.Visibility = Visibility.Visible;
			UpdateThumb();
		}

		private static string FormatLabel(PulseSectionHeader section)
		{
			var today = DateTime.Today;
			if (section.Day == today || section.Day == today.AddDays(-1))
				return section.Label;

			return section.Day.Year == today.Year
				? section.Day.ToString("dd.MM.", CultureInfo.InvariantCulture)
				: section.Day.ToString("MM.yy", CultureInfo.InvariantCulture);
		}

		/// <summary>
		/// Places the position thumb from the owning feed's scroll ratio (0 at the top, 1 at the
		/// bottom). This is what makes the rail reflect where the user currently is.
		/// </summary>
		public void SetActiveProgress(double ratio)
		{
			activeProgress = double.IsNaN(ratio) ? 0 : Math.Clamp(ratio, 0, 1);

			if (ticks.Count > 0)
			{
				var index = (int)Math.Round(activeProgress * (ticks.Count - 1));
				if (index != activeIndex)
					SetActiveIndex(index);
			}

			UpdateThumb();
		}

		private void UpdateThumb()
		{
			if (ActiveThumb is null || ticks.Count == 0)
				return;

			var top = Math.Clamp(activeProgress * ContentHeight - ActiveThumb.Height / 2, 0, Math.Max(0, ContentHeight - ActiveThumb.Height));
			ActiveThumb.Margin = new Thickness(0, top, 3, 0);
		}

		[DynamicWindowsRuntimeCast(typeof(Brush))]
		private void SetActiveIndex(int index)
		{
			if (ticks.Count == 0)
				return;

			activeIndex = Math.Clamp(index, 0, ticks.Count - 1);

			for (int i = 0; i < ticks.Count; i++)
			{
				var isActive = i == activeIndex;
				var isHover = isPointerOver && i == HoverIndex;
				var size = isActive || isHover ? HoverDotSize : DotSize;
				var dot = ticks[i].Dot;

				if (Math.Abs(dot.Width - size) > 0.01)
				{
					dot.Width = size;
					dot.Height = size;
				}

				Canvas.SetTop(dot, ticks[i].Y - size / 2);
				dot.Fill = (Brush)Application.Current.Resources[
					isActive ? "AccentFillColorDefaultBrush" : "TextFillColorSecondaryBrush"];
			}

			AutomationProperties.SetName(this, ticks[activeIndex].Section.DateText);
		}

		private int HoverIndex
		{
			get
			{
				if (hoverY < 0)
					return -1;

				var nearest = -1;
				var best = double.MaxValue;
				for (int i = 0; i < ticks.Count; i++)
				{
					var d = Math.Abs(ticks[i].Y - hoverY);
					if (d < best) { best = d; nearest = i; }
				}
				return nearest;
			}
		}

		// Buffered navigation

		private void RequestDay(DateTime day, bool immediate)
		{
			pendingDay = day;

			if (immediate || navigationTimer is null)
			{
				navigationTimer?.Stop();
				RaisePendingDay();
				return;
			}

			// Restart the one-shot timer so a continuous drag only navigates once the pointer rests.
			navigationTimer.Stop();
			navigationTimer.Start();
		}

		private void RaisePendingDay()
		{
			if (pendingDay is not { } day || day == lastRaisedDay)
				return;

			lastRaisedDay = day;
			DayRequested?.Invoke(this, day);
		}

		// Pointer

		private void Rail_PointerEntered(object sender, PointerRoutedEventArgs e)
		{
			isPointerOver = true;
			hoverY = e.GetCurrentPoint(this).Position.Y - 12;
			SetActiveIndex(activeIndex < 0 ? 0 : activeIndex);
		}

		private void Rail_PointerExited(object sender, PointerRoutedEventArgs e)
		{
			isPointerOver = false;
			hoverY = -1;

			if (!isScrubbing)
			{
				SetActiveIndex(activeIndex < 0 ? 0 : activeIndex);
				ScrubBubble.Visibility = Visibility.Collapsed;
			}
		}

		private void Rail_PointerPressed(object sender, PointerRoutedEventArgs e)
		{
			if (ticks.Count == 0)
				return;

			Focus(FocusState.Pointer);
			isScrubbing = CapturePointer(e.Pointer);
			lastRaisedDay = null;
			ScrubBubble.Visibility = Visibility.Visible;
			UpdateScrub(e.GetCurrentPoint(this).Position.Y);
			e.Handled = true;
		}

		private void Rail_PointerMoved(object sender, PointerRoutedEventArgs e)
		{
			var pointerY = e.GetCurrentPoint(this).Position.Y;
			hoverY = pointerY - 12;

			if (!isScrubbing)
			{
				// Live hover preview: highlight the nearest day, no navigation yet.
				var near = HoverIndex;
				if (near >= 0)
				{
					ShowBubble(near);
					SetActiveIndex(near);
				}
				return;
			}

			UpdateScrub(pointerY);
			e.Handled = true;
		}

		private void Rail_PointerReleased(object sender, PointerRoutedEventArgs e)
		{
			if (!isScrubbing)
				return;

			isScrubbing = false;
			ReleasePointerCaptures();

			// Flush the buffered jump immediately on release.
			if (activeIndex >= 0 && activeIndex < ticks.Count)
				RequestDay(ticks[activeIndex].Section.Day, immediate: true);

			ScrubBubble.Visibility = isPointerOver ? Visibility.Visible : Visibility.Collapsed;
			e.Handled = true;
		}

		private void UpdateScrub(double pointerY)
		{
			if (ticks.Count == 0)
				return;

			var y = pointerY - 12;
			var nearest = 0;
			var nearestDistance = double.MaxValue;
			for (int i = 0; i < ticks.Count; i++)
			{
				var distance = Math.Abs(ticks[i].Y - y);
				if (distance < nearestDistance)
				{
					nearestDistance = distance;
					nearest = i;
				}
			}

			ShowBubble(nearest);
			SetActiveIndex(nearest);

			// Buffer the actual jump; the preview above remains instant.
			RequestDay(ticks[nearest].Section.Day, immediate: false);
		}

		private void ShowBubble(int index)
		{
			ScrubText.Text = ticks[index].Section.DateText;
			ScrubBubble.Margin = new Thickness(0, Math.Clamp(ticks[index].Y - 2, 0, Math.Max(0, ActualHeight - 36)), 27, 0);
			ScrubBubble.VerticalAlignment = VerticalAlignment.Top;
			ScrubBubble.Visibility = Visibility.Visible;
		}

		// Keyboard

		private void Rail_KeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (ticks.Count == 0)
				return;

			var current = activeIndex < 0 ? 0 : activeIndex;
			var page = Math.Max(1, ticks.Count / 6);
			var target = e.Key switch
			{
				VirtualKey.Up => current - 1,
				VirtualKey.Down => current + 1,
				VirtualKey.PageUp => current - page,
				VirtualKey.PageDown => current + page,
				VirtualKey.Home => 0,
				VirtualKey.End => ticks.Count - 1,
				_ => int.MinValue,
			};

			if (target == int.MinValue)
				return;

			SetActiveIndex(target);
			activeProgress = ticks.Count <= 1 ? 0 : activeIndex / (double)(ticks.Count - 1);
			UpdateThumb();
			ShowBubble(activeIndex);
			RequestDay(ticks[activeIndex].Section.Day, immediate: true);
			e.Handled = true;
		}
	}
}
