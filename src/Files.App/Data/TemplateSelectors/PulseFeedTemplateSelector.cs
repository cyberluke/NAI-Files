// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Services.Pulse;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Files.App.Data.TemplateSelectors
{
	/// <summary>
	/// Picks the Pulse feed template for an entry: a day header or one of the content-aware cards.
	/// </summary>
	internal sealed partial class PulseFeedTemplateSelector : DataTemplateSelector
	{
		public DataTemplate? HeaderTemplate { get; set; }
		public DataTemplate? ImageTemplate { get; set; }
		public DataTemplate? VideoTemplate { get; set; }
		public DataTemplate? AudioTemplate { get; set; }
		public DataTemplate? MarkdownTemplate { get; set; }
		public DataTemplate? DocumentTemplate { get; set; }
		public DataTemplate? GenericTemplate { get; set; }

		protected override DataTemplate SelectTemplateCore(object item)
		{
			var template = item switch
			{
				PulseSectionHeader => HeaderTemplate,
				PulseCardViewModel card => card.Kind switch
				{
					PulseCardKind.Image => ImageTemplate,
					PulseCardKind.Video => VideoTemplate,
					PulseCardKind.Audio => AudioTemplate,
					PulseCardKind.Markdown => MarkdownTemplate,
					PulseCardKind.Document => DocumentTemplate,
					// Archives use the compact utility card
					_ => GenericTemplate,
				},
				_ => GenericTemplate,
			};

			return template ?? GenericTemplate ?? throw new InvalidOperationException("The Pulse feed template selector has no templates.");
		}

		protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
			=> SelectTemplateCore(item);
	}
}
