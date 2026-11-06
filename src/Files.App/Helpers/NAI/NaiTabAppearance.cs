// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT;

namespace Files.App.Helpers
{
	public sealed class NaiTabAppearance : DependencyObject
	{
		// Mid-tone anchors keep the decorative strip visible on light and dark surfaces.
		private static readonly Color[] AccentColors =
		[
			Color.FromArgb(255, 198, 93, 84),
			Color.FromArgb(255, 159, 125, 25),
			Color.FromArgb(255, 65, 145, 78),
			Color.FromArgb(255, 24, 143, 151),
			Color.FromArgb(255, 81, 123, 206),
			Color.FromArgb(255, 157, 98, 194),
		];

		public static readonly DependencyProperty AnimateToolTipProperty = DependencyProperty.RegisterAttached(
			"AnimateToolTip", typeof(bool), typeof(NaiTabAppearance), new PropertyMetadata(false, OnAnimateToolTipChanged));

		public static bool GetAnimateToolTip(DependencyObject obj) => (bool)obj.GetValue(AnimateToolTipProperty);

		public static void SetAnimateToolTip(DependencyObject obj, bool value) => obj.SetValue(AnimateToolTipProperty, value);

		internal static SolidColorBrush CreateAccentBrush(string? path)
		{
			// Unlike string.GetHashCode, FNV-1a is stable across processes and restored sessions.
			uint hash = 2166136261;
			foreach (var character in (path ?? string.Empty).Replace('/', '\\').TrimEnd('\\').ToUpperInvariant())
				hash = unchecked((hash ^ character) * 16777619);

			// Mix nearby path hashes, then interpolate instead of limiting tabs to six colors.
			hash ^= hash >> 16;
			hash = unchecked(hash * 0x85ebca6b);
			hash ^= hash >> 13;
			var position = (hash % 3600) / 600.0;
			var start = AccentColors[(int)position];
			var end = AccentColors[((int)position + 1) % AccentColors.Length];
			var amount = position % 1;
			return new SolidColorBrush(Color.FromArgb(255,
				Interpolate(start.R, end.R, amount),
				Interpolate(start.G, end.G, amount),
				Interpolate(start.B, end.B, amount)));
		}

		private static byte Interpolate(byte start, byte end, double amount) => (byte)(start + (end - start) * amount);

		[DynamicWindowsRuntimeCast(typeof(ToolTip))]
		private static void OnAnimateToolTipChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
		{
			if (sender is not ToolTip toolTip)
				return;

			toolTip.Opened -= OnToolTipOpened;
			toolTip.Closed -= OnToolTipClosed;
			if ((bool)args.NewValue)
			{
				toolTip.Opened += OnToolTipOpened;
				toolTip.Closed += OnToolTipClosed;
			}
		}

		[DynamicWindowsRuntimeCast(typeof(ToolTip))]
		private static void OnToolTipOpened(object sender, RoutedEventArgs args)
		{
			if (sender is not ToolTip toolTip)
				return;

			var visual = ElementCompositionPreview.GetElementVisual(toolTip);
			visual.StopAnimation("Opacity");
			visual.StopAnimation("Scale");
			visual.Opacity = 1;
			visual.Scale = Vector3.One;

			// Read on every opening so a Windows reduced-motion change takes effect immediately.
			if (!new UISettings().AnimationsEnabled)
				return;

			visual.CenterPoint = new Vector3((float)toolTip.ActualWidth / 2, 0, 0);
			using var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
			fade.InsertKeyFrame(0, 0);
			fade.InsertKeyFrame(1, 1);
			fade.Duration = TimeSpan.FromMilliseconds(140);
			using var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
			scale.InsertKeyFrame(0, new Vector3(0.97f, 0.97f, 1));
			scale.InsertKeyFrame(1, Vector3.One);
			scale.Duration = fade.Duration;
			visual.StartAnimation("Opacity", fade);
			visual.StartAnimation("Scale", scale);
		}

		[DynamicWindowsRuntimeCast(typeof(ToolTip))]
		private static void OnToolTipClosed(object sender, RoutedEventArgs args)
		{
			if (sender is not ToolTip toolTip)
				return;

			var visual = ElementCompositionPreview.GetElementVisual(toolTip);
			visual.StopAnimation("Opacity");
			visual.StopAnimation("Scale");
			visual.Opacity = 1;
			visual.Scale = Vector3.One;
		}
	}
}
