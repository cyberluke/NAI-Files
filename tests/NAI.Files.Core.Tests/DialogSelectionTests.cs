using Files.App.Helpers;
using Files.App.Utils.CommandLine;

internal static class DialogSelectionTests
{
	public static int Run()
	{
		var count = 0;
		void Check(bool passed, string name)
		{
			if (!passed) throw new InvalidOperationException(name);
			Console.WriteLine($"PASS {name}");
			count++;
		}

		var root = Path.Combine(Path.GetTempPath(), "nai-dialog-test-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var child = Directory.CreateDirectory(Path.Combine(root, "příliš žluťoučký folder")).FullName;
			var file = Path.Combine(root, "výběr file.txt");
			var second = Path.Combine(child, "other.txt");
			File.WriteAllText(file, "first");
			File.WriteAllText(second, "second");
			DialogSelectionResult Resolve(string mode, string? input, params string[] selection)
				=> DialogSelectionResolver.Resolve(mode, root, input, selection);

			Check(Resolve("open", null, file).Paths.SequenceEqual([file]), "selected Unicode file returns exact absolute path");
			var literal = Path.Combine(root, "%USERNAME%.txt");
			File.WriteAllText(literal, "literal name");
			File.WriteAllText(Environment.ExpandEnvironmentVariables(literal), "different file");
			Check(Resolve("open", null, literal).Paths.SequenceEqual([literal]), "selected percent-variable filename is literal, never expanded to a different file");
			Check(Resolve("open", "\"" + file + "\"").Paths.SequenceEqual([file]), "quoted absolute input enables and resolves Open");
			Check(Resolve("open", Path.GetFileName(file)).Paths.SequenceEqual([file]), "typed relative filename resolves in active pane");
			Check(Resolve("open", new Uri(file).AbsoluteUri).Paths.SequenceEqual([file]), "file URI input resolves correctly");
			Check(Resolve("open", "missing.txt", file) is { CanSubmit: false }, "invalid typed override never returns a stale selected file");
			Check(Resolve("open", null) is { CanSubmit: false }, "empty open request cannot close picker");
			Check(Resolve("open", child) is { NavigateTo: not null, Paths.Count: 0 }, "typed directory navigates instead of returning a file result");
			Check(Resolve("open", null, child).NavigateTo == child, "selected directory Open navigates");
			Check(Resolve("folder", child).Paths.SequenceEqual([child]), "folder picker returns typed directory");
			Check(Resolve("folder", null).Paths.SequenceEqual([root]), "folder picker accepts current pane directory");
			Check(Resolve("folder", "missing", child) is { CanSubmit: false }, "invalid typed folder never silently returns old selection");
			Check(Resolve("multi", null, file, second).Paths.SequenceEqual([file, second]), "multi-open preserves selected absolute paths");
			Check(Resolve("multi", $"\"{file}\" \"{second}\"").Paths.SequenceEqual([file, second]), "quoted multi-input preserves spaces and Unicode");
			Check(Resolve("multi", null, file, file).Paths.SequenceEqual([file]), "duplicate file selection is deduplicated");
			Check(Resolve("open", null, file, second) is { CanSubmit: false }, "single-open cannot emit multiple files");
			var save = Path.Combine(root, "new document.txt");
			Check(Resolve("save", "new document.txt").Paths.SequenceEqual([save]) && !File.Exists(save), "save returns new filename without precreating or truncating it");
			Check(Resolve("save", file).Paths.SequenceEqual([file]) && File.ReadAllText(file) == "first", "save validation never changes existing file content");
			Check(Resolve("save", Path.Combine(root, "absent", "file.txt")) is { CanSubmit: false }, "save rejects missing parent folder");
			Check(Resolve("save", "bad*.txt") is { CanSubmit: false }, "save rejects wildcard filename");
			Check(Resolve("save", child).NavigateTo == child, "save typed folder navigates without confirming it as a filename");
			Check(!DialogSelectionResolver.Resolve("open", null, "relative.txt", []).CanSubmit, "relative path never uses process working directory as fallback");
			Check(!Resolve("open", "bad\0path").CanSubmit, "invalid input does not throw through UI callback");

			var response = Path.Combine(root, "request.txt");
			var otherResponse = Path.Combine(root, "other-request.txt");
			using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, DialogResultWriter.EventName(response));
			using var otherSignal = new EventWaitHandle(false, EventResetMode.AutoReset, DialogResultWriter.EventName(otherResponse));
			var writer = new DialogResultWriter(response);
			Check(writer.TryComplete([file, second], out _) && signal.WaitOne(1000), "result writer closes and signals matching request");
			Check(File.ReadAllLines(response).SequenceEqual([file, second]), "result writer roundtrips Unicode absolute paths as UTF-8 lines");
			Check(!otherSignal.WaitOne(0), "completion cannot wake another open dialog");
			Check(!File.ReadAllBytes(response).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "native result has no UTF-8 BOM prefix");
			File.Delete(response);
			Check(writer.TryComplete([], out _) && !signal.WaitOne(0) && !File.Exists(response), "Window_Closed cannot recreate or re-signal consumed success");
			Check(new DialogResultWriter(otherResponse).TryComplete([], out _) && otherSignal.WaitOne(1000)
				&& File.ReadAllLines(otherResponse).Length == 0, "cancel signals its caller promptly with an empty result");
			var retryWriter = new DialogResultWriter(response);
			using (var locked = File.Open(response, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
				Check(!retryWriter.TryComplete([file], out _) && !signal.WaitOne(0), "write failure does not signal a bogus result or seal the request");
			Check(retryWriter.TryComplete([file], out _) && signal.WaitOne(1000), "failed delivery can be retried without losing selection");

			var commands = CommandLineParser.ParseUntrustedCommands($"files-dev.exe -outputpath \"{response}\" -mode \"save\" -select \"-draft.txt\" -directory \"{root}\"");
			Check(commands.Single(command => command.Type == ParsedCommandType.SelectItem).Payload == "-draft.txt", "quoted leading-hyphen suggested filename is not parsed as a switch");
			Check(commands.Single(command => command.Type == ParsedCommandType.OutputPath).Payload == response, "output request survives command parsing with spaces");
			Check(commands.Single(command => command.Type == ParsedCommandType.DialogMode).Payload == "save", "dialog mode has no stray quote or terminal NUL");
			Check(commands.Single(command => command.Type == ParsedCommandType.OpenDirectory).Payload == root, "initial directory survives command parsing");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
		return count;
	}
}
