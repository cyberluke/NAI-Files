// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Actions;
using Files.App.Controls;
using Microsoft.UI.Input;
using Microsoft.Extensions.Logging;
using System.IO;

namespace Files.App.Helpers
{
	/// <summary>Opt-in backend tracer: invokes the production context/actions without UI automation.</summary>
	internal static class DialogBackendTrace
	{
		private static bool started;

		public static async Task RunAsync(MainPage main)
		{
			if (started || App.OutputPath is not { } output)
				return;
			var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
			var name = Path.GetFileNameWithoutExtension(output);
			if (!Path.GetFullPath(output).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
				|| !name.StartsWith("nai-dialog-", StringComparison.Ordinal)
				|| !Guid.TryParseExact(name["nai-dialog-".Length..], "N", out _)
				|| Path.GetExtension(output) != ".txt")
				return;
			started = true;
			if (App.DialogTraceForWriter)
				MainWindow.Instance.AppWindow.Hide();
			var trace = output + ".trace";
			void Check(bool condition, string assertion)
			{
				File.AppendAllText(trace, (condition ? "PASS " : "FAIL ") + assertion + Environment.NewLine);
				if (!condition) throw new InvalidOperationException(assertion);
			}
			try
			{
				await MainPageViewModel.StartupTabsLoadedTask.WaitAsync(TimeSpan.FromSeconds(60));
				await UntilAsync(() => main.IsDialogContextBound);
				Check(main.IsDialogContextBound, "selected tab, content context, sidebar and toolbar share the active shell");

				var context = Ioc.Default.GetRequiredService<IContentPageContext>();
				var token = App.DialogTraceForWriter ? name["nai-dialog-".Length..] : Guid.NewGuid().ToString("N");
				var root = Path.Combine(Path.GetTempPath(), "nai-dialog-trace-" + token);
				Directory.CreateDirectory(root);
				var child = Directory.CreateDirectory(Path.Combine(root, "child folder")).FullName;
				var selected = Path.Combine(child, App.DialogTraceForWriter ? "výběr Writer.fodt" : "výběr file.txt");
				var contents = App.DialogTraceForWriter
					? "<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" office:version=\"1.3\" office:mimetype=\"application/vnd.oasis.opendocument.text\"><office:body><office:text><text:p>NAI Writer COM acceptance tracer</text:p></office:text></office:body></office:document>"
					: "dialog tracer fixture";
				await File.WriteAllTextAsync(selected, contents);
				if (App.DialogTraceForWriter)
				{
					// This tracer exercises Writer's real caller/acceptance path, not navigation
					// stress. Use the same model setter and confirm command as typed input.
					await UntilAsync(() => context.ShellPage?.ShellViewModel is { IsLoadingItems: false } model
						&& SamePath(model.WorkingDirectory, root));
					main.ViewModel.DialogFileName = selected;
					Check(main.ViewModel.CanConfirmDialog, "Writer fixture enables the actual Open command");
					Check(main.ViewModel.ResolveDialogSelection().Paths.SequenceEqual(new[] { selected }), "Writer fixture resolves to the exact native result");
					main.ViewModel.ConfirmDialog();
					Check(App.DialogConfirmRequested, "Writer result published; native caller owns consumption and cleanup");
					return;
				}
				var drivePath = Path.GetPathRoot(root)!;
				await UntilAsync(() => main.SidebarAdaptiveViewModel.GetSection(SectionType.Drives)?.ChildItems?
					.Any(item => SamePath(item.Path, drivePath)) == true);
				var drive = main.SidebarAdaptiveViewModel.GetSection(SectionType.Drives)!.ChildItems!
					.First(item => SamePath(item.Path, drivePath));
				Check(drive is DriveItem && ((ISidebarItemModel)drive).SelectsOnInvoked, "real drive row requests navigation rather than a section-header menu");
				await main.SidebarAdaptiveViewModel.HandleItemInvokedAsync(drive, PointerUpdateKind.LeftButtonReleased);
				await UntilAsync(() => SamePath(context.ShellPage?.ShellViewModel?.WorkingDirectory, drivePath));
				Check(true, "real drive invocation opens its contents in the active main pane");
				var home = main.SidebarAdaptiveViewModel.GetSection(SectionType.Home)!;
				await main.SidebarAdaptiveViewModel.HandleItemInvokedAsync(home, PointerUpdateKind.LeftButtonReleased);
				await UntilAsync(() => context.PageType == ContentPageTypes.Home);
				Check(true, "Home row opens the Home page while retaining the active shell");

				async Task NavigateFromSidebar(string path)
				{
					var location = new LocationItem { Path = path, Text = path, Section = SectionType.Pinned, SelectsOnInvoked = true };
					Check(((ISidebarItemModel)location).SelectsOnInvoked, "sidebar folder model requests navigation, not expansion");
					await main.SidebarAdaptiveViewModel.HandleItemInvokedAsync(location, PointerUpdateKind.LeftButtonReleased);
					await UntilAsync(() => SamePath(context.ShellPage?.ShellViewModel?.WorkingDirectory, path));
					await WaitForFixtureListing(path);
					Check(SamePath(context.ShellPage?.ShellViewModel?.WorkingDirectory, path), "sidebar invocation changes the main pane folder");
				}

				async Task WaitForFixtureListing(string path)
				{
					// Navigation updates the address before asynchronous enumeration completes.
					// Do not chain the next action while the previous listing is still loading.
					var expected = SamePath(path, root) ? child : selected;
					await UntilAsync(() => context.ShellPage?.ShellViewModel is { IsLoadingItems: false } model
						&& model.FilesAndFolders.Any(item => item.ItemPath == expected));
				}

				await NavigateFromSidebar(root);
				await NavigateFromSidebar(child);
				var back = new NavigateBackAction();
				await UntilAsync(() => back.IsExecutable);
				await back.ExecuteAsync();
				await UntilAsync(() => SamePath(context.ShellPage?.ShellViewModel?.WorkingDirectory, root));
				await WaitForFixtureListing(root);
				Check(true, "toolbar Back action changes the active pane to its prior directory");
				var forward = new NavigateForwardAction();
				await UntilAsync(() => forward.IsExecutable);
				await forward.ExecuteAsync();
				await UntilAsync(() => SamePath(context.ShellPage?.ShellViewModel?.WorkingDirectory, child));
				await WaitForFixtureListing(child);
				Check(true, "toolbar Forward action restores the child directory");
				var up = new NavigateUpAction();
				await UntilAsync(() => up.IsExecutable);
				await up.ExecuteAsync();
				await UntilAsync(() => SamePath(context.ShellPage?.ShellViewModel?.WorkingDirectory, root));
				await WaitForFixtureListing(root);
				Check(true, "toolbar Up action navigates to parent directory");

				Check(await context.ShellPage!.ToolbarViewModel.HandleItemNavigationAsync(child), "address submission accepts a directory path");
				await UntilAsync(() => SamePath(context.ShellPage?.ShellViewModel?.WorkingDirectory, child));
				await UntilAsync(() => context.ShellPage?.ShellViewModel?.FilesAndFolders.Any(item => item.ItemPath == selected) == true);
				var shellModel = context.ShellPage?.ShellViewModel ?? throw new InvalidOperationException("Active shell model was lost.");
				var item = shellModel.FilesAndFolders.First(item => item.ItemPath == selected);
				((BaseShellPage)context.ShellPage).ContentPage!.ItemManipulationModel.SetSelectedItem(item);
				await UntilAsync(() => context.SelectedItem?.ItemPath == selected);
				Check(main.ViewModel.CanConfirmDialog, "selecting a file enables the actual footer command");
				Check(main.ViewModel.ResolveDialogSelection().Paths.SequenceEqual(new[] { selected }), "selection reaches the same resolver used for COM output");
				if (App.DialogMode == "save")
				{
					main.ViewModel.DialogFileName = "new save name.txt";
					await NavigateFromSidebar(root);
					Check(main.ViewModel.DialogFileName == "new save name.txt", "save filename survives folder navigation and context refresh");
					await NavigateFromSidebar(child);
					var destination = Path.Combine(child, "new save name.txt");
					Check(main.ViewModel.CanConfirmDialog && !File.Exists(destination), "save enables a new filename without creating it");
					main.ViewModel.ConfirmDialog();
					Check(File.ReadAllLines(output).SequenceEqual(new[] { destination }) && !File.Exists(destination), "save footer publishes exact destination without creating or truncating it");
					return;
				}
				if (App.DialogMode == "cancel")
				{
					MainWindow.Instance.Close();
					Check(File.Exists(output) && File.ReadAllLines(output).Length == 0, "window close synchronously publishes cancellation");
					return;
				}
				main.ViewModel.DialogFileName = Path.Combine(child, "missing.txt");
				Check(!main.ViewModel.CanConfirmDialog, "invalid typed override cannot return a stale selection");
				main.ViewModel.DialogFileName = $"\"{selected}\"";
				Check(main.ViewModel.CanConfirmDialog, "typing a quoted Unicode file path enables the footer");
				Check(main.SidebarAdaptiveViewModel.HasUniqueSections, "sidebar section creation is unique after asynchronous startup");
				using (var locked = File.Open(output, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
				{
					main.ViewModel.ConfirmDialog();
					Check(main.ViewModel.HasDialogError && main.IsDialogContextBound, "failed result publish keeps picker active and surfaces the error");
				}
				main.ViewModel.ConfirmDialog();
				Check(File.Exists(output) && File.ReadAllLines(output).SequenceEqual(new[] { selected }), "footer confirmation writes the exact file path for the native caller");
			}
			catch (Exception ex)
			{
				var context = Ioc.Default.GetRequiredService<IContentPageContext>();
				var shell = context.ShellPage?.ShellViewModel;
				File.AppendAllText(trace, $"STATE folder={shell?.WorkingDirectory}; loading={shell?.IsLoadingItems}; items={shell?.FilesAndFolders.Count}; selected={context.SelectedItem?.ItemPath}; context={main.IsDialogContextBound}" + Environment.NewLine);
				if (shell?.WorkingDirectory?.Contains("nai-dialog-trace-", StringComparison.Ordinal) == true)
					File.AppendAllLines(trace, shell.FilesAndFolders.Select(item => "ITEM " + item.ItemPath));
				File.AppendAllText(trace, "FAIL " + ex + Environment.NewLine);
				App.Logger.LogError(ex, "Dialog backend tracer failed");
				// The tracer is an explicit QA/acceptance driver. It must never
				// tear down a real interactive picker when its own assertion
				// fails (e.g. a stray `-dialogtrace writer` launch): only the
				// deliberately trace-requested runs auto-complete/close. A failed
				// driver on an interactive session logs and leaves the dialog
				// open so the user can still complete their file selection.
				if (App.DialogTraceRequested)
				{
					App.TryCompleteDialog(Array.Empty<string>(), out _);
					MainWindow.Instance.Close();
				}
			}
		}

		private static bool SamePath(string? left, string right) => string.Equals(
			left?.TrimEnd(Path.DirectorySeparatorChar), right.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

		private static async Task UntilAsync(Func<bool> predicate,
			[System.Runtime.CompilerServices.CallerArgumentExpression(nameof(predicate))] string? condition = null)
		{
			var deadline = DateTime.UtcNow.AddSeconds(60);
			while (!predicate())
			{
				if (DateTime.UtcNow >= deadline) throw new TimeoutException("Backend state did not reach: " + condition);
				await Task.Delay(100);
			}
		}
	}
}
