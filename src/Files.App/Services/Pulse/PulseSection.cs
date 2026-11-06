// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Services.Pulse
{
	/// <summary>
	/// One day of the Pulse feed: its header plus the cards modified that day (newest first).
	/// Each section renders as its own flow gallery, so days never share a line.
	/// </summary>
	public sealed class PulseSection
	{
		public PulseSectionHeader Header { get; }

		public ObservableCollection<PulseCardViewModel> Cards { get; } = [];

		public DateTime Day => Header.Day;

		public PulseSection(PulseSectionHeader header)
		{
			Header = header;
		}

		/// <summary>Replaces the cards with the given sequence, touching the collection only where it differs.</summary>
		public void SetCards(IReadOnlyList<PulseCardViewModel> cards)
		{
			var same = cards.Count == Cards.Count;
			if (same)
			{
				for (int i = 0; i < cards.Count; i++)
				{
					if (!ReferenceEquals(cards[i], Cards[i]))
					{
						same = false;
						break;
					}
				}
			}
			if (same)
				return;

			Cards.Clear();
			foreach (var card in cards)
				Cards.Add(card);

			Header.Count = cards.Count;
		}
	}
}
