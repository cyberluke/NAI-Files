// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Services.DateTimeFormatter;
using Microsoft.UI.Dispatching;
using System.Collections.Specialized;

namespace Files.App.Services.Pulse
{
	/// <summary>
	/// Projects the shell view model's <c>FilesAndFolders</c> into the Pulse feed: one
	/// <see cref="PulseSection"/> per day, newest first, with version stacks collapsed to their
	/// latest member. Rebuilds are debounced (enumeration fires many change events) and card
	/// view models are reused per <see cref="ListedItem"/> so container state survives.
	/// </summary>
	public sealed class PulseFeedBuilder : IDisposable
	{
		private static readonly TimeSpan RebuildDelay = TimeSpan.FromMilliseconds(80);

		private readonly IDateTimeFormatter dateTimeFormatter = Ioc.Default.GetRequiredService<IDateTimeFormatter>();
		private readonly DispatcherQueue dispatcherQueue;
		private readonly DispatcherQueueTimer rebuildTimer;
		private readonly Dictionary<ListedItem, PulseCardViewModel> cards = new(ReferenceEqualityComparer.Instance);
		private readonly Dictionary<DateTime, PulseSection> sectionsByDay = [];
		private readonly HashSet<string> expandedStacks = new(StringComparer.OrdinalIgnoreCase);

		private BulkConcurrentObservableCollection<ListedItem>? source;
		private PulseFilter filter = PulseFilter.All;
		private bool isDisposed;

		/// <summary>Day sections in feed order (newest first).</summary>
		public ObservableCollection<PulseSection> Feed { get; } = [];

		/// <summary>Day headers in feed order; drives the time rail.</summary>
		public IReadOnlyList<PulseSectionHeader> Sections => sections;
		private readonly List<PulseSectionHeader> sections = [];

		/// <summary>Raised after the feed was rebuilt.</summary>
		public event EventHandler? FeedRebuilt;

		public PulseFilter Filter
		{
			get => filter;
			set
			{
				if (filter == value)
					return;
				filter = value;
				Rebuild();
			}
		}

		private bool autoGroupVersions = true;
		/// <summary>Whether version/sibling files are collapsed into stacks; off shows every file as its own card.</summary>
		public bool AutoGroupVersions
		{
			get => autoGroupVersions;
			set
			{
				if (autoGroupVersions == value)
					return;
				autoGroupVersions = value;
				Rebuild();
			}
		}

		public PulseFeedBuilder(DispatcherQueue dispatcherQueue)
		{
			this.dispatcherQueue = dispatcherQueue;
			rebuildTimer = dispatcherQueue.CreateTimer();
			rebuildTimer.Interval = RebuildDelay;
			rebuildTimer.IsRepeating = false;
			rebuildTimer.Tick += (_, _) => Rebuild();
		}

		public void SetSource(BulkConcurrentObservableCollection<ListedItem>? newSource)
		{
			if (ReferenceEquals(source, newSource))
				return;

			if (source is not null)
				source.CollectionChanged -= Source_CollectionChanged;

			source = newSource;

			if (source is not null)
				source.CollectionChanged += Source_CollectionChanged;

			Rebuild();
		}

		public PulseCardViewModel GetCard(ListedItem item)
		{
			if (!cards.TryGetValue(item, out var card))
				cards[item] = card = new PulseCardViewModel(item);
			return card;
		}

		public bool TryGetCard(ListedItem item, out PulseCardViewModel card)
			=> cards.TryGetValue(item, out card!);

		/// <summary>All cards currently shown, in feed order.</summary>
		public IEnumerable<PulseCardViewModel> VisibleCards
			=> Feed.SelectMany(section => section.Cards);

		/// <summary>Opens or closes a version stack inline; members then appear as individual cards.</summary>
		public void ToggleStack(PulseCardViewModel card)
		{
			if (card.Stack is null)
				return;

			if (!expandedStacks.Remove(card.Stack.Key))
				expandedStacks.Add(card.Stack.Key);

			Rebuild();
		}

		private void Source_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
		{
			if (isDisposed)
				return;

			// Collection events may arrive from the enumeration thread
			if (!dispatcherQueue.HasThreadAccess)
			{
				dispatcherQueue.TryEnqueue(ScheduleRebuild);
				return;
			}

			ScheduleRebuild();
		}

