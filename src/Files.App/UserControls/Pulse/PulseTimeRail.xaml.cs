// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Services.Pulse;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Globalization;

namespace Files.App.UserControls.Pulse
{
	/// <summary>
	/// Vertical date scrubber for the Pulse feed. Each day section becomes a tick on a rail;
	/// pressing and dragging along the rail shows the day under the pointer and asks the feed
	/// to jump there, so a folder with thousands of files is navigated by time, not by scrolling.
	/// </summary>
	public sealed partial class PulseTimeRail : UserControl
	{
		private const double MinTickSpacing = 22;
		private const int MaxLabels = 14;

		private IReadOnlyList<PulseSectionHeader> sections = [];
		private readonly List<(double Y, PulseSectionHeader Section)> ticks = [];
		private bool isScrubbing;
		private DateTime? lastRequestedDay;

		/// <summary>Raised while scrubbing whenever the day under the pointer changes.</summary>
		public event EventHandler<DateTime>? DayRequested;

		public IReadOnlyList<PulseSectionHeader> Sections
		{
			get => sections;
			set
			{
				sections = value ?? [];
				Rebuild();
			}
		}

		public PulseTimeRail()
		{
			InitializeComponent();
			SizeChanged += (_, _) => Rebuild();
		}

		private void Rebuild()
		{
			TicksCanvas.Children.Clear();
			ticks.Clear();

			var height = ActualHeight - 24;
			if (sections.Count == 0 || height <= 0)
			{
				Visibility = sections.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
				return;
			}

			Visibility = Visibility.Visible;

			// Ticks are spread by index (feed order), not by elapsed time: a gap of months must not
			// collapse the busy recent days into a few pixels
			var step = sections.Count == 1 ? 0 : height / (sections.Count - 1);
			var labelEvery = Math.Max(1, (int)Math.Ceiling(sections.Count / (double)MaxLabels));
			var lastLabelY = double.NegativeInfinity;

			for (int i = 0; i < sections.Count; i++)
			{
				var section = sections[i];
				var y = sections.Count == 1 ? height / 2 : i * step;
				ticks.Add((y, section));

				var dot = new Ellipse
				{
					Width = 6,
					Height = 6,
					Fill = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
				};
				Canvas.SetLeft(dot, Width - 15);
				Canvas.SetTop(dot, y - 3);
				TicksCanvas.Children.Add(dot);

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
				Canvas.SetTop(label, y - 8);
				TicksCanvas.Children.Add(label);
			}
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

		private void Rail_PointerPressed(object sender, PointerRoutedEventArgs e)
		{
			if (ticks.Count == 0)
				return;

			isScrubbing = CapturePointer(e.Pointer);
			lastRequestedDay = null;
			ScrubBubble.Visibility = Visibility.Visible;
			UpdateScrub(e.GetCurrentPoint(this).Position.Y);
			e.Handled = true;
		}

		private void Rail_PointerMoved(object sender, PointerRoutedEventArgs e)
		{
			if (!isScrubbing)
				return;

			UpdateScrub(e.GetCurrentPoint(this).Position.Y);
			e.Handled = true;
		}

		private void Rail_PointerReleased(object sender, PointerRoutedEventArgs e)
		{
			if (!isScrubbing)
				return;

			isScrubbing = false;
			ReleasePointerCaptures();
			ScrubBubble.Visibility = Visibility.Collapsed;
			e.Handled = true;
		}

		private void UpdateScrub(double pointerY)
		{
			var y = pointerY - 12;
			var nearest = ticks[0];
			var nearestDistance = double.MaxValue;
			foreach (var tick in ticks)
			{
				var distance = Math.Abs(tick.Y - y);
				if (distance < nearestDistance)
				{
					nearestDistance = distance;
					nearest = tick;
				}
			}

			ScrubText.Text = nearest.Section.DateText;
			ScrubBubble.Margin = new Thickness(0, Math.Clamp(nearest.Y - 2, 0, Math.Max(0, ActualHeight - 36)), 20, 0);
			ScrubBubble.VerticalAlignment = VerticalAlignment.Top;

			if (lastRequestedDay != nearest.Section.Day)
			{
				lastRequestedDay = nearest.Section.Day;
				DayRequested?.Invoke(this, nearest.Section.Day);
			}
		}
	}
}
