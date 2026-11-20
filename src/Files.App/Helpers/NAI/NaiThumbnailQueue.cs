// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Helpers
{
	public enum NaiThumbnailOutcome { Ready, Unavailable }
	public sealed record NaiThumbnailProgress(int Pending, int Running, int Completed, int Unavailable, int Failed)
	{
		public int Settled => Completed + Unavailable + Failed;
		public int Total => Pending + Running + Settled;
	}

	/// <summary>Application-wide bounded admission; actual shell work stays on its existing STA workers.</summary>
	public sealed partial class NaiThumbnailQueue : IDisposable
	{
		private sealed class Job(NaiThumbnailQueue owner, string key, Func<CancellationToken, Task<NaiThumbnailOutcome>> work, CancellationToken token)
		{
			public readonly NaiThumbnailQueue Owner = owner;
			public readonly string Key = key;
			public readonly int Generation = owner.generation;
			public readonly Func<CancellationToken, Task<NaiThumbnailOutcome>> Work = work;
			public readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
			public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
			public bool Running;
		}

		private const int Capacity = 256;
		private static readonly object gate = new();
		private static readonly List<Job> jobs = [];
		private static readonly SemaphoreSlim signal = new(0);
		private static readonly Task[] workers = Enumerable.Range(0, 4).Select(_ => Task.Run(WorkerAsync)).ToArray();
		private readonly Dictionary<string, Job> ownJobs = new(StringComparer.OrdinalIgnoreCase);
		private HashSet<string> visible = new(StringComparer.OrdinalIgnoreCase);
		private int generation;
		private int completed, unavailable, failed;
		private bool disposed;
		private bool viewportKnown;
		public event Action? Changed;
		private void Notify()
		{
			try { Changed?.Invoke(); }
			catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Thumbnail progress listener failed: " + ex.Message); }
		}

		public NaiThumbnailProgress Progress
		{
			get { lock (gate) return new(ownJobs.Values.Count(job => !job.Running), ownJobs.Values.Count(job => job.Running), completed, unavailable, failed); }
		}

		public Task Enqueue(string path, Func<CancellationToken, Task<NaiThumbnailOutcome>> work, CancellationToken token)
		{
			Task task;
			lock (gate)
			{
				// The viewport is a scheduling PREFERENCE, never a veto. Callers only request thumbnails for
				// realized (visible) rows, and the worker already runs visible jobs first. Vetoing here dropped
				// loads whenever the 50 ms viewport snapshot lagged container realization, and because the item
				// is flagged ItemPropertiesInitialized before this call it was never retried until a full reload.
				if (disposed || token.IsCancellationRequested) return Task.FromCanceled(new CancellationToken(true));
				if (ownJobs.TryGetValue(path, out var existing) && !existing.Cancellation.IsCancellationRequested) return existing.Completion.Task;
				if (jobs.Count >= Capacity)
				{
					// Prefer evicting a queued (not running) job that is outside every owner's visible set; once no
					// viewport has been reported, fall back to the oldest queued job so a burst can never wedge.
					var victim = viewportKnown
						? jobs.LastOrDefault(job => !job.Running && !job.Owner.visible.Contains(job.Key))
						: jobs.LastOrDefault(job => !job.Running);
					if (victim is null) return Task.FromCanceled(new CancellationToken(true));
					CancelLocked(victim);
				}
				var job = new Job(this, path, work, token);
				ownJobs[path] = job;
				jobs.Add(job);
				task = job.Completion.Task;
				signal.Release();
			}
			Notify();
			return task;
		}

		public void UpdateViewport(IEnumerable<string> paths)
		{
			lock (gate)
			{
				var next = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
				// An empty report happens during initial layout before any item is realized (a width or range
				// change fires ViewChanged while the panel is still empty). Keep the previous priority set in
				// that case; it is re-established by the next non-empty report.
				if (next.Count == 0)
					return;

				// Re-ordering priority must not cancel in-flight or queued loads: a job cancelled because its row
				// briefly scrolled out has already marked its item initialized and would never be retried until a
				// full reload. Pressure is instead handled by the Capacity eviction in Enqueue.
				visible = next;
				viewportKnown = true;
			}
			Notify();
		}

		public void Cancel(string path)
		{
			lock (gate) { if (ownJobs.TryGetValue(path, out var job)) CancelLocked(job); }
			Notify();
		}

		public void Reset()
		{
			lock (gate)
			{
				generation++;
				viewportKnown = false;
				visible.Clear();
				foreach (var job in ownJobs.Values.ToArray()) CancelLocked(job);
				completed = unavailable = failed = 0;
			}
			Notify();
		}

		private static void CancelLocked(Job job)
		{
			job.Cancellation.Cancel();
			RemoveOwnedLocked(job);
			if (!job.Running)
			{
				jobs.Remove(job);
				job.Completion.TrySetCanceled();
				job.Cancellation.Dispose();
			}
			// Running native calls cannot be interrupted: their worker remains
			// occupied until they return, and their cancelled result is discarded.
		}

		private static void RemoveOwnedLocked(Job job)
		{
			if (job.Owner.ownJobs.TryGetValue(job.Key, out var current) && ReferenceEquals(current, job))
				job.Owner.ownJobs.Remove(job.Key);
		}

		private static async Task WorkerAsync()
		{
			while (true)
			{
				await signal.WaitAsync().ConfigureAwait(false);
				Job? job;
				lock (gate)
				{
					job = jobs.FirstOrDefault(candidate => !candidate.Running && candidate.Owner.visible.Contains(candidate.Key))
						?? jobs.FirstOrDefault(candidate => !candidate.Running);
					if (job is not null) job.Running = true;
				}
				if (job is null) continue;
				NaiThumbnailOutcome? outcome = null;
				Exception? error = null;
				bool cancelled = false;
				try
				{
					job.Owner.Notify();
					job.Cancellation.Token.ThrowIfCancellationRequested();
					outcome = await job.Work(job.Cancellation.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException) { cancelled = true; }
				catch (Exception ex) { error = ex; }
				finally
				{
					lock (gate)
					{
						cancelled |= job.Cancellation.IsCancellationRequested || job.Generation != job.Owner.generation || job.Owner.disposed;
						jobs.Remove(job);
						RemoveOwnedLocked(job);
						if (!cancelled)
						{
							if (error is not null) job.Owner.failed++;
							else if (outcome == NaiThumbnailOutcome.Ready) job.Owner.completed++;
							else job.Owner.unavailable++;
						}
					}
					job.Cancellation.Dispose();
					if (cancelled) job.Completion.TrySetCanceled();
					else if (error is not null) job.Completion.TrySetException(error);
					else job.Completion.TrySetResult();
					job.Owner.Notify();
				}
			}
		}

		public void Dispose()
		{
			lock (gate) { disposed = true; }
			Reset();
			Changed = null;
		}
	}
}
