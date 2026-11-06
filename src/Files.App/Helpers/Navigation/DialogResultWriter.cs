// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Text;
using System.Threading;

namespace Files.App.Helpers
{
	internal sealed class DialogResultWriter(string outputPath)
	{
		private readonly object gate = new();
		private bool completed;
		public string OutputPath { get; } = outputPath;
		public bool IsConfirmed { get; private set; }
		public static string EventName(string path) => @"Local\NAI.Files.Dialog." + Path.GetFileName(path);

		public static void Started(string path)
		{
			File.WriteAllText(path + ".pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
		}

		public bool TryComplete(IReadOnlyList<string> paths, out string error, int fileTypeIndex = 1, Action? beforeSignal = null)
		{
			lock (gate)
			{
				error = string.Empty;
				if (completed)
					return true;
				var temporary = OutputPath + ".writing";
				try
				{
					using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, EventName(OutputPath));
					if (paths.Count > 0)
					{
						File.WriteAllText(temporary + ".typeindex", Math.Max(1, fileTypeIndex).ToString(System.Globalization.CultureInfo.InvariantCulture));
						File.Move(temporary + ".typeindex", OutputPath + ".typeindex", overwrite: true);
					}
					File.WriteAllLines(temporary, paths, new UTF8Encoding(false));
					File.Move(temporary, OutputPath, overwrite: true);
					beforeSignal?.Invoke();
					signal.Set();
					IsConfirmed = paths.Count > 0;
					completed = true;
					return true;
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
				{
					error = ex.Message;
					return false;
				}
				finally
				{
					try { File.Delete(temporary); }
					catch (IOException) { }
					catch (UnauthorizedAccessException) { }
					try { File.Delete(temporary + ".typeindex"); }
					catch (IOException) { }
					catch (UnauthorizedAccessException) { }
				}
			}
		}
	}
}