		private void ScheduleRebuild()
		{
			if (isDisposed)
				return;

			rebuildTimer.Stop();
			rebuildTimer.Start();
		}

		public void Rebuild()
		{
			if (isDisposed)
				return;

			rebuildTimer.Stop();

			var items = source?.ToList() ?? [];

			// Newest first: the feed is a timeline of what was touched last
			items.Sort((a, b) =>
			{
				var byDate = b.ItemDateModifiedReal.CompareTo(a.ItemDateModifiedReal);
				return byDate != 0 ? byDate : string.Compare(a.ItemNameRaw, b.ItemNameRaw, StringComparison.OrdinalIgnoreCase);
			});

			var stackByLatest = new Dictionary<ListedItem, FileStack>(ReferenceEqualityComparer.Instance);
			var hiddenMembers = new HashSet<ListedItem>(ReferenceEqualityComparer.Instance);
			if (autoGroupVersions)
			{
				foreach (var stack in FileStackService.BuildStacks(items))
				{
					stackByLatest[stack.Latest] = stack;
					if (expandedStacks.Contains(stack.Key))
						continue;
					for (int i = 1; i < stack.Members.Count; i++)
						hiddenMembers.Add(stack.Members[i]);
				}
			}

			var live = new HashSet<ListedItem>(ReferenceEqualityComparer.Instance);
			var days = new List<(DateTime Day, DateTimeOffset First, List<PulseCardViewModel> Cards)>();
			List<PulseCardViewModel>? currentCards = null;
			DateTime currentDay = default;

			foreach (var item in items)
			{
				live.Add(item);

				if (hiddenMembers.Contains(item) || !filter.Matches(item))
					continue;

				var day = item.ItemDateModifiedReal.ToLocalTime().Date;
				if (currentCards is null || currentDay != day)
				{
					currentDay = day;
					currentCards = [];
					days.Add((day, item.ItemDateModifiedReal, currentCards));
				}

				var card = GetCard(item);
				if (stackByLatest.TryGetValue(item, out var stack))
				{
					card.Stack = stack;
					card.IsStackExpanded = expandedStacks.Contains(stack.Key);
				}
				else
				{
					card.Stack = null;
					card.IsStackExpanded = false;
				}
				currentCards.Add(card);
			}

			// Drop cards for items no longer listed
			foreach (var (item, card) in cards.ToList())
			{
				if (!live.Contains(item))
				{
					card.Detach();
					cards.Remove(item);
				}
			}

			// Sections are reused per day so their galleries keep realized containers and scroll state
			var newFeed = new List<PulseSection>(days.Count);
			sections.Clear();
			foreach (var (day, first, dayCards) in days)
			{
				if (!sectionsByDay.TryGetValue(day, out var section))
				{
					var label = dateTimeFormatter.ToTimeSpanLabel(first, GroupByDateUnit.Day).Text;
					section = new PulseSection(new PulseSectionHeader(day, label, dayCards.Count));
					sectionsByDay[day] = section;
				}
				section.SetCards(dayCards);
				newFeed.Add(section);
				sections.Add(section.Header);
			}

			foreach (var day in sectionsByDay.Keys.Except(days.Select(d => d.Day)).ToList())
				sectionsByDay.Remove(day);

			ReplaceFeed(newFeed);
			FeedRebuilt?.Invoke(this, EventArgs.Empty);
		}

		private void ReplaceFeed(List<PulseSection> newFeed)
		{
			if (newFeed.Count == Feed.Count)
			{
				var same = true;
				for (int i = 0; i < newFeed.Count; i++)
				{
					if (!ReferenceEquals(newFeed[i], Feed[i]))
					{
						same = false;
						break;
					}
				}
				if (same)
					return;
			}

			Feed.Clear();
			foreach (var section in newFeed)
				Feed.Add(section);
		}

		public void Dispose()
		{
			if (isDisposed)
				return;
			isDisposed = true;

			rebuildTimer.Stop();
			if (source is not null)
				source.CollectionChanged -= Source_CollectionChanged;
			source = null;

			foreach (var card in cards.Values)
				card.Detach();
			cards.Clear();
			sectionsByDay.Clear();
			Feed.Clear();
			sections.Clear();
		}
	}
}
