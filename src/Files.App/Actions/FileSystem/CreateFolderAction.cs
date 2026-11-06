// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace Files.App.Actions
{
	[GeneratedRichCommand]
	internal sealed partial class CreateFolderAction : BaseUIAction, IAction
	{
		private readonly IContentPageContext context;
		private bool isCreating;

		public string Label
			=> Strings.Folder.GetLocalizedResource();

		public string Description
			=> Strings.CreateFolderDescription.GetLocalizedResource();

		public ActionCategory Category
			=> ActionCategory.Create;

		public string AccessKey
			=> "F";

		public string AutomationId
			=> "InnerNavigationToolbarNewFolderButton";

		public HotKey HotKey
			=> new(Keys.N, KeyModifiers.CtrlShift);

		public RichGlyph Glyph
			=> new(baseGlyph: "\uE8B7");

		public override bool IsExecutable =>
			!isCreating && context.CanCreateItem &&
			UIHelpers.CanShowDialog;

		public CreateFolderAction()
		{
			context = Ioc.Default.GetRequiredService<IContentPageContext>();

			context.PropertyChanged += Context_PropertyChanged;
		}

		public async Task ExecuteAsync(object? parameter = null)
		{
			if (!IsExecutable || context.ShellPage is not { } pane)
				return;
			isCreating = true;
			OnPropertyChanged(nameof(IsExecutable));
			try
			{
				await UIFilesystemHelpers.CreateFileFromDialogResultTypeAsync(AddItemDialogItemType.Folder, null, pane);
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Folder creation failed");
				if (App.AppModel.IsMainWindowClosed) return;
				if (App.IsDialogMode)
					Ioc.Default.GetRequiredService<MainPageViewModel>().ReportDialogError(ex.Message);
				else
					await DialogDisplayHelper.ShowDialogAsync(Strings.NewFolder.GetLocalizedResource(), ex.Message);
			}
			finally
			{
				isCreating = false;
				OnPropertyChanged(nameof(IsExecutable));
			}
		}

		private void Context_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			switch (e.PropertyName)
			{
				case nameof(IContentPageContext.CanCreateItem):
				case nameof(IContentPageContext.HasSelection):
					OnPropertyChanged(nameof(IsExecutable));
					break;
			}
		}
	}
}
