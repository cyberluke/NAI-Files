// Copyright (c) Files Community
// Licensed under the MIT License.
namespace Files.App.Services.Pulse
{
	internal sealed class PulseRuntimeDiscovery<T>(Func<CancellationToken, T?> discover) where T : class
	{
		private readonly object gate = new();
		private Task<T?>? task;
		private CancellationTokenSource? lifetime;
		private int readers;
		public async Task<T?> GetAsync(CancellationToken cancellationToken)
		{
			Task<T?> pending;
			CancellationTokenSource source;
			lock (gate)
			{
				if (task is null || task.IsFaulted || task.IsCanceled || lifetime?.IsCancellationRequested == true
					|| (task.IsCompletedSuccessfully && task.Result is null))
				{
					lifetime = new CancellationTokenSource();
					var token = lifetime.Token;
					task = Task.Run(() => discover(token), token);
					readers = 0;
				}
				pending = task;
				source = lifetime!;
				readers++;
			}
			try { return await pending.WaitAsync(cancellationToken); }
			finally
			{
				lock (gate)
					if (ReferenceEquals(task, pending) && --readers == 0 && !pending.IsCompleted) source.Cancel();
			}
		}
	}
}
