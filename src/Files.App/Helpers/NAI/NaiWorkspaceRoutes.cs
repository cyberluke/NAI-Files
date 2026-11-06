// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Helpers
{
	internal static class NaiWorkspaceRoutes
	{
		public const string Vision = "NaiVision";
		public const string Workbench = "NaiFilesWorkspace";
		public const string Runtime = "NaiRuntime";
		public const string Mail = "NaiEmail";
		public const string Semantic = "NaiSemantic";
		public static bool IsRoute(string? path) => path is Vision or Workbench or Runtime or Mail or Semantic;
		public static string Title(string route) => (route switch { Vision => "NaiVisionTitle", Runtime => "NaiRuntimeTitle", Mail => "NaiEmailTitle", Semantic => "NaiSemanticTitle", _ => "NaiWorkspaceTitle" }).GetLocalizedResource();
		public static Type PageType(string route) => route switch { Vision => typeof(Files.App.Views.NaiVisionPage), Runtime => typeof(Files.App.Views.NaiRuntimePage), Mail => typeof(Files.App.Views.NaiEmailPage), Semantic => typeof(Files.App.Views.NaiSemanticPage), _ => typeof(Files.App.Views.NaiWorkspacePage) };
		public static void OpenVision(string? folder = null, string? query = null) => Open(Vision, folder, query);
		public static void Open(string route, string? folder = null, string? query = null)
		{
			var pane = Ioc.Default.GetRequiredService<IMultiPanesContext>().ActivePane;
			if (pane is null) return;
			OpenInPane(pane, route, folder, query);
		}
		public static void OpenInPane(IShellPage pane, string route, string? folder = null, string? query = null)
		{
			if (!IsRoute(route)) throw new ArgumentException("Unknown workspace route");
			folder ??= pane.ShellViewModel?.WorkingDirectory;
			pane.NavigateWithArguments(PageType(route), new NavigationArguments
			{
				NavPathParam = route, AssociatedTabInstance = pane, SelectItems = (string[])[folder ?? string.Empty, query ?? string.Empty],
			});
		}
		public static async Task PrepareAsync(IShellPage host, string route)
		{
			var state = host.InstanceViewModel;
			state.IsPageTypeNotHome = true; state.IsPageTypeSearchResults = false; state.IsPageTypeMtpDevice = false;
			state.IsPageTypeRecycleBin = false; state.IsPageTypeCloudDrive = false; state.IsPageTypeFtp = false;
			state.IsPageTypeZipFolder = false; state.IsPageTypeLibrary = false; state.GitRepositoryPath = null;
			state.IsGitRepository = false; state.IsPageTypeReleaseNotes = false; state.IsPageTypeSettings = true;
			host.ToolbarViewModel.CanRefresh = false; host.ToolbarViewModel.CanNavigateToParent = false;
			host.ToolbarViewModel.CanGoBack = host.CanNavigateBackward; host.ToolbarViewModel.CanGoForward = host.CanNavigateForward;
			await host.GetRequiredShellViewModel().SetWorkingDirectoryAsync(route);
			host.ToolbarViewModel.PathComponents.Clear();
			host.ToolbarViewModel.PathComponents.Add(new PathBoxItem { Title = Title(route), Path = route });
		}

		public static void SearchInPane(IShellPage pane, string folder, string query)
		{
			folder = NaiSemanticProtocol.CanonicalFolder(folder);
			if (!System.IO.Directory.Exists(folder)) throw new System.IO.DirectoryNotFoundException(folder);
			pane.GetRequiredShellViewModel().CancelSearch();
			pane.InstanceViewModel.CurrentSearchQuery = query;
			var layout = pane.InstanceViewModel.FolderSettings.GetLayoutType(folder);
			if (layout == typeof(Files.App.Views.Layouts.ColumnsLayoutPage)) layout = typeof(Files.App.Views.Layouts.DetailsLayoutPage);
			pane.NavigateWithArguments(layout, new NavigationArguments
			{
				AssociatedTabInstance = pane, NavPathParam = folder, IsSearchResultPage = true, SearchPathParam = folder, SearchQuery = query,
			});
		}
	}
}
