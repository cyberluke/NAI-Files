// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Services.Pulse;
using Files.App.ViewModels.Properties;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using System.IO;
using Windows.Media.Core;

namespace Files.App.ViewModels.Previews
{
	public sealed partial class MediaPreviewViewModel : BasePreviewModel
	{
		public event EventHandler? TogglePlaybackRequested;
		private readonly CancellationTokenSource lifetime = new();
		private MediaSource? source;
		private PulseMediaPlayback? playback;
		private bool closed;
		private bool compatibilityAttempted;
		private bool isPreparing;
		private string errorMessage = string.Empty;

		public MediaSource? Source { get => source; private set => SetProperty(ref source, value); }
		public bool IsPreparing { get => isPreparing; private set => SetProperty(ref isPreparing, value); }
		public string ErrorMessage
		{
			get => errorMessage;
			private set { if (SetProperty(ref errorMessage, value)) OnPropertyChanged(nameof(HasError)); }
		}
		public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

		public MediaPreviewViewModel(ListedItem item) : base(item) { }
		public void TogglePlayback() => TogglePlaybackRequested?.Invoke(this, EventArgs.Empty);

		public override Task<List<FileProperty>> LoadPreviewAndDetailsAsync()
		{
			if (!closed && Item.ItemFile is { } file)
			{
				try { Source = MediaSource.CreateFromStorageFile(file); }
				catch (Exception ex) { ReportError(ex.Message); }
			}
			return Task.FromResult(new List<FileProperty>());
		}

		public async Task PrepareCompatibleAsync(string nativeError)
		{
			if (closed) return;
			if (compatibilityAttempted) { ReportError(nativeError); return; }
			compatibilityAttempted = true;
			IsPreparing = true;
			ErrorMessage = string.Empty;
			PulseMediaPlayback? prepared = null;
			MediaSource? preparedSource = null;
			try
			{
				var file = Item.ItemFile ?? throw new FileNotFoundException(Item.ItemPath);
				var ffmpeg = await PulseFfmpeg.RequireAsync(lifetime.Token);
				prepared = await new PulseMediaGate(ffmpeg).PrepareAsync(file.Path, lifetime.Token, forceCompatible: true);
				if (prepared is null) throw new IOException(Strings.PulseTrimPreviewFailed.GetLocalizedResource());
				var playable = await StorageFileExtensions.DangerousGetFileFromPathAsync(prepared.Path);
				if (closed || lifetime.IsCancellationRequested) return;
				if (playable is null) throw new FileNotFoundException(prepared.Path);
				preparedSource = MediaSource.CreateFromStorageFile(playable);
				var previous = Source;
				Source = preparedSource;
				preparedSource = null;
				previous?.Dispose();
				playback?.Dispose();
				playback = prepared;
				prepared = null;
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Media preview preparation failed");
				ReportError(nativeError + Environment.NewLine + ex.Message);
			}
			finally
			{
				preparedSource?.Dispose();
				prepared?.Dispose();
				if (!closed) IsPreparing = false;
			}
		}

		public void ReportError(string message)
		{
			if (!closed) ErrorMessage = message;
		}

		public override void PreviewControlBase_Unloaded(object? sender, RoutedEventArgs e)
		{
			if (closed) return;
			closed = true;
			lifetime.Cancel();
			var previous = Source;
			Source = null;
			previous?.Dispose();
			playback?.Dispose();
			playback = null;
			base.PreviewControlBase_Unloaded(sender, e);
		}
	}
}
