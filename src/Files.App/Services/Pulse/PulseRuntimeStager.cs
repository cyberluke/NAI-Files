// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Security.Cryptography;
using System.Text;
using Windows.ApplicationModel;

namespace Files.App.Services.Pulse
{
	/// <summary>Prepares bundled child-process runtimes in the user's writable cache.</summary>
	internal static class PulseRuntimeStager
	{
		private static readonly object FfmpegLock = new();
		private static readonly object DemucsLock = new();
		private static string? stagedRoot;
		public static string? GetStagedRoot() => stagedRoot;
		public static bool IsFfmpegStaged { get; private set; }
		public static bool IsDemucsStaged { get; private set; }

		public static void StageFfmpeg(CancellationToken cancellationToken = default)
		{
			lock (FfmpegLock)
			{
				if (!IsFfmpegStaged)
					IsFfmpegStaged = Stage("ffmpeg", ["ffmpeg.exe", "ffprobe.exe"], cancellationToken);
			}
		}

		public static void StageDemucs(CancellationToken cancellationToken = default)
		{
			lock (DemucsLock)
			{
				if (!IsDemucsStaged)
					IsDemucsStaged = Stage("demucs", [Path.Combine("python", "python.exe")], cancellationToken);
			}
		}

		private static bool Stage(string runtime, string[] required, CancellationToken cancellationToken)
		{
			Package package;
			try { package = Package.Current; }
			catch (InvalidOperationException) { return false; }
			var source = Path.Combine(package.InstalledLocation.Path, runtime);
			if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
			foreach (var relative in required)
				if (!File.Exists(Path.Combine(source, relative))) throw new FileNotFoundException(Path.Combine(source, relative));

			var root = Path.Combine(Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path, "PulseRuntime", package.Id.FullName);
			var destination = Path.Combine(root, runtime);
			var marker = Path.Combine(root, runtime + ".ready");
			var identity = package.Id.FullName;
			var mutexName = @"Local\NAI.PulseRuntime." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root + runtime)));
			using var mutex = new Mutex(false, mutexName);
			var ownsMutex = false;
			try
			{
				while (!ownsMutex)
				{
					cancellationToken.ThrowIfCancellationRequested();
					try { ownsMutex = mutex.WaitOne(100); }
					catch (AbandonedMutexException) { ownsMutex = true; }
				}
			if (File.Exists(marker) && File.ReadAllText(marker) == identity
				&& required.All(relative => File.Exists(Path.Combine(destination, relative))))
			{
				stagedRoot = root;
				return true;
			}

			Directory.CreateDirectory(destination);
			var buffer = new byte[256 * 1024];
			foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
			{
				cancellationToken.ThrowIfCancellationRequested();
				var target = Path.Combine(destination, Path.GetRelativePath(source, file));
				var original = new FileInfo(file);
				var cached = new FileInfo(target);
				if (cached.Exists && original.Length == cached.Length && original.LastWriteTimeUtc == cached.LastWriteTimeUtc)
					continue;
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				var temporary = target + ".staging-" + Guid.NewGuid().ToString("N");
				try
				{
					using (var input = File.OpenRead(file))
					using (var output = File.Create(temporary))
					{
						int count;
						while ((count = input.Read(buffer)) > 0)
						{
							cancellationToken.ThrowIfCancellationRequested();
							output.Write(buffer, 0, count);
						}
					}
					File.SetLastWriteTimeUtc(temporary, original.LastWriteTimeUtc);
					File.Move(temporary, target, overwrite: true);
				}
				finally
				{
					if (File.Exists(temporary)) File.Delete(temporary);
				}
			}

			foreach (var relative in required)
				if (!File.Exists(Path.Combine(destination, relative))) throw new FileNotFoundException(Path.Combine(destination, relative));
			var pendingMarker = marker + "." + Guid.NewGuid().ToString("N");
			File.WriteAllText(pendingMarker, identity);
			File.Move(pendingMarker, marker, overwrite: true);
			stagedRoot = root;
			return true;
			}
			finally { if (ownsMutex) mutex.ReleaseMutex(); }
		}
	}
}
