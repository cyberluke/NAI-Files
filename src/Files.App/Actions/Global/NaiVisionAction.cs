// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;

namespace Files.App.Actions
{
	[GeneratedRichCommand]
	internal sealed partial class NaiVisionAction : ObservableObject, IAction
	{
		private readonly IContentPageContext context = Ioc.Default.GetRequiredService<IContentPageContext>();
		public string Label => "NaiVisionTitle".GetLocalizedResource();
		public string Description => "NaiVisionDescription".GetLocalizedResource();
		public ActionCategory Category => ActionCategory.Open;
		public RichGlyph Glyph => new(themedIconStyle: "App.ThemedIcons.Omnibar.Search");
		public bool IsExecutable => context.ShellPage?.InstanceViewModel is { IsPageTypeFtp: false, IsPageTypeZipFolder: false, IsPageTypeRecycleBin: false };
		public NaiVisionAction() => context.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsExecutable));
		public Task ExecuteAsync(object? parameter = null)
		{
			if (!IsExecutable) return Task.CompletedTask;
			var folder = context.ShellPage?.ShellViewModel?.WorkingDirectory;
			NaiWorkspaceRoutes.OpenVision(folder is not null && Directory.Exists(folder) ? folder : null);
			return Task.CompletedTask;
		}
	}
}
