// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Files.App.Converters
{
	/// <summary>True → Top, false → Bottom. Used to move the portrait media tools above the play glyph.</summary>
	public sealed class BoolToVerticalAlignmentConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language)
			=> value is true ? VerticalAlignment.Top : VerticalAlignment.Bottom;

		public object ConvertBack(object value, Type targetType, object parameter, string language)
			=> throw new NotSupportedException();
	}
}
