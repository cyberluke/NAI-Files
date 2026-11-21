// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Helpers;
using Files.App.Services.Pulse;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Globalization;
using System.IO;
using Path = System.IO.Path;
using Windows.Foundation;
using WinRT;

namespace Files.App.Dialogs
{
	/// <summary>
	/// Image editor for Pulse image cards, backed by the bundled FFmpeg 9 toolset: interactive crop
	/// (drag a box over the preview, aspect-ratio presets), 90°/180° rotation, resize (100/75/50/25 %)
	/// and format conversion (PNG/JPG/WebP/BMP/TIFF with quality). The result is written next to the
	/// source as "&lt;name&gt; (edited).&lt;ext&gt;", which the Pulse feed stacks as a new version.
	/// The crop overlay works in the displayed (rotated) frame, matching the filter chain that applies
	/// rotation before cropping.
	/// </summary>
	public sealed partial class PulseImageToolDialog : ContentDialog
	{
		private const double MaxPreviewHeight = 300;
		private const double MinCropPixels = 16;

		private readonly PulseCardViewModel card;
		private readonly string sourcePath;
		private readonly CancellationTokenSource lifetime = new();

		private PulseFfmpeg? ffmpeg;
		private bool isReady;
		private bool isApplying;
		private int sourceWidth;
		private int sourceHeight;
		private int quarterTurns;
		private double displayWidth;
		private double displayHeight;

		// Crop rectangle in display-pixel coordinates (the rotated frame the filter chain crops in)
		private Rect cropRect;
		private CropMode cropMode;
		private Point grabOffset;
		private Point anchorPixel;
		private readonly Rectangle[] dimRects = new Rectangle[4];

		private enum CropMode { None, Draw, Move, ResizeTL, ResizeTR, ResizeBL, ResizeBR }

