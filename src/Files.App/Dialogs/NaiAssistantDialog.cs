// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.IO;
using System.Text;
using Windows.ApplicationModel.DataTransfer;

namespace Files.App.Dialogs
{
	internal static class NaiAssistantDialog
	{
		private sealed record Operation(string Key, string Instruction);
		private sealed record PreviewSnapshot(string Text, IReadOnlyDictionary<string, string> Excerpts);
		private static readonly Operation[] operations =
		[
			new("NaiActionSummary", "Summarize each provided document with citations and a concise overall synthesis."),
			new("NaiActionQuestion", "Answer the user's question only from the provided excerpts. Say when evidence is missing."),
			new("NaiActionKeyPoints", "Extract the main claims, facts, and decisions. Cite their source filenames."),
			new("NaiActionTasks", "Extract actionable tasks and cite the source filename for every task. Separately report project-level owners and deadlines exactly as stated. Do NOT assign a project milestone to every task. Do not say dates/owners are missing when the supplied documents state them. Individual-task deadlines can be unspecified while the project deadline is known."),
			new("NaiActionExplain", "Explain the selected content in plain language. Define jargon without inventing facts."),
			new("NaiActionTranslate", "Return ONLY a JSON array of {\"source\":\"exact provided filename\",\"translation\":\"complete translated excerpt\"}, ONE entry for EVERY provided document. Translate the complete excerpts into the requested language (or interface language), do NOT summarize or omit a document. Preserve all numbers, Markdown, code, filenames, personal names and product names exactly. Do NOT localize or transliterate personal names. Use the requested language consistently; avoid mixed-script words. Do not translate metadata headers."),
			new("NaiActionCompare", "Compare the supplied documents: shared points, differences and contradictions. Cite both sources; do not infer omitted content."),
			new("NaiActionName", "Return ONLY a JSON array of {\"source\":\"exact provided filename\",\"proposedName\":\"improved filename\",\"reason\":\"short reason\"}. Propose distinct, descriptive Windows-safe names derived from document titles/topics. Preserve each extension. Do NOT repeat unchanged names, invent source files, or claim to rename anything."),
			new("NaiActionTags", "Return ONLY a JSON array of {\"source\":\"exact provided filename\",\"tags\":[\"tag\"]}. Every tag set must be attributed to a supplied filename. Suggest 2-5 short tags grounded in its text/name; no invented source files or explanations outside JSON."),
			new("NaiActionOrganize", "The input is a FLAT listing of sibling entries in one folder; subfolder contents were NOT read. Never claim an existing folder contains other listed entries. Return ONLY a JSON array of up to 16 proposed {\"source\":\"exact provided FILE name\",\"destinationFolder\":\"relative folder name\"}. Do not move directory entries or invent source files. Group actual files sensibly by type/topic. No absolute paths, traversal, deletion, explanations, or claims that changes occurred."),
			new("NaiActionSearch", "Return ONLY a JSON object with these keys: nameContains (string or null), extensions (array of extensions without dots, or null), contentContains (string or null), modifiedAfter (YYYY-MM-DD or null), modifiedBefore (YYYY-MM-DD or null). At least one criterion required. Markdown means extensions md and markdown, PDF means pdf. No System.Kind, AQS, SQL, commands or extra keys: the application builds the indexed query safely."),
		];

