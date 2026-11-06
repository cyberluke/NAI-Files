// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Numerics;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Files.App.Helpers
{
	/// <summary>Native WinUI counterpart of NAI Apps SearchSparkle.svelte (20×20 geometry).</summary>
	public sealed partial class NaiSearchIcon : Grid
	{
		private readonly Path sparkle;
		private readonly UISettings uiSettings = new();
		public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(nameof(IsBusy),
			typeof(bool), typeof(NaiSearchIcon), new PropertyMetadata(false, (owner, _) => ((NaiSearchIcon)owner).Animate()));
		public bool IsBusy { get => (bool)GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }

		[WinRT.DynamicWindowsRuntimeCast(typeof(Brush))]
		public NaiSearchIcon()
		{
			Width = Height = 22;
			IsHitTestVisible = false;
			var canvas = new Canvas { Width = 20, Height = 20 };
			Children.Add(new Viewbox { Child = canvas });
			var stroke = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"];
			var ring = new Ellipse { Width = 8.6, Height = 8.6, Stroke = stroke, StrokeThickness = 1.6 };
			Canvas.SetLeft(ring, 4.4); Canvas.SetTop(ring, 4.4);
			canvas.Children.Add(ring);
			canvas.Children.Add(new Line { X1 = 12, Y1 = 12, X2 = 16.4, Y2 = 16.4,
				Stroke = stroke, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
			var figure = new PathFigure { StartPoint = new Point(14.9, 1.7), IsClosed = true };
			figure.Segments.Add(Curve(15.1, 3.3, 16.3, 4.5, 17.9, 4.7));
			figure.Segments.Add(Curve(16.3, 4.9, 15.1, 6.1, 14.9, 7.7));
			figure.Segments.Add(Curve(14.7, 6.1, 13.5, 4.9, 11.9, 4.7));
			figure.Segments.Add(Curve(13.5, 4.5, 14.7, 3.3, 14.9, 1.7));
			var geometry = new PathGeometry(); geometry.Figures.Add(figure);
			var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
			gradient.GradientStops.Add(new GradientStop { Offset = 0, Color = Windows.UI.Color.FromArgb(255, 77, 235, 255) });
			gradient.GradientStops.Add(new GradientStop { Offset = .55, Color = Windows.UI.Color.FromArgb(255, 141, 124, 255) });
			gradient.GradientStops.Add(new GradientStop { Offset = 1, Color = Windows.UI.Color.FromArgb(255, 255, 93, 143) });
			sparkle = new Path { Data = geometry, Fill = gradient };
			canvas.Children.Add(sparkle);
			Loaded += (_, _) => { uiSettings.AnimationsEnabledChanged += AnimationsChanged; Animate(); };
			Unloaded += (_, _) => { uiSettings.AnimationsEnabledChanged -= AnimationsChanged; Stop(); };
		}

		private static BezierSegment Curve(double x1, double y1, double x2, double y2, double x3, double y3)
			=> new() { Point1 = new(x1, y1), Point2 = new(x2, y2), Point3 = new(x3, y3) };
		private void AnimationsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(Animate);
		private void Stop()
		{
			var visual = ElementCompositionPreview.GetElementVisual(sparkle);
			visual.StopAnimation("Opacity"); visual.StopAnimation("Scale");
			visual.Opacity = 1; visual.Scale = Vector3.One;
		}
		private void Animate()
		{
			if (!IsLoaded) return;
			Stop();
			if (!IsBusy || !uiSettings.AnimationsEnabled) return;
			var visual = ElementCompositionPreview.GetElementVisual(sparkle);
			visual.CenterPoint = new Vector3(14.9f, 4.7f, 0);
			var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
			opacity.Duration = TimeSpan.FromSeconds(1.6); opacity.IterationBehavior = AnimationIterationBehavior.Forever;
			opacity.InsertKeyFrame(0, 1); opacity.InsertKeyFrame(.5f, .5f); opacity.InsertKeyFrame(1, 1);
			var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
			scale.Duration = opacity.Duration; scale.IterationBehavior = AnimationIterationBehavior.Forever;
			scale.InsertKeyFrame(0, Vector3.One); scale.InsertKeyFrame(.5f, new Vector3(.86f, .86f, 1)); scale.InsertKeyFrame(1, Vector3.One);
			visual.StartAnimation("Opacity", opacity); visual.StartAnimation("Scale", scale);
		}
	}
}
