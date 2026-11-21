// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Numerics;
using System.Runtime.CompilerServices;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace Files.App.Dialogs
{
	/// <summary>
	/// Shared chrome for the Pulse action dialogs: lets the user move a <see cref="ContentDialog"/>
	/// by dragging its title strip, and resets the offset each time it opens. Overflow is handled
	/// per dialog with a ScrollViewer; this only adds the drag behaviour.
	/// </summary>
	internal static class PulseDialogChrome
	{
		private const double DragZoneHeight = 64;

		private sealed class DragState
		{
			public bool IsDragging;
			public Point Start;
			public Vector3 Origin;
		}

		private static readonly ConditionalWeakTable<ContentDialog, DragState> states = new();

		internal static void EnableDrag(ContentDialog dialog)
		{
			var state = states.GetOrCreateValue(dialog);

			dialog.PointerPressed += (_, e) => OnPressed(dialog, state, e);
			dialog.PointerMoved += (_, e) => OnMoved(dialog, state, e);
			dialog.PointerReleased += (_, e) => OnReleased(dialog, state, e);
			dialog.PointerCanceled += (_, e) => OnReleased(dialog, state, e);
			dialog.Opened += (_, _) => dialog.Translation = Vector3.Zero;
		}

		private static void OnPressed(ContentDialog dialog, DragState state, PointerRoutedEventArgs e)
		{
			var point = e.GetCurrentPoint(dialog);
			if (!point.Properties.IsLeftButtonPressed || point.Position.Y > DragZoneHeight)
				return;

			// Buttons/inputs inside the title area keep their own gestures.
			if ((e.OriginalSource as DependencyObject)?.FindAscendantOrSelf<ButtonBase>() is not null)
				return;

			state.IsDragging = true;
			state.Start = e.GetCurrentPoint(null).Position;
			state.Origin = dialog.Translation;
			dialog.CapturePointer(e.Pointer);
			e.Handled = true;
		}

		private static void OnMoved(ContentDialog dialog, DragState state, PointerRoutedEventArgs e)
		{
			if (!state.IsDragging)
				return;

			var now = e.GetCurrentPoint(null).Position;
			dialog.Translation = new Vector3(
				state.Origin.X + (float)(now.X - state.Start.X),
				state.Origin.Y + (float)(now.Y - state.Start.Y),
				0);
			e.Handled = true;
		}

		private static void OnReleased(ContentDialog dialog, DragState state, PointerRoutedEventArgs e)
		{
			if (!state.IsDragging)
				return;

			state.IsDragging = false;
			dialog.ReleasePointerCaptures();
			e.Handled = true;
		}
	}
}
