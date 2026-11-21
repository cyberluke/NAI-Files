// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Services.Pulse;
using Files.App.Services.Pulse.MediaLab;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.IO;

namespace Files.App.Dialogs
{
	/// <summary>
	/// NAI Media Lab: a capability-aware surface over the bundled FFmpeg. Operations are filtered
	/// by the binary's actual `-filters`/`-encoders`, grouped by Enhance / Create / Analyze, and
	/// always write a sibling file (originals are never modified). Analysis passes show their report.
	/// </summary>
	public sealed partial class PulseMediaLabDialog : ContentDialog
	{
		private readonly PulseCardViewModel card;
		private readonly PulseFfmpeg ffmpeg;
		private readonly MediaKind kind;
		private readonly CancellationTokenSource lifetime = new();
		private MediaOperationRegistry.CapabilityProbe? probe;
		private bool loading;

		public PulseMediaLabDialog(PulseCardViewModel card, PulseFfmpeg ffmpeg)
		{
			InitializeComponent();

			this.card = card;
			this.ffmpeg = ffmpeg;
			kind = ResolveKind(card);

			Title = Strings.PulseMediaLabTitle.GetLocalizedResource();
			SourceText.Text = $"{Path.GetFileName(card.Item.ItemPath)} · {kind}";

			PulseDialogChrome.EnableDrag(this);
			Opened += async (_, _) => await LoadAsync();
			Closed += (_, _) => lifetime.Cancel();
		}

		private static MediaKind ResolveKind(PulseCardViewModel card)
			=> card.IsVideo ? MediaKind.Video : card.IsAudio ? MediaKind.Audio : MediaKind.Image;

		private async Task LoadAsync()
		{
			try
			{
				Busy.IsActive = true;
				probe = new();
				await probe.LoadAsync(ffmpeg, lifetime.Token);

				var available = MediaOperationRegistry.For(kind).Where(probe.Supports).ToArray();

				loading = true;
				GroupBox.Items.Clear();
				foreach (var group in available.Select(operation => operation.Group).Distinct())
				{
					GroupBox.Items.Add(new ComboBoxItem
					{
						Content = group switch
						{
							MediaLabGroup.Enhance => Strings.PulseMediaLabEnhance.GetLocalizedResource(),
							MediaLabGroup.Create => Strings.PulseMediaLabCreate.GetLocalizedResource(),
							_ => Strings.PulseMediaLabAnalyze.GetLocalizedResource(),
						},
						Tag = group,
					});
				}
				if (GroupBox.Items.Count > 0)
					GroupBox.SelectedIndex = 0;
				loading = false;

				ApplyGroup();

				if (available.Length == 0)
					StatusText.Text = Strings.PulseMediaLabEmpty.GetLocalizedResource();
			}
			catch (Exception ex)
			{
				StatusText.Text = ex.Message;
			}
			finally
			{
				Busy.IsActive = false;
			}
		}

		private void ApplyGroup()
		{
			if (probe is null)
				return;

			var group = (GroupBox.SelectedItem as ComboBoxItem)?.Tag is MediaLabGroup selected
				? selected
				: MediaLabGroup.Enhance;

			var operations = MediaOperationRegistry.For(kind)
				.Where(operation => operation.Group == group && probe.Supports(operation))
				.ToArray();

			OperationsList.ItemsSource = operations;
			OperationsList.SelectedIndex = operations.Length > 0 ? 0 : -1;
			RunButton.IsEnabled = operations.Length > 0;
		}

		private void GroupBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (!loading)
				ApplyGroup();
		}

		private void OperationsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (OperationsList.SelectedItem is MediaOperation operation)
			{
				StatusText.Text = operation.Description;
				RunButton.IsEnabled = true;
			}
		}

		private async void Run_Click(object sender, RoutedEventArgs e)
		{
			if (OperationsList.SelectedItem is not MediaOperation operation)
				return;

			var sourcePath = card.Item.ItemPath;
			if (string.IsNullOrEmpty(sourcePath))
				return;

			var outputPath = operation.IsAnalysis
				? string.Empty
				: ResolveOutputPath(sourcePath, operation.Suffix, operation.OutputExtension);

			var progress = new Progress<double>(value =>
			{
				Progress.IsIndeterminate = false;
				Progress.Value = Math.Clamp(value * 100, 0, 100);
			});

			Busy.IsActive = true;
			RunButton.IsEnabled = false;
			Progress.Visibility = Visibility.Visible;
			Progress.IsIndeterminate = true;
			ResultScroller.Visibility = Visibility.Collapsed;
			StatusText.Text = Strings.PulseQuickWorking.GetLocalizedResource();

			try
			{
				var result = await operation.Execute(
					new MediaOperationContext(sourcePath, outputPath, ffmpeg), progress, lifetime.Token);

				Progress.IsIndeterminate = false;
				Progress.Value = 100;

				if (operation.IsAnalysis)
				{
					ResultText.Text = result ?? string.Empty;
					ResultScroller.Visibility = Visibility.Visible;
					StatusText.Text = Strings.PulseMediaLabReport.GetLocalizedResource();
				}
				else
				{
					StatusText.Text = result is null
						? Strings.PulseQuickFailed.GetLocalizedResource()
						: string.Format(Strings.PulseQuickSaved.GetLocalizedResource(), Path.GetFileName(result));
				}
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
				Busy.IsActive = false;
				RunButton.IsEnabled = true;
				Progress.IsIndeterminate = false;
			}
		}

		private static string ResolveOutputPath(string sourcePath, string suffix, string extension)
		{
			var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
			var stem = Path.GetFileNameWithoutExtension(sourcePath);
			if (string.IsNullOrWhiteSpace(stem))
				stem = "media";

			var candidate = Path.Combine(directory, $"{stem} ({suffix}).{extension}");
			var counter = 2;
			while (File.Exists(candidate))
				candidate = Path.Combine(directory, $"{stem} ({suffix}) ({counter++}).{extension}");
			return candidate;
		}
	}
}
