// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Dispatching;

namespace Files.App.Services.Pulse
{
	/// <summary>
	/// Asynchronous batch queue that turns audio cards into spectral waveforms. Requests are processed
	/// LIFO (the card that scrolled into view last is painted first), decoding runs on a bounded number of
	/// workers so a folder of 500 tracks never saturates the machine, and results land on the UI thread as
	/// a ready-to-bind bitmap. Cards that scroll away cancel their pending request.
	/// </summary>
	public sealed class PulseWaveformService : IDisposable
	{
		private const int RenderWidth = 512;
		private const int RenderHeight = 128;

		private static readonly int MaxWorkers = Math.Clamp(Environment.ProcessorCount / 2, 1, 3);

		private readonly DispatcherQueue dispatcherQueue;
		private readonly object sync = new();
		private readonly LinkedList<PulseCardViewModel> queue = new();
		private readonly Dictionary<PulseCardViewModel, LinkedListNode<PulseCardViewModel>> nodes = new(ReferenceEqualityComparer.Instance);
		private readonly HashSet<PulseCardViewModel> active = new(ReferenceEqualityComparer.Instance);
		private readonly CancellationTokenSource lifetime = new();
		private int runningWorkers;

		public PulseWaveformService(DispatcherQueue dispatcherQueue)
		{
			this.dispatcherQueue = dispatcherQueue;
		}

		/// <summary>Asks for the waveform of an audio card; no-op when it is already available or in flight.</summary>
		public void Request(PulseCardViewModel card)
		{
			if (!card.IsAudio || card.Waveform is not null || card.WaveformUnavailable)
				return;

			lock (sync)
			{
				if (lifetime.IsCancellationRequested || active.Contains(card))
					return;

				// Re-requesting bumps the card to the front: it is the one on screen now
				if (nodes.TryGetValue(card, out var existing))
					queue.Remove(existing);

				nodes[card] = queue.AddFirst(card);

				if (runningWorkers < MaxWorkers)
				{
					runningWorkers++;
					_ = Task.Run(WorkerAsync);
				}
			}
		}

		/// <summary>Drops a pending request (the card scrolled away); an in-flight analysis finishes and is cached.</summary>
		public void Cancel(PulseCardViewModel card)
		{
			lock (sync)
			{
				if (nodes.Remove(card, out var node))
					queue.Remove(node);
			}
		}

		private async Task WorkerAsync()
		{
			while (true)
			{
				PulseCardViewModel card;
				lock (sync)
				{
					if (lifetime.IsCancellationRequested || queue.First is null)
					{
						runningWorkers--;
						return;
					}

					card = queue.First.Value;
					queue.RemoveFirst();
					nodes.Remove(card);
					active.Add(card);
				}

				PulseWaveform? waveform = null;
				byte[]? png = null;
				try
				{
					waveform = await PulseWaveformAnalyzer.GetOrAnalyzeAsync(card.Item.GetRequiredPath(), lifetime.Token);
					if (waveform is not null)
						png = PulseWaveformRenderer.RenderPng(waveform, RenderWidth, RenderHeight);
				}
				catch (OperationCanceledException)
				{
					return;
				}
				catch
				{
					// Undecodable file: fall back to the static card
				}

				lock (sync)
					active.Remove(card);

				if (lifetime.IsCancellationRequested)
					return;

				dispatcherQueue.TryEnqueue(async () =>
				{
					if (waveform is null || png is null)
					{
						card.WaveformUnavailable = true;
						return;
					}

					card.Waveform = waveform;
					card.WaveformImage = await png.ToBitmapAsync();
				});
			}
		}

		public void Dispose()
		{
			lifetime.Cancel();
			lock (sync)
			{
				queue.Clear();
				nodes.Clear();
				active.Clear();
			}
			lifetime.Dispose();
		}
	}
}
