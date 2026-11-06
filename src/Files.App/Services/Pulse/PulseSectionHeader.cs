// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Globalization;

namespace Files.App.Services.Pulse
{
	/// <summary>
	/// A day marker in the Pulse feed ("Today · 23.10.26 · 12 files"). Lives in the same
	/// flat source as the cards so one virtualizing layout handles both.
	/// </summary>
	public sealed partial class PulseSectionHeader : ObservableObject
	{
		/// <summary>Local calendar day the section covers.</summary>
		public DateTime Day { get; }

		/// <summary>Relative label when near today ("Today", "Yesterday"), otherwise the long date.</summary>
		public string Label { get; }

		/// <summary>Compact absolute date, always shown next to the label.</summary>
		public string DateText => Day.ToString("dd.MM.yy", CultureInfo.InvariantCulture);

		private int count;
		public int Count
		{
			get => count;
			set
			{
				if (SetProperty(ref count, value))
					OnPropertyChanged(nameof(CountText));
			}
		}

		public string CountText => $"{count} {Strings.Items.GetLocalizedFormatResource(count)}";

		public PulseSectionHeader(DateTime day, string label, int count)
		{
			Day = day;
			Label = label;
			this.count = count;
		}
	}
}