		private static string L(string key) => key.GetLocalizedResource();
		[WinRT.DynamicWindowsRuntimeCast(typeof(Style))]
		public static async Task ShowAsync(XamlRoot root, IContentPageContext context)
		{
			using var lifetime = new CancellationTokenSource();
			CancellationTokenSource? request = null;
			var selection = context.SelectedItems.Where(item => !item.IsFolder && item.ItemPath is not null).Take(8).ToArray();
			var currentFolder = context.ShellPage?.ShellViewModel?.FilesAndFolders.Take(100).ToArray() ?? [];
			var choice = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, ColumnSpacing = 8, RowSpacing = 8 };
			var selectedOperation = operations.First(op => op.Key == "NaiActionSearch");
			var actionOrder = new[] { "NaiActionSearch", "NaiActionOrganize", "NaiActionName", "NaiActionTags", "NaiActionCompare", "NaiActionQuestion", "NaiActionSummary", "NaiActionKeyPoints", "NaiActionTasks", "NaiActionTranslate", "NaiActionExplain" };
			var actionButtons = new List<ToggleButton>();
			var actionTitle = new TextBlock { Text = L(selectedOperation.Key), Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["SubtitleTextBlockStyle"], TextWrapping = TextWrapping.Wrap };
			var models = new ComboBox { Header = L("NaiAiModel"), HorizontalAlignment = HorizontalAlignment.Stretch };
			var prompt = new TextBox { PlaceholderText = L("NaiAiPrompt"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 4000, MinHeight = 60 };
			var preview = new TextBox { Header = L("NaiAiPreview"), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 150 };
			var consent = new CheckBox { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = new TextBlock { Text = L("NaiAiConsent"), TextWrapping = TextWrapping.Wrap } };
			var result = new TextBox { Header = L("NaiAiResult"), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 240, MinHeight = 80 };
			var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
			var icon = new ProgressRing { Width = 22, Height = 22, IsActive = false, Visibility = Visibility.Collapsed };
			var connect = new Button { Content = L("NaiAiPair") };
			var disconnect = new Button { Content = L("NaiAiDisconnect") };
			var generate = new Button { Content = L("NaiAiGenerate"), IsEnabled = false };
			var cancel = new Button { Content = Strings.Cancel.GetLocalizedResource(), IsEnabled = false };
			var copy = new Button { Content = Strings.Copy.GetLocalizedResource(), IsEnabled = false };
			var unread = new Button { Content = L("NaiMarkUnopened"), IsEnabled = selection.Any(item => NaiDocumentHistory.IsDocument(item.ItemPath)) };
			var query = new TextBox { Header = L("NaiActionSearch"), MaxLength = 2048, Visibility = Visibility.Collapsed };
			var runSearch = new Button { Content = L("NaiAiApplySearch"), Visibility = Visibility.Collapsed };
			runSearch.IsEnabled = context.ShellPage?.ShellViewModel?.WorkingDirectory is string directory && directory != "Settings";
			var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
			controls.Children.Add(icon); controls.Children.Add(generate); controls.Children.Add(cancel); controls.Children.Add(copy);
			var pairing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
			pairing.Children.Add(connect); pairing.Children.Add(disconnect);
			var connection = new Expander { Header = L("NaiConnectionSettings"), IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
			var connectionBody = new StackPanel { Spacing = 10 };
			connectionBody.Children.Add(models); connectionBody.Children.Add(pairing);
			connection.Content = connectionBody;
			var previewDetails = new Expander { Header = L("NaiAiPreview"), Content = preview, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
			var panel = new StackPanel { Spacing = 14, MinWidth = 0, MaxWidth = 960, HorizontalAlignment = HorizontalAlignment.Stretch };
			var vision = new Button { Content = L("NaiVisionTitle"), Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 56 };
			foreach (var control in new UIElement[] {
				new TextBlock { Text = L("NaiChooseAction"), Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["SubtitleTextBlockStyle"], TextWrapping = TextWrapping.Wrap },
				vision, choice, actionTitle, prompt, controls, status, result, query, runSearch,
				new TextBlock { Text = L("NaiAiNoSelection"), TextWrapping = TextWrapping.Wrap },
				previewDetails, consent, unread, connection,
				new TextBlock { Text = L("NaiAiReview"), TextWrapping = TextWrapping.Wrap, FontSize = 12 },
			}) panel.Children.Add(control);
			var dialog = new ContentDialog
			{
				XamlRoot = root, Title = L("NaiAssistantTitle"), CloseButtonText = Strings.Close.GetLocalizedResource(),
				Content = new ScrollViewer { Content = panel, MaxHeight = Math.Max(300, Math.Min(760, root.Size.Height - 180)), Padding = new Thickness(0, 0, 12, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled },
			};
			AutomationProperties.SetAutomationId(dialog, "NaiAssistantDialog");
			AutomationProperties.SetAutomationId(choice, "NaiOperation");
			AutomationProperties.SetName(choice, L("NaiAssistantTitle"));
			AutomationProperties.SetAutomationId(models, "NaiModel");
			AutomationProperties.SetAutomationId(prompt, "NaiQuestion");
			AutomationProperties.SetAutomationId(preview, "NaiContentPreview");
			AutomationProperties.SetAutomationId(consent, "NaiContentConsent");
			AutomationProperties.SetAutomationId(result, "NaiResult");
			AutomationProperties.SetAutomationId(generate, "NaiGenerate");
			AutomationProperties.SetAutomationId(connect, "NaiConnect");
			AutomationProperties.SetAutomationId(disconnect, "NaiDisconnect");
			AutomationProperties.SetAutomationId(connection, "NaiConnectionSettings");
			AutomationProperties.SetAutomationId(status, "NaiConnectionStatus");
			AutomationProperties.SetAutomationId(unread, "NaiMarkUnopened");
			dialog.Resources["ContentDialogMaxWidth"] = 1040d;
			vision.Click += (_, _) =>
			{
				var folder = context.ShellPage?.ShellViewModel?.WorkingDirectory;
				dialog.Hide();
				NaiWorkspaceRoutes.OpenVision(folder is not null && Directory.Exists(folder) ? folder : null);
			};
			bool busy = false;
			IReadOnlyDictionary<string, string> approvedExcerpts = new Dictionary<string, string>();
			bool preparingPreview = false;
			int previewVersion = 0;
			void UpdateEnabled()
			{
				var operation = selectedOperation.Key;
				var hasInput = operation switch
				{
					"NaiActionSearch" => true,
					"NaiActionOrganize" => currentFolder.Any(item => !item.IsFolder),
					"NaiActionCompare" => selection.Length >= 2,
					"NaiActionTranslate" => selection.Length > 0 && approvedExcerpts.Count == selection.Length,
					_ => selection.Length > 0,
				};
				generate.IsEnabled = !busy && !preparingPreview && hasInput && consent.IsChecked == true && models.SelectedItem is string;
			}
			void Busy(bool value)
			{
				busy = value; icon.IsActive = value; icon.Visibility = value ? Visibility.Visible : Visibility.Collapsed; cancel.IsEnabled = value;
				foreach (var card in actionButtons) card.IsEnabled = !value;
				models.IsEnabled = prompt.IsEnabled = connect.IsEnabled = disconnect.IsEnabled = !value;
				UpdateEnabled();
			}
			void Failure(Exception ex) => status.Text = string.Format(L("NaiAiFailure"), ex.Message);
			async Task LoadModels(CancellationToken cancellation)
			{
				models.Items.Clear();
				if (!NaiAiClient.IsPaired) { status.Text = L("NaiAiUnavailable"); connection.IsExpanded = true; return; }
				foreach (var model in await NaiAiClient.ModelsAsync(cancellation)) models.Items.Add(model);
				if (Windows.Storage.ApplicationData.Current.LocalSettings.Values.TryGetValue("NaiAi.Model", out var savedModel)
					&& savedModel is string saved && models.Items.Contains(saved)) models.SelectedItem = saved;
				status.Text = models.Items.Count == 0 ? L("NaiNoCompatibleModel")
					: savedModel is string pinned && !models.Items.Contains(pinned) ? string.Format(L("NaiPinnedModelUnavailable"), pinned)
					: L("NaiAiConnected");
				connection.IsExpanded = models.SelectedItem is not string;
			}
			async Task LoadPreview()
			{
				var version = ++previewVersion;
				preparingPreview = true;
				consent.IsChecked = false;
				UpdateEnabled();
				var op = selectedOperation;
				var source = op.Key == "NaiActionOrganize" ? currentFolder : selection;
				try
				{
					var content = await PreviewAsync(source, op.Key != "NaiActionOrganize" && op.Key != "NaiActionSearch", lifetime.Token);
					if (version == previewVersion) { preview.Text = content.Text; approvedExcerpts = content.Excerpts; consent.IsChecked = false; }
				}
				finally { if (version == previewVersion) { preparingPreview = false; UpdateEnabled(); } }
			}
			dialog.Opened += async (_, _) =>
			{
				try { await LoadPreview(); await LoadModels(lifetime.Token); }
				catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
				catch (Exception ex) { Failure(ex); }
			};
			dialog.Closing += (_, _) => { lifetime.Cancel(); request?.Cancel(); };
			consent.Checked += (_, _) => UpdateEnabled(); consent.Unchecked += (_, _) => UpdateEnabled();
			models.SelectionChanged += (_, _) =>
			{
				if (models.SelectedItem is string model) Windows.Storage.ApplicationData.Current.LocalSettings.Values["NaiAi.Model"] = model;
				consent.IsChecked = false;
				UpdateEnabled();
			};
			foreach (var key in actionOrder)
			{
				var operation = operations.First(op => op.Key == key);
				var symbol = key switch
				{
					"NaiActionSearch" => Symbol.Find, "NaiActionOrganize" => Symbol.Folder,
					"NaiActionName" => Symbol.Rename, "NaiActionTags" => Symbol.Tag,
					"NaiActionCompare" => Symbol.Switch, "NaiActionQuestion" => Symbol.Message,
					"NaiActionKeyPoints" => Symbol.List, "NaiActionTasks" => Symbol.Accept,
					"NaiActionTranslate" => Symbol.World, "NaiActionExplain" => Symbol.Help, _ => Symbol.Document,
				};
				var content = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
				content.Children.Add(new SymbolIcon(symbol));
				content.Children.Add(new TextBlock { Text = L(key), TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
				var card = new ToggleButton { Content = content, MinHeight = 92, Padding = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsChecked = key == selectedOperation.Key };
				AutomationProperties.SetAutomationId(card, key);
				AutomationProperties.SetName(card, L(key));
				card.Click += async (_, _) =>
				{
					if (busy) return;
					selectedOperation = operation;
					foreach (var button in actionButtons) button.IsChecked = ReferenceEquals(button, card);
					actionTitle.Text = L(key); generate.Content = L(key);
					query.Visibility = runSearch.Visibility = Visibility.Collapsed;
					try { await LoadPreview(); } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { } catch (Exception ex) { Failure(ex); }
				};
				actionButtons.Add(card); choice.Children.Add(card);
			}
			void ArrangeActions(double width)
			{
				var columns = width >= 780 ? 3 : width >= 480 ? 2 : 1;
				choice.ColumnDefinitions.Clear(); choice.RowDefinitions.Clear();
				for (var i = 0; i < columns; i++) choice.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
				for (var i = 0; i < (actionButtons.Count + columns - 1) / columns; i++) choice.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
				for (var i = 0; i < actionButtons.Count; i++) { Grid.SetRow(actionButtons[i], i / columns); Grid.SetColumn(actionButtons[i], i % columns); }
			}
			ArrangeActions(Math.Min(root.Size.Width - 120, 960));
			choice.SizeChanged += (_, args) => { if (Math.Abs(args.NewSize.Width - args.PreviousSize.Width) > 1) ArrangeActions(args.NewSize.Width); };
			connect.Click += async (_, _) =>
			{
				using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
				request = cancellation;
				Busy(true);
				try { await NaiAiClient.PairAsync(cancellation.Token); await LoadModels(cancellation.Token); }
				catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { } catch (Exception ex) { Failure(ex); }
				finally { request = null; Busy(false); }
			};
			disconnect.Click += (_, _) => { NaiAiClient.Disconnect(); models.Items.Clear(); consent.IsChecked = false; status.Text = L("NaiAiUnavailable"); UpdateEnabled(); };
			unread.Click += async (_, _) =>
			{
				foreach (var item in selection.Where(item => NaiDocumentHistory.IsDocument(item.ItemPath)))
					await NaiDocumentHistory.MarkUnopenedAsync(item.ItemPath!);
			};
			copy.Click += (_, _) => { var package = new DataPackage(); package.SetText(result.Text); Clipboard.SetContent(package); };
			cancel.Click += (_, _) => request?.Cancel();
			generate.Click += async (_, _) =>
			{
				if (busy || consent.IsChecked != true || models.SelectedItem is not string model) return;
				var op = selectedOperation;
				using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
				request = cancellation;
				Busy(true); result.Text = string.Empty; copy.IsEnabled = false;
				status.Text = string.Empty;
				query.Visibility = runSearch.Visibility = Visibility.Collapsed;
				try
				{
					var names = selection.Select(item => item.ItemNameRaw ?? Path.GetFileName(item.ItemPath) ?? string.Empty).ToArray();
					var fileNames = currentFolder.Where(item => !item.IsFolder).Select(item => item.ItemNameRaw ?? Path.GetFileName(item.ItemPath) ?? string.Empty).ToArray();
					var folderNames = currentFolder.Where(item => item.IsFolder).Select(item => item.ItemNameRaw ?? string.Empty).ToArray();
					string? validated = null;
					var validationProblem = string.Empty;
					for (var attempt = 0; attempt < 2; attempt++)
					{
						var instruction = op.Instruction + (op.Key == "NaiActionSearch" ? $" Today is {DateTimeOffset.Now:yyyy-MM-dd}." : string.Empty) + (attempt == 0 ? string.Empty :
							" Your previous response failed validation: " + validationProblem + ". Return exactly the specified JSON schema, include every required source, retain all original numeric values, and use no additional prose.");
						var answer = await NaiAiClient.GenerateAsync(model, instruction, prompt.Text, preview.Text,
							AppLanguageHelper.PreferredLanguage.Code, cancellation.Token);
						try
						{
							validated = op.Key switch
							{
								"NaiActionSearch" => NaiAiResultValidator.BuildSearchQuery(answer),
								"NaiActionName" => NaiAiResultValidator.ValidateRename(answer, names, fileNames.Concat(folderNames).ToArray(), string.Join("\n", approvedExcerpts.Values) + "\n" + string.Join("\n", names) + "\n" + prompt.Text),
								"NaiActionTranslate" => NaiAiResultValidator.ValidateTranslation(answer, approvedExcerpts),
								"NaiActionTags" => NaiAiResultValidator.ValidateTags(answer, names),
								"NaiActionOrganize" => NaiAiResultValidator.ValidateOrganization(answer, fileNames, folderNames),
								_ => answer,
							};
							break;
						}
						catch (FormatException ex) when (attempt == 0) { validationProblem = ex.Message; }
					}
					result.Text = validated ?? throw new FormatException("NaiAiInvalidResponse".GetLocalizedResource());
					copy.IsEnabled = !string.IsNullOrWhiteSpace(result.Text);
					if (op.Key == "NaiActionSearch")
					{
						query.Text = result.Text.Trim();
						query.Visibility = runSearch.Visibility = Visibility.Visible;
					}
				}
				catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
				catch (FormatException) { Failure(new FormatException("NaiAiInvalidResponse".GetLocalizedResource())); }
				catch (Exception ex) { Failure(ex); }
				finally { request = null; Busy(false); consent.IsChecked = false; }
			};
			runSearch.Click += (_, _) =>
			{
				if (string.IsNullOrWhiteSpace(query.Text) || query.Text.Contains('\n') || query.Text.Contains('\r')) return;
				// Reviewed input is only passed to the existing search parser, never a shell.
				context.ShellPage?.SubmitSearch(query.Text.StartsWith('$') ? query.Text : "$" + query.Text);
				dialog.Hide();
			};
			await dialog.ShowAsync();
		}

		private static async Task<PreviewSnapshot> PreviewAsync(IEnumerable<ListedItem> selection, bool includeText, CancellationToken token)
		{
			var builder = new StringBuilder();
			var excerpts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (!includeText) builder.AppendLine("FLAT LISTING: all entries are direct siblings; subfolder contents are unknown. Suggestions are not facts about existing organization.");
			foreach (var item in selection)
			{
				token.ThrowIfCancellationRequested();
				if (builder.Length >= 64000) break;
				builder.AppendLine($"FILE: {item.ItemNameRaw}");
				builder.AppendLine($"IS_DIRECTORY: {item.IsFolder}");
				// The model does not need the user's account name, drive/share root,
				// or absolute path. Those remain local to the file manager.
				builder.AppendLine($"TYPE: {item.ItemType}; SIZE: {item.FileSize}");
				if (includeText && item.ItemPath is string path && NaiDocumentReader.Supports(path))
				{
					try
					{
						if ((File.GetAttributes(path) & FileAttributes.Offline) != 0) continue;
						var limit = Math.Min(16000, Math.Max(0, 64000 - builder.Length - 100));
						if (limit == 0) break;
						var excerpt = await NaiDocumentReader.ReadAsync(path, limit, token);
						builder.AppendLine($"EXCERPT ({excerpt.Length} chars; maximum {limit}):");
						builder.AppendLine(excerpt);
						excerpts[item.ItemNameRaw ?? Path.GetFileName(path)] = excerpt;
					}
					catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
					{ builder.AppendLine(L("NaiAiNoSelection")); }
				}
				builder.AppendLine();
			}
			return new PreviewSnapshot(builder.ToString(0, Math.Min(builder.Length, 64000)), excerpts);
		}
	}
}