		private FrameworkElement RootAppElement
		{
			[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
			get => (FrameworkElement)MainWindow.Instance.Content;
		}

		public PulseImageToolDialog(PulseCardViewModel card)
		{
			this.card = card;
			sourcePath = card.Item.GetRequiredPath();
			InitializeComponent();
			PulseDialogChrome.EnableDrag(this);

			FileNameText.Text = System.IO.Path.GetFileName(sourcePath);
			PrefillFormat(sourcePath);
			QualitySlider.ValueChanged += (_, e) => QualityText.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture);
			ImageHost.SizeChanged += (_, _) => RenderCrop();

			// Outside-dim rectangles are created in code to keep the XAML lean
			for (int i = 0; i < dimRects.Length; i++)
			{
				dimRects[i] = new Rectangle { Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0, 0, 0)), IsHitTestVisible = false };
				Canvas.SetZIndex(dimRects[i], 1);
				CropCanvas.Children.Add(dimRects[i]);
			}
		}

		public new async Task<DialogResult> ShowAsync()
		{
			Opened += Dialog_Opened;
			Closing += Dialog_Closing;
			return (DialogResult)await base.ShowAsync();
		}

		private async void Dialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
		{
			try
			{
				ffmpeg = await PulseFfmpeg.RequireAsync(lifetime.Token);
				if (lifetime.IsCancellationRequested) return;
				if (ffmpeg is null || await ffmpeg.ProbeImageSizeAsync(sourcePath, lifetime.Token) is not { } size)
				{
					StatusText.Text = Strings.PulseTrimUnsupported.GetLocalizedResource();
					IsPrimaryButtonEnabled = false;
					return;
				}

				sourceWidth = size.Width;
				sourceHeight = size.Height;
				displayWidth = sourceWidth;
				displayHeight = sourceHeight;
				cropRect = new Rect(0, 0, displayWidth, displayHeight);
				SizeText.Text = $"{sourceWidth} × {sourceHeight} px";

				PreviewImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(sourcePath));
				PreviewImage.Visibility = Visibility.Visible;
				LoadingText.Visibility = Visibility.Collapsed;
				LayoutPreview();
				RenderCrop();
				isReady = true;
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Pulse image editor initialization failed");
				StatusText.Text = Strings.PulseTrimUnsupported.GetLocalizedResource();
				IsPrimaryButtonEnabled = false;
			}
		}

		/// <summary>Sizes the preview host to the rotated aspect ratio so the crop canvas maps 1:1 to pixels.</summary>
		private void LayoutPreview()
		{
			var rotatedAspect = displayWidth / displayHeight;
			var maxWidth = Math.Max(240, Math.Min(620, ActualWidth > 0 ? ActualWidth - 48 : 560));

			double hostWidth = maxWidth;
			double hostHeight = hostWidth / rotatedAspect;
			if (hostHeight > MaxPreviewHeight)
			{
				hostHeight = MaxPreviewHeight;
				hostWidth = hostHeight * rotatedAspect;
			}

			ImageHost.Width = hostWidth;
			ImageHost.Height = hostHeight;
			PreviewImage.RenderTransform = new RotateTransform
			{
				Angle = quarterTurns * 90,
				CenterX = hostWidth / 2,
				CenterY = hostHeight / 2,
			};
		}

		// ---- rotation / reset / aspect -------------------------------------------

		private void RotateLeft_Click(object sender, RoutedEventArgs e) => Rotate(3);

		private void RotateRight_Click(object sender, RoutedEventArgs e) => Rotate(1);

		private void Rotate180_Click(object sender, RoutedEventArgs e) => Rotate(2);

		private void Rotate(int steps)
		{
			if (!isReady)
				return;

			quarterTurns = (quarterTurns + steps) % 4;
			// The crop box was drawn in the previous orientation: reset it to the full rotated frame
			if (quarterTurns % 2 == 1)
				(displayWidth, displayHeight) = (displayHeight, displayWidth);
			cropRect = new Rect(0, 0, displayWidth, displayHeight);
			LayoutPreview();
			RenderCrop();
		}

		private void ResetCrop_Click(object sender, RoutedEventArgs e)
		{
			if (!isReady)
				return;

			cropRect = new Rect(0, 0, displayWidth, displayHeight);
			RenderCrop();
		}

		private void AspectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
			=> RenderCrop();

		private double? CurrentAspectLock => AspectComboBox.SelectedIndex switch
		{
			1 => 1.0,          // 1:1
			2 => 4.0 / 3.0,    // 4:3
			3 => 16.0 / 9.0,   // 16:9
			_ => null,          // Free
		};

		// ---- crop interaction ------------------------------------------------------

		private void ImageHost_PointerPressed(object sender, PointerRoutedEventArgs e)
		{
			if (!isReady)
				return;

			var (hostX, hostY) = GetHostPosition(e);
			var (px, py) = HostToPixel(hostX, hostY);
			var handle = HitTestHandle(hostX, hostY);

			if (handle is not null)
			{
				cropMode = handle.Value;
				anchorPixel = OppositeCorner(handle.Value);
			}
			else if (px >= cropRect.X && px <= cropRect.Right && py >= cropRect.Y && py <= cropRect.Bottom)
			{
				cropMode = CropMode.Move;
				grabOffset = new Point(px - cropRect.X, py - cropRect.Y);
			}
			else
			{
				cropMode = CropMode.Draw;
				anchorPixel = new Point(px, py);
				cropRect = new Rect(px, py, 0, 0);
			}

			ImageHost.CapturePointer(e.Pointer);
			RenderCrop();
		}

		private void ImageHost_PointerMoved(object sender, PointerRoutedEventArgs e)
		{
			if (cropMode is CropMode.None)
				return;

			var (hostX, hostY) = GetHostPosition(e);
			var (px, py) = HostToPixel(hostX, hostY);
			var lockRatio = CurrentAspectLock;

			switch (cropMode)
			{
				case CropMode.Draw:
					cropRect = MakeRect(anchorPixel, new Point(px, py), lockRatio);
					break;
				case CropMode.Move:
					cropRect.X = Math.Clamp(px - grabOffset.X, 0, displayWidth - cropRect.Width);
					cropRect.Y = Math.Clamp(py - grabOffset.Y, 0, displayHeight - cropRect.Height);
					break;
				case CropMode.ResizeTL:
				case CropMode.ResizeTR:
				case CropMode.ResizeBL:
				case CropMode.ResizeBR:
					cropRect = ResizeFrom(cropMode, new Point(px, py), lockRatio);
					break;
			}

			RenderCrop();
		}

		private void ImageHost_PointerReleased(object sender, PointerRoutedEventArgs e)
		{
			cropMode = CropMode.None;
			ImageHost.ReleasePointerCapture(e.Pointer);
		}

		private (double X, double Y) GetHostPosition(PointerRoutedEventArgs e)
		{
			var point = e.GetCurrentPoint(ImageHost).Position;
			return (point.X, point.Y);
		}

		private (double X, double Y) HostToPixel(double hostX, double hostY)
		{
			var width = Math.Max(1, ImageHost.ActualWidth);
			var height = Math.Max(1, ImageHost.ActualHeight);
			return (Math.Clamp(hostX / width * displayWidth, 0, displayWidth), Math.Clamp(hostY / height * displayHeight, 0, displayHeight));
		}

		private CropMode? HitTestHandle(double hostX, double hostY)
		{
			const double radius = 14;
			var corners = new (CropMode Mode, Point Pixel)[]
			{
				(CropMode.ResizeTL, new Point(cropRect.X, cropRect.Y)),
				(CropMode.ResizeTR, new Point(cropRect.Right, cropRect.Y)),
				(CropMode.ResizeBL, new Point(cropRect.X, cropRect.Bottom)),
				(CropMode.ResizeBR, new Point(cropRect.Right, cropRect.Bottom)),
			};

			foreach (var (mode, pixel) in corners)
			{
				var (cx, cy) = PixelToHost(pixel.X, pixel.Y);
				if (Math.Abs(hostX - cx) <= radius && Math.Abs(hostY - cy) <= radius)
					return mode;
			}
			return null;
		}

		private (double X, double Y) PixelToHost(double px, double py)
		{
			var width = Math.Max(1, ImageHost.ActualWidth);
			var height = Math.Max(1, ImageHost.ActualHeight);
			return (px / displayWidth * width, py / displayHeight * height);
		}

		private Point OppositeCorner(CropMode handle) => handle switch
		{
			CropMode.ResizeTL => new Point(cropRect.Right, cropRect.Bottom),
			CropMode.ResizeTR => new Point(cropRect.X, cropRect.Bottom),
			CropMode.ResizeBL => new Point(cropRect.Right, cropRect.Y),
			_ => new Point(cropRect.X, cropRect.Y),
		};

		private Rect ResizeFrom(CropMode handle, Point current, double? lockRatio)
		{
			var opposite = OppositeCorner(handle);
			var isLeft = handle is CropMode.ResizeTL or CropMode.ResizeBL;
			var isTop = handle is CropMode.ResizeTL or CropMode.ResizeTR;

			var x = isLeft ? Math.Min(current.X, opposite.X) : opposite.X;
			var y = isTop ? Math.Min(current.Y, opposite.Y) : opposite.Y;
			var width = Math.Abs(current.X - opposite.X);
			var height = Math.Abs(current.Y - opposite.Y);

			if (lockRatio is { } ratio)
			{
				if (width / Math.Max(1, height) > ratio)
					height = width / ratio;
				else
					width = height * ratio;
				// Re-anchor so the opposite corner stays fixed
				x = isLeft ? opposite.X - width : opposite.X;
				y = isTop ? opposite.Y - height : opposite.Y;
			}

			return new Rect(
				Math.Clamp(x, 0, displayWidth - MinCropPixels),
				Math.Clamp(y, 0, displayHeight - MinCropPixels),
				Math.Clamp(width, MinCropPixels, displayWidth),
				Math.Clamp(height, MinCropPixels, displayHeight));
		}

		private Rect MakeRect(Point a, Point b, double? lockRatio)
		{
			var x = Math.Min(a.X, b.X);
			var y = Math.Min(a.Y, b.Y);
			var width = Math.Abs(b.X - a.X);
			var height = Math.Abs(b.Y - a.Y);

			if (lockRatio is { } ratio)
			{
				if (width / Math.Max(1, height) > ratio)
					height = width / ratio;
				else
					width = height * ratio;
				// Keep the anchor corner fixed: the anchor is the press origin
				x = a.X <= b.X ? a.X : a.X - width;
				y = a.Y <= b.Y ? a.Y : a.Y - height;
			}

			return new Rect(
				Math.Clamp(x, 0, displayWidth - MinCropPixels),
				Math.Clamp(y, 0, displayHeight - MinCropPixels),
				Math.Clamp(width, MinCropPixels, displayWidth),
				Math.Clamp(height, MinCropPixels, displayHeight));
		}

		private void RenderCrop()
		{
			if (!isReady || ImageHost.ActualWidth <= 0)
				return;

			var (hostX, hostY) = PixelToHost(cropRect.X, cropRect.Y);
			var hostW = cropRect.Width / displayWidth * ImageHost.ActualWidth;
			var hostH = cropRect.Height / displayHeight * ImageHost.ActualHeight;

			// Dim the outside with four rectangles around the crop box
			var w = ImageHost.ActualWidth;
			var h = ImageHost.ActualHeight;
			PlaceDim(dimRects[0], 0, 0, w, hostY);
			PlaceDim(dimRects[1], 0, hostY + hostH, w, Math.Max(0, h - hostY - hostH));
			PlaceDim(dimRects[2], 0, hostY, hostX, hostH);
			PlaceDim(dimRects[3], hostX + hostW, hostY, Math.Max(0, w - hostX - hostW), hostH);

			Canvas.SetLeft(CropRectVisual, hostX);
			Canvas.SetTop(CropRectVisual, hostY);
			CropRectVisual.Width = hostW;
			CropRectVisual.Height = hostH;

			PlaceHandle(HandleTL, hostX - 6, hostY - 6);
			PlaceHandle(HandleTR, hostX + hostW - 6, hostY - 6);
			PlaceHandle(HandleBL, hostX - 6, hostY + hostH - 6);
			PlaceHandle(HandleBR, hostX + hostW - 6, hostY + hostH - 6);

			CropSizeText.Text = $"{cropRect.Width:0} × {cropRect.Height:0} px";
		}

		private static void PlaceDim(Rectangle rect, double x, double y, double width, double height)
		{
			Canvas.SetLeft(rect, x);
			Canvas.SetTop(rect, y);
			rect.Width = Math.Max(0, width);
			rect.Height = Math.Max(0, height);
		}

		private static void PlaceHandle(Rectangle handle, double x, double y)
		{
			Canvas.SetLeft(handle, x);
			Canvas.SetTop(handle, y);
		}

		// ---- apply ---------------------------------------------------------------

		/// <summary>Selects the format that matches the source extension so Apply never silently converts.</summary>
		private void PrefillFormat(string path)
		{
			FormatComboBox.SelectedIndex = System.IO.Path.GetExtension(path).ToLowerInvariant() switch
			{
				".png" => 0,
				".jpg" or ".jpeg" => 1,
				".webp" => 2,
				".bmp" => 3,
				".tif" or ".tiff" => 4,
				_ => 0,
			};
		}

		private void FormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			var show = CurrentFormat is PulseImageFormat.Jpg or PulseImageFormat.Webp;
			QualityRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
			if (show)
				QualityText.Text = ((int)QualitySlider.Value).ToString(CultureInfo.InvariantCulture);
		}

		private PulseImageFormat CurrentFormat => (PulseImageFormat)Math.Clamp(FormatComboBox.SelectedIndex, 0, 4);

		private async void PrimaryButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
		{
			if (isApplying || ffmpeg is null || !isReady)
			{
				args.Cancel = true;
				return;
			}

			var deferral = args.GetDeferral();
			isApplying = true;
			IsPrimaryButtonEnabled = false;
			AspectComboBox.IsEnabled = false;
			ResizeComboBox.IsEnabled = false;
			FormatComboBox.IsEnabled = false;
			QualitySlider.IsEnabled = false;
			ProgressRing.IsActive = true;
			StatusText.Text = Strings.PulseImageApplying.GetLocalizedResource();

			try
			{
				var format = CurrentFormat;
				var quality = PulseImageFormatSpec.MapQuality(format, (int)QualitySlider.Value);
				var scalePercent = new[] { 1.0, 0.75, 0.5, 0.25 }[Math.Clamp(ResizeComboBox.SelectedIndex, 0, 3)];

				// A full-frame box means "no crop"
				Rect? crop = null;
				if (cropRect.Width < displayWidth - 1 || cropRect.Height < displayHeight - 1)
				{
					crop = new Rect(
						cropRect.X / displayWidth,
						cropRect.Y / displayHeight,
						cropRect.Width / displayWidth,
						cropRect.Height / displayHeight);
				}

				var ops = new PulseImageOps(quarterTurns, crop, scalePercent);
				var outputPath = ResolveOutputPath(PulseImageFormatSpec.For(format).Extension);
				var succeeded = await ffmpeg.EditImageAsync(sourcePath, ops, format, quality, outputPath, lifetime.Token);

				if (succeeded)
					Hide();
				else
				{
					ProgressRing.IsActive = false;
					StatusText.Text = Strings.PulseTrimExportFailed.GetLocalizedResource();
				}
			}
			catch (OperationCanceledException)
			{
				// Dialog closed mid-apply
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Pulse image edit failed");
				ProgressRing.IsActive = false;
				StatusText.Text = Strings.PulseTrimExportFailed.GetLocalizedResource();
			}
			finally
			{
				isApplying = false;
				IsPrimaryButtonEnabled = true;
				AspectComboBox.IsEnabled = true;
				ResizeComboBox.IsEnabled = true;
				FormatComboBox.IsEnabled = true;
				QualitySlider.IsEnabled = true;
				deferral.Complete();
			}
		}

		/// <summary>"photo.png" -> "photo (edited).png" next to the source; "(2)", "(3)" … on collision.</summary>
		private string ResolveOutputPath(string extension)
		{
			var directory = System.IO.Path.GetDirectoryName(sourcePath) ?? string.Empty;
			var stem = System.IO.Path.GetFileNameWithoutExtension(sourcePath);

			var candidate = System.IO.Path.Combine(directory, $"{stem} (edited).{extension}");
			var counter = 2;
			while (File.Exists(candidate))
				candidate = System.IO.Path.Combine(directory, $"{stem} (edited) ({counter++}).{extension}");

			return candidate;
		}

		private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
			=> lifetime.Cancel();
	}
}
