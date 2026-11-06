// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Files.App.Services.Pulse
{
	/// <summary>Hidden, cancellable media processes with both pipes drained and useful failure details.</summary>
	internal static class PulseProcess
	{
		private static readonly SemaphoreSlim Slots = new(2, 2);

		public static async Task<string> TextAsync(ProcessStartInfo start, CancellationToken cancellationToken,
			TimeSpan? timeout = null)
			=> await RunAsync(start, async (stream, token) =>
			{
				using var reader = new StreamReader(stream, leaveOpen: true);
				return await reader.ReadToEndAsync(token).ConfigureAwait(false);
			}, cancellationToken, timeout).ConfigureAwait(false);

		public static async Task<T> RunAsync<T>(ProcessStartInfo start,
			Func<Stream, CancellationToken, Task<T>> readOutput, CancellationToken cancellationToken,
			TimeSpan? timeout = null, Action<string>? errorOutput = null)
		{
			await Slots.WaitAsync(cancellationToken).ConfigureAwait(false);
			using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			deadline.CancelAfter(timeout ?? TimeSpan.FromHours(6));
			var tool = Path.GetFileName(start.FileName);
			try
			{
				start.UseShellExecute = false;
				start.CreateNoWindow = true;
				start.RedirectStandardInput = true;
				start.RedirectStandardOutput = true;
				start.RedirectStandardError = true;
				using var process = new Process { StartInfo = start };
				try
				{
					if (!process.Start()) throw new IOException(start.FileName);
				}
				catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
				{
					throw new IOException(string.Format(Strings.PulseMediaStartFailed.GetLocalizedResource(), tool, ex.Message), ex);
				}
				process.StandardInput.Close();
				try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
				catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
				using var stop = deadline.Token.Register(() => Kill(process));
				var stderr = ReadTailAsync(process.StandardError, errorOutput, deadline.Token);
				try
				{
					var output = await readOutput(process.StandardOutput.BaseStream, deadline.Token).ConfigureAwait(false);
					await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
					var details = await stderr.WaitAsync(deadline.Token).ConfigureAwait(false);
					if (process.ExitCode != 0)
						throw new IOException(string.Format(Strings.PulseMediaProcessFailed.GetLocalizedResource(), tool, process.ExitCode, details));
					return output;
				}
				finally
				{
					Kill(process);
					try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
					catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { }
					try { await stderr.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
					catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { }
				}
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				throw new TimeoutException(string.Format(Strings.PulseMediaTimedOut.GetLocalizedResource(), tool));
			}
			finally
			{
				Slots.Release();
			}
		}

		private static async Task<string> ReadTailAsync(StreamReader reader, Action<string>? output, CancellationToken cancellationToken)
		{
			const int maximum = 8192;
			var tail = new StringBuilder();
			var buffer = new char[2048];
			int count;
			while ((count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
			{
				tail.Append(buffer, 0, count);
				output?.Invoke(new string(buffer, 0, count));
				if (tail.Length > maximum) tail.Remove(0, tail.Length - maximum);
			}
			return tail.ToString().Trim();
		}

		private static void Kill(Process process)
		{
			try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
			catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
		}
	}
}
