// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.WinUI;
using Files.App.Services.Pulse;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Files.App.UserControls.Pulse
{
	/// <summary>
	/// One day of the Pulse feed: header plus a <see cref="LinedFlowLayout"/> gallery of its cards.
	/// Selection and invocation are forwarded to the layout page, which owns the real selection.
	/// </summary>
	public sealed partial class PulseSectionView : UserControl
	{
		private PulseSection? section;
		private bool isSyncingSelection;
		private Microsoft.UI.Dispatching.DispatcherQueueTimer? invalidateTimer;

		public static readonly DependencyProperty SectionProperty =
			DependencyProperty.Register(nameof(Section), typeof(PulseSection), typeof(PulseSectionView), new PropertyMetadata(null, OnSectionChanged));

		public static readonly DependencyProperty CardTemplateProperty =
			DependencyProperty.Register(nameof(CardTemplate), typeof(object), typeof(PulseSectionView), new PropertyMetadata(null, OnCardTemplateChanged));

		public static readonly DependencyProperty LineHeightProperty =
			DependencyProperty.Register(nameof(LineHeight), typeof(double), typeof(PulseSectionView), new PropertyMetadata(184d, OnLineHeightChanged));

		public PulseSection? Section
		{
			get => (PulseSection?)GetValue(SectionProperty);
			set => SetValue(SectionProperty, value);
		}

		/// <summary>A <see cref="DataTemplate"/> or <see cref="DataTemplateSelector"/> producing the card containers.</summary>
		public object? CardTemplate
		{
			get => GetValue(CardTemplateProperty);
			set => SetValue(CardTemplateProperty, value);
		}

		public double LineHeight
		{
			get => (double)GetValue(LineHeightProperty);
			set => SetValue(LineHeightProperty, value);
		}

		/// <summary>The page-level selection changed inside this section.</summary>
		public event EventHandler? SelectionChanged;

		public ItemsView ItemsView => Cards;

		public PulseSectionView()
		{
			InitializeComponent();
		}

		private static void OnSectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
			=> ((PulseSectionView)d).ApplySection(e.NewValue as PulseSection);

		private static void OnCardTemplateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
			=> ((PulseSectionView)d).Cards.ItemTemplate = e.NewValue as IElementFactory;

		private static void OnLineHeightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
			=> ((PulseSectionView)d).FlowLayout.LineHeight = (double)e.NewValue;

		private void ApplySection(PulseSection? newSection)
		{
			if (section is not null)
			{
				section.Header.PropertyChanged -= Header_PropertyChanged;
				section.Cards.CollectionChanged -= Cards_CollectionChanged;
			}

			section = newSection;
			FlowLayout.LineHeight = LineHeight;

			if (section is null)
			{
				Cards.ItemsSource = null;
				return;
			}

			section.Header.PropertyChanged += Header_PropertyChanged;
			section.Cards.CollectionChanged += Cards_CollectionChanged;
			LabelText.Text = section.Header.Label;
			DateText.Text = section.Header.DateText;
			CountText.Text = section.Header.CountText;
			Cards.ItemsSource = section.Cards;
		}

		private void Header_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (section is not null)
				CountText.Text = section.Header.CountText;
		}

		private void Cards_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
			=> QueueInvalidate();

		private void Cards_SizeChanged(object sender, SizeChangedEventArgs e)
		{
			// Keep the row packing in sync with the real width
			if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
				QueueInvalidate();
		}

		/// <summary>Re-asks the layout for aspect ratios (coalesced); called when thumbnails land.</summary>
		public void QueueInvalidate()
		{
			if (invalidateTimer is null)
			{
				invalidateTimer = DispatcherQueue.CreateTimer();
				invalidateTimer.Interval = TimeSpan.FromMilliseconds(150);
				invalidateTimer.IsRepeating = false;
				invalidateTimer.Tick += (_, _) => FlowLayout.InvalidateItemsInfo();
			}

			if (!invalidateTimer.IsRunning)
				invalidateTimer.Start();
		}

		// Below this ratio the footer only fits the timestamp and the type badge
		private const double SizeVisibleMinRatio = 1.2;

		// Must match the PulseFooterHeight resource: the footer below a preview takes this much of the line
		private const double FooterHeight = 52;

		// How far a card may be scaled from its desired width when the layout fills a line. LinedFlowLayout picks a
		// distribution that balances line widths, then scales each line to exactly the available width: pictures crop
		// gracefully (UniformToFill) and may move more, text-driven cards only a little so their footers stay readable.
		private const double PictureStretchTolerance = 0.30;
		private const double CardStretchTolerance = 0.12;

		private void FlowLayout_ItemsInfoRequested(LinedFlowLayout sender, LinedFlowLayoutItemsInfoRequestedEventArgs args)
		{
			if (section is null || section.Cards.Count == 0)
				return;

			// Sizing info for the whole day so the layout never estimates from averages
			var count = section.Cards.Count;
			var ratios = new double[count];
			var minWidths = new double[count];
			var maxWidths = new double[count];
			var lineHeight = LineHeight;

			for (int i = 0; i < count; i++)
			{
				var card = section.Cards[i];
				var ratio = GetAspectRatio(card);
				ratios[i] = ratio;
				card.ShowsSize = ratio >= SizeVisibleMinRatio;

				var tolerance = card.IsEdgeToEdge && card.ImageAspectRatio is not null ? PictureStretchTolerance : CardStretchTolerance;
				var minRatio = ratio * (1 - tolerance);
				var maxRatio = ratio * (1 + tolerance);

				// A portrait box must stay portrait even when its line is expanded to fill the width
				if (card.IsPortrait)
					maxRatio = Math.Min(maxRatio, 0.9);

				minWidths[i] = Math.Floor(minRatio * lineHeight);
				maxWidths[i] = Math.Ceiling(maxRatio * lineHeight);
			}

			args.ItemsRangeStartIndex = 0;
			args.SetDesiredAspectRatios(ratios);
			args.SetMinWidths(minWidths);
			args.SetMaxWidths(maxWidths);
		}

		/// <summary>
		/// Card aspect ratio (width / line height) for one card.
		/// Portrait photo: the box has the picture's own ratio and the picture fills the whole card height
		/// (its footer is overlaid), so a 9:16 shot is shown uncropped as a tall box.
		/// Other previews: the preview area above the footer gets exactly the thumbnail's ratio, so a 16:9 video
		/// frame or landscape photo is shown uncropped as a wide box. Clamps keep the footer legible.
		/// </summary>
		private double GetAspectRatio(PulseCardViewModel card)
		{
			var previewShare = Math.Max(0.1, (LineHeight - FooterHeight) / LineHeight);

			if (card.IsEdgeToEdge && card.ImageAspectRatio is { } picture)
			{
				if (card.IsPortrait)
					return Math.Clamp(picture, 0.5, 0.85);

				return Math.Clamp(picture * previewShare, 0.92, 1.9);
			}

			return card.Kind switch
			{
				PulseCardKind.Video => 1.5,
				PulseCardKind.Markdown => 1.15,
				// Audio: a wide strip reads like a deck waveform
				PulseCardKind.Audio => 1.7,
				PulseCardKind.Document => 0.92,
				_ => 1.0,
			};
		}

		// Selection: the page owns the selection (hidden list); this gallery only mirrors it
		// through the card view models. ItemsView's own selection machinery is deliberately not
		// used (SelectionMode=None): the cards are custom ItemContainer templates where the
		// built-in click/ctrl/shift selection is unreliable, and the page drives every gesture.

		/// <summary>Mirrors the page selection into this gallery without raising <see cref="SelectionChanged"/>.</summary>
		public void SyncSelection(HashSet<ListedItem> selectedItems)
		{
			if (section is null)
				return;

			isSyncingSelection = true;
			try
			{
				for (int i = 0; i < section.Cards.Count; i++)
					section.Cards[i].IsSelected = selectedItems.Contains(section.Cards[i].Item);
			}
			finally
			{
				isSyncingSelection = false;
			}
		}

		public IEnumerable<PulseCardViewModel> SelectedCards
			=> Cards.SelectedItems.OfType<PulseCardViewModel>();

		public int IndexOf(ListedItem item)
		{
			if (section is null)
				return -1;

			for (int i = 0; i < section.Cards.Count; i++)
			{
				if (ReferenceEquals(section.Cards[i].Item, item))
					return i;
			}
			return -1;
		}

		/// <summary>Selects exactly the given card (used by click-to-select on the card root).</summary>
		public void SelectOnly(PulseCardViewModel card)
		{
			var current = section;
			if (current is null)
				return;

			var index = current.Cards.IndexOf(card);
			if (index < 0)
				return;

			isSyncingSelection = true;
			try
			{
				for (int i = 0; i < current.Cards.Count; i++)
					current.Cards[i].IsSelected = i == index;
			}
			finally
			{
				isSyncingSelection = false;
			}
		}

		/// <summary>Finds the realized container of a card, if any.</summary>
		[WinRT.DynamicWindowsRuntimeCast(typeof(ItemContainer))]
		[WinRT.DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		public ItemContainer? FindContainer(PulseCardViewModel card)
		{
			foreach (var container in Cards.FindDescendants().OfType<ItemContainer>())
			{
				if ((container.Child as FrameworkElement)?.DataContext is PulseCardViewModel bound && ReferenceEquals(bound, card))
					return container;
			}
			return null;
		}
	}
}
