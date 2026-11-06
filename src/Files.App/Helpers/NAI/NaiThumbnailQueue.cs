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
				if (disposed || token.IsCancellationRequested || (viewportKnown && !visible.Contains(path))) return Task.FromCanceled(new CancellationToken(true));
				if (ownJobs.TryGetValue(path, out var existing) && !existing.Cancellation.IsCancellationRequested) return existing.Completion.Task;
				if (jobs.Count >= Capacity)
				{
					var victim = jobs.LastOrDefault(job => !job.Running && !job.Owner.visible.Contains(job.Key));
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
				visible = new(paths, StringComparer.OrdinalIgnoreCase);
				viewportKnown = true;
				foreach (var job in ownJobs.Values.Where(job => !visible.Contains(job.Key)).ToArray()) CancelLocked(job);
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
