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

namespace Files.App.UserControls.Pulse
{
	/// <summary>
	/// Vertical date scrubber for the Pulse feed. Each day section becomes a tick on a rail.
	/// Dragging along the rail previews the day under the pointer and asks the feed to jump
	/// there, so a folder with thousands of files is navigated by time, not by scrolling. A
	/// position thumb follows the feed's own scroll so the rail doubles as a timeline indicator,
	/// and the keyboard (Up/Down/PageUp/PageDown/Home/End) moves the active day when focused.
	/// </summary>
	public sealed partial class PulseTimeRail : UserControl
	{
		private const double MinTickSpacing = 22;
		private const int MaxLabels = 14;
		private const double DotSize = 6;
		private const double HoverDotSize = 9;

		private readonly record struct Tick(double Y, PulseSectionHeader Section, Ellipse Dot);

		private IReadOnlyList<PulseSectionHeader> sections = [];
		private readonly List<Tick> ticks = [];
		private bool isScrubbing;
		private bool isPointerOver;
		private DateTime? lastRequestedDay;
		private int activeIndex = -1;
		private double activeProgress;

		/// <summary>Raised while scrubbing (and on keyboard moves) whenever the day under the pointer changes.</summary>
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
			SizeChanged += (_, _) => Rebuild();
			Loaded += (_, _) => Rebuild();
		}

		// Geometry helpers: the content area inside the 12 px vertical margin.

		private double ContentHeight => Math.Max(0, ActualHeight - 24);

		private double TickToCanvas(double y) => y;

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
					Fill = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
					IsHitTestVisible = false,
				};
				Canvas.SetLeft(dot, Width - 15);
				Canvas.SetTop(dot, TickToCanvas(y) - DotSize / 2);
				TicksCanvas.Children.Add(dot);
				ticks.Add(new Tick(y, section, dot));

				if (i % labelEvery != 0 || y - lastLabelY < MinTickSpacing)
					continue;
				lastLabelY = y;

				var label = new TextBlock
				{
					Text = FormatLabel(section),
					FontSize = 11,
					Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
					TextAlignment = TextAlignment.Right,
					Width = Width - 24,
					IsHitTestVisible = false,
				};
				Canvas.SetLeft(label, 0);
				Canvas.SetTop(label, TickToCanvas(y) - 8);
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

			// Same year: day and month; otherwise month and year keep the rail readable
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
				// Nearest day under the thumb keeps the accessible "current day" in sync with scrolling.
				var index = (int)Math.Round(activeProgress * (ticks.Count - 1));
				SetActiveIndex(index, raise: false);
			}
			UpdateThumb();
		}

		private void UpdateThumb()
		{
			if (ActiveThumb is null || ticks.Count == 0)
				return;

			var top = activeProgress * ContentHeight;
			ActiveThumb.Margin = new Thickness(0, Math.Clamp(top - 2, 0, Math.Max(0, ContentHeight - 4)), 6, 0);
			AutomationProperties.SetName(this, ActiveDayText());
		}

		private string ActiveDayText()
		{
			if (activeIndex >= 0 && activeIndex < ticks.Count)
				return ticks[activeIndex].Section.DateText;
			return string.Empty;
		}

		[DynamicWindowsRuntimeCast(typeof(Brush))]
		private void SetActiveIndex(int index, bool raise)
		{
			if (ticks.Count == 0)
				return;

			activeIndex = Math.Clamp(index, 0, ticks.Count - 1);
			for (int i = 0; i < ticks.Count; i++)
			{
				var isActive = i == activeIndex;
				ticks[i].Dot.Width = isActive || (isPointerOver && i == HoverIndex) ? HoverDotSize : DotSize;
				ticks[i].Dot.Height = ticks[i].Dot.Width;
				Canvas.SetTop(ticks[i].Dot, TickToCanvas(ticks[i].Y) - ticks[i].Dot.Height / 2);
				ticks[i].Dot.Fill = (Brush)Application.Current.Resources[
					isActive ? "AccentFillColorDefaultBrush" : "TextFillColorSecondaryBrush"];
			}

			if (raise && ticks[activeIndex].Section.Day != lastRequestedDay)
			{
				lastRequestedDay = ticks[activeIndex].Section.Day;
				DayRequested?.Invoke(this, ticks[activeIndex].Section.Day);
			}
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

		private double hoverY = -1;

		// Pointer

		private void Rail_PointerEntered(object sender, PointerRoutedEventArgs e)
		{
			isPointerOver = true;
			hoverY = e.GetCurrentPoint(this).Position.Y - 12;
			SetActiveIndex(activeIndex, raise: false);
		}

		private void Rail_PointerExited(object sender, PointerRoutedEventArgs e)
		{
			isPointerOver = false;
			hoverY = -1;
			if (!isScrubbing)
			{
				SetActiveIndex(activeIndex, raise: false);
				ScrubBubble.Visibility = Visibility.Collapsed;
			}
		}

		private void Rail_PointerPressed(object sender, PointerRoutedEventArgs e)
		{
			if (ticks.Count == 0)
				return;

			Focus(FocusState.Pointer);
			isScrubbing = CapturePointer(e.Pointer);
			lastRequestedDay = null;
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
					ScrubText.Text = ticks[near].Section.DateText;
					ScrubBubble.Margin = new Thickness(0, Math.Clamp(ticks[near].Y - 2, 0, Math.Max(0, ActualHeight - 36)), 20, 0);
					ScrubBubble.Visibility = Visibility.Visible;
					SetActiveIndex(activeIndex, raise: false);
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

			ScrubText.Text = ticks[nearest].Section.DateText;
			ScrubBubble.Margin = new Thickness(0, Math.Clamp(ticks[nearest].Y - 2, 0, Math.Max(0, ActualHeight - 36)), 20, 0);
			ScrubBubble.VerticalAlignment = VerticalAlignment.Top;

			activeIndex = nearest;
			SetActiveIndex(nearest, raise: true);
		}

		// Keyboard

		private void Rail_KeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (ticks.Count == 0)
				return;

			var target = activeIndex;
			var page = Math.Max(1, ticks.Count / 6);
			switch (e.Key)
			{
				case VirtualKey.Up: target = activeIndex <= 0 ? 0 : activeIndex - 1; break;
				case VirtualKey.Down: target = activeIndex < 0 ? 0 : activeIndex + 1; break;
				case VirtualKey.PageUp: target = activeIndex - page; break;
				case VirtualKey.PageDown: target = activeIndex + page; break;
				case VirtualKey.Home: target = 0; break;
				case VirtualKey.End: target = ticks.Count - 1; break;
				default: return;
			}

			SetActiveIndex(target, raise: true);
			activeProgress = ticks.Count <= 1 ? 0 : activeIndex / (double)(ticks.Count - 1);
			UpdateThumb();
			e.Handled = true;
		}
	}
}
