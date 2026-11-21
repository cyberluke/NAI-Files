// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.IO;

namespace Files.App.Dialogs
{
	/// <summary>
	/// A small movable progress dialog for one-shot Pulse media actions (extract audio, mute,
	/// rotate, flip). The action receives a progress sink and the dialog's cancellation token;
	/// it returns the output path on success or null on a reported failure.
	/// </summary>
	public sealed partial class PulseQuickActionDialog : ContentDialog
	{
		private readonly Func<IProgress<double>, CancellationToken, Task<string?>> work;
		private readonly CancellationTokenSource lifetime = new();

		public string? OutputPath { get; private set; }

		public PulseQuickActionDialog(string title, string sourceName, Func<IProgress<double>, CancellationToken, Task<string?>> work)
		{
			InitializeComponent();
			Title = title;
			SourceText.Text = sourceName;
			this.work = work;

			PulseDialogChrome.EnableDrag(this);
			Opened += async (_, _) => await RunAsync();
			Closed += (_, _) => lifetime.Cancel();
		}

		private async Task RunAsync()
		{
			var progress = new Progress<double>(value =>
			{
				Progress.IsIndeterminate = false;
				Progress.Value = Math.Clamp(value * 100, 0, 100);
			});

			try
			{
				Progress.IsIndeterminate = true;
				StatusText.Text = Strings.PulseQuickWorking.GetLocalizedResource();

				OutputPath = await work(progress, lifetime.Token);

				Progress.IsIndeterminate = false;
				Progress.Value = 100;
				StatusText.Text = OutputPath is null
					? Strings.PulseQuickFailed.GetLocalizedResource()
					: string.Format(Strings.PulseQuickSaved.GetLocalizedResource(), Path.GetFileName(OutputPath));
			}
			catch (OperationCanceledException)
			{
				StatusText.Text = Strings.PulseQuickCancelled.GetLocalizedResource();
			}
			catch (Exception ex)
			{
				StatusText.Text = ex.Message;
			}
			finally
			{
				Progress.IsIndeterminate = false;
			}
		}
	}
}
