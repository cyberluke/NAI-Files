// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;

namespace Files.App.Helpers
{
	internal enum DialogSelectionError { None, Empty, InvalidPath, MissingFile, MissingFolder, InvalidName, UnsupportedType, AccessDenied, PathTooLong }

	internal sealed record DialogSelectionResult(IReadOnlyList<string> Paths, string? NavigateTo = null,
		DialogSelectionError Error = DialogSelectionError.None, string? Detail = null)
	{
		public bool CanSubmit => Paths.Count > 0 || NavigateTo is not null;
		public static DialogSelectionResult Invalid { get; } = new(Array.Empty<string>(), Error: DialogSelectionError.InvalidPath);
		public static DialogSelectionResult Failure(DialogSelectionError error, string? detail = null) => new(Array.Empty<string>(), Error: error, Detail: detail);
	}

	/// <summary>One resolution policy for picker button state and the result sent to the caller.</summary>
	internal static class DialogSelectionResolver
	{
		public static DialogSelectionResult Resolve(string? mode, string? folder, string? input, IEnumerable<string> selection)
		{
			try
			{
				var typed = !string.IsNullOrWhiteSpace(input);
				var paths = (typed ? SplitInput(input!, mode == "multi") : selection)
					.Select(path => typed ? ResolvePath(path, folder)
						: Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null)
					.ToArray();
				if (paths.Any(path => path is null))
					return DialogSelectionResult.Invalid;

				var resolved = paths.Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
				if (mode == "folder")
				{
					if (resolved.Length == 0 && !string.IsNullOrWhiteSpace(folder) && Path.IsPathFullyQualified(folder))
						resolved = [Path.GetFullPath(folder)];
					return resolved.Length == 1 && Directory.Exists(resolved[0])
						? new(resolved) : DialogSelectionResult.Failure(DialogSelectionError.MissingFolder, resolved.FirstOrDefault());
				}

				// Enter/Open on a folder navigates; it never sends a directory to a file-open caller.
				if (resolved.Length == 1 && Directory.Exists(resolved[0]))
					return new(Array.Empty<string>(), resolved[0]);
				if (resolved.Length == 0) return DialogSelectionResult.Failure(DialogSelectionError.Empty);
				if (mode != "multi" && resolved.Length != 1)
					return DialogSelectionResult.Invalid;

				if (mode == "save")
				{
					var name = Path.GetFileName(resolved[0]);
					if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
						return DialogSelectionResult.Failure(DialogSelectionError.InvalidName, name);
					var parent = Path.GetDirectoryName(resolved[0]);
					return !string.IsNullOrEmpty(parent) && (File.GetAttributes(parent) & FileAttributes.Directory) != 0
						? new(resolved) : DialogSelectionResult.Failure(DialogSelectionError.MissingFolder, Path.GetDirectoryName(resolved[0]));
				}

				foreach (var path in resolved)
					if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
						return DialogSelectionResult.Failure(DialogSelectionError.MissingFile, path);
				return new(resolved);
			}
			catch (UnauthorizedAccessException ex) { return DialogSelectionResult.Failure(DialogSelectionError.AccessDenied, ex.Message); }
			catch (PathTooLongException ex) { return DialogSelectionResult.Failure(DialogSelectionError.PathTooLong, ex.Message); }
			catch (FileNotFoundException ex) { return DialogSelectionResult.Failure(DialogSelectionError.MissingFile, ex.FileName); }
			catch (DirectoryNotFoundException ex) { return DialogSelectionResult.Failure(DialogSelectionError.MissingFolder, ex.Message); }
			catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
			{
				return DialogSelectionResult.Invalid;
			}
		}

		public static string? ResolvePath(string input, string? folder)
		{
			var path = Environment.ExpandEnvironmentVariables(input.Trim().Trim('"'));
			if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
				return null;
			if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile)
				path = uri.LocalPath;
			if (Path.IsPathFullyQualified(path))
				return Path.GetFullPath(path);
			return !string.IsNullOrWhiteSpace(folder) && Path.IsPathFullyQualified(folder)
				? Path.GetFullPath(path, folder) : null;
		}

		private static IEnumerable<string> SplitInput(string input, bool multiple)
		{
			var trimmed = input.Trim();
			if (!multiple || !trimmed.StartsWith('"'))
				return new[] { trimmed };
			var parts = trimmed.Split('"');
			return parts.Where((part, index) => index % 2 == 1 || !string.IsNullOrWhiteSpace(part));
		}
	}
}
