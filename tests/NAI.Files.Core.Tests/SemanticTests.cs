using Files.App.Helpers;
using System.Text.Json;

internal static class SemanticTests
{
	public static async Task<int> RunAsync()
	{
		var count = 0;
		void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); count++; Console.WriteLine("PASS " + message); }
		void Reject(Action action, string message) { try { action(); } catch (Exception error) when (error is ArgumentException or FormatException or InvalidDataException) { Check(true, message); return; } throw new InvalidOperationException(message); }
		var root = Path.Combine(Path.GetTempPath(), "kilo", "nai-semantic-test-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var folder = Path.Combine(root, "scope"); Directory.CreateDirectory(folder);
			var file = Path.Combine(folder, "neutral-name.txt"); await File.WriteAllTextAsync(file, "A synthetic bicycle brake repair receipt.");
			var info = new FileInfo(file);
			var dataset = NaiSemanticProtocol.Dataset(folder); var fileId = NaiSemanticProtocol.FileId(info.Name); var chunkId = fileId + "-version-0";
			Check(dataset.Length == 73 && dataset == NaiSemanticProtocol.Dataset(folder + Path.DirectorySeparatorChar), "Semantic folder identity is stable without leaking an absolute path");
			Check(NaiSemanticProtocol.TryParse("files: SeMaNtIc: oprava kola", out var query) && query == "oprava kola", "Semantic routing is explicit and retains the user's language");
			Check(!NaiSemanticProtocol.TryParse("semantic-report.txt", out _), "Filename search remains local without the explicit semantic prefix");
			Reject(() => NaiSemanticProtocol.CanonicalFolder("relative-folder"), "Semantic scope must be an explicit local folder");
			Check(NaiSemanticIndexStore.ResolvePath(folder, "../foreign.txt") is null && NaiSemanticIndexStore.ResolvePath(folder, file) is null, "Index mapping rejects traversal and backend-supplied absolute paths");
			Check(NaiSemanticIndexStore.ResolvePath(Path.GetPathRoot(folder)!, "synthetic.txt") is not null, "Root-volume containment does not introduce a double separator");
			using (var request = JsonDocument.Parse(NaiSemanticProtocol.SearchPayload(dataset, "oprava kola", (string[])[chunkId])))
			{
				Check(request.RootElement.GetProperty("app_id").GetString() == "app-nai-files" && request.RootElement.GetProperty("dataset").GetString() == dataset, "Semantic request is constrained to the Files-owned profile and chosen folder");
				Check(request.RootElement.GetProperty("rerank").GetBoolean() && request.RootElement.GetProperty("filter").GetProperty("must")[0].GetProperty("match").GetProperty("any")[0].GetString() == chunkId, "Semantic ranking is requested only for active locally mapped chunk IDs");
			}
			Reject(() => NaiSemanticProtocol.SearchPayload(dataset, "receipt", Array.Empty<string>()), "A missing index cannot trigger unscoped semantic retrieval");
			Reject(() => NaiSemanticProtocol.DeletePayload(dataset, Array.Empty<string>()), "Empty IDs cannot become dataset-wide deletion");
			using (var request = JsonDocument.Parse(NaiSemanticProtocol.UpsertPayload(dataset, (NaiSemanticChunk[])[new(chunkId, fileId, info.Name, "Synthetic bicycle repair receipt")])))
				Check(request.RootElement.GetProperty("items")[0].GetProperty("payload").GetProperty("file_id").GetString() == fileId
					&& !request.RootElement.GetRawText().Contains(folder), "Upsert sends approved text and opaque identity, not executable filesystem paths");
			JsonDocument Response(string scope, string profile = "app-nai-files", string device = "NPU") => JsonDocument.Parse(JsonSerializer.Serialize(new
			{
				app_id = "app-nai-files", profile, device, model = NaiSemanticProtocol.EmbeddingModel, instruction_applied = true, rerank_seconds = 0.2,
				results = new[] { new { id = chunkId, score = 0.9, payload = new { app_id = "app-nai-files", dataset = scope, file_id = fileId, source_kind = "files-document", text = "Synthetic bicycle repair receipt", path = "C:\\not-trusted.txt" } } },
			}));
			using (var reply = Response(dataset))
				Check(NaiSemanticProtocol.ParseSearch(reply.RootElement, dataset).Single() is { FileId: var returned, Score: 0.9 } && returned == fileId,
					"Semantic-only matches survive even when the filename has no lexical query match");
			using (var reply = Response(dataset, "default")) Reject(() => NaiSemanticProtocol.ParseSearch(reply.RootElement, dataset), "An owner default-profile fallback is rejected");
			// Device is not part of the retrieval protocol: Kelvin serves the same
			// profile/model on whatever execution device it placed, and the response
			// may carry no device field (or any label) without affecting validity.
			using (var reply = Response(dataset, device: "CPU"))
				Check(NaiSemanticProtocol.ParseSearch(reply.RootElement, dataset).Single() is { Score: 0.9 },
					"A CPU-served semantic response is valid (device is not part of the contract)");
			using (var reply = JsonDocument.Parse("""{"app_id":"app-nai-files","profile":"app-nai-files","model":"Qwen3-Embedding-0.6B-int4","instruction_applied":true,"rerank_seconds":0.2,"results":[]}"""))
				Check(NaiSemanticProtocol.ParseSearch(reply.RootElement, dataset).Count == 0,
					"Semantic response without any device field is valid");
			using (var reply = Response("foreign-dataset")) Reject(() => NaiSemanticProtocol.ParseSearch(reply.RootElement, dataset), "Foreign folder-index results are rejected");
			var document = new NaiIndexedDocument(fileId, info.Name, info.Length, info.LastWriteTimeUtc.Ticks, new string('a', 64), [chunkId], 30, false, "fixture", DateTimeOffset.UtcNow);
			var state = new NaiSemanticIndexManifest(1, folder, dataset, false, [document], [], DateTimeOffset.UtcNow, false);
			var store = new NaiSemanticIndexStore(Path.Combine(root, "state"));
			using (store.AcquireWriter(folder))
			{
				await store.SaveAsync(state, CancellationToken.None);
				var refused = false; try { using var second = store.AcquireWriter(folder); } catch (IOException) { refused = true; }
				Check(refused, "A concurrent Files window cannot become a second index writer");
			}
			var restored = await new NaiSemanticIndexStore(Path.Combine(root, "state")).ReadAsync(folder, CancellationToken.None);
			Check(restored?.Documents.Single().ChunkIds.Single() == chunkId, "Current-version index mappings survive a fresh store instance");
			Check(NaiSemanticIndexStore.IsCurrentLocalFile(folder, document), "Search validates current local source metadata before returning a hit");
			await File.WriteAllTextAsync(file, "Different source version with a different length.");
			Check(!NaiSemanticIndexStore.IsCurrentLocalFile(folder, document), "Stale document versions cannot be presented as current semantic matches");
			var pendingId = fileId + "-interrupted-upload-0";
			await store.SaveAsync(state with { PendingDeleteIds = [pendingId], Partial = true }, CancellationToken.None);
			Check((await store.ReadAsync(folder, CancellationToken.None))!.PendingDeleteIds.Single() == pendingId, "Interrupted remote writes retain exact recoverable cleanup identities");
			var invalid = false;
			try { await store.SaveAsync(state with { PendingDeleteIds = [chunkId] }, CancellationToken.None); } catch (InvalidDataException) { invalid = true; }
			Check(invalid, "Cleanup cannot delete the active locally mapped content version");
			// A paired-but-unavailable retrieval backend is a known state, not an
			// opaque transport failure. Only the gateway's documented 502/503
			// retrieval shapes classify as such; everything else stays raw.
			Check(NaiAiProtocol.IsRetrievalUnavailable(503, "NPU retrieval backend not available; start the OpenVINO NPU service"),
				"Gateway 503 with no resolvable retrieval endpoint classifies as retrieval unavailable");
			Check(NaiAiProtocol.IsRetrievalUnavailable(502, "NPU retrieval backend unreachable"),
				"Gateway 502 with an unreachable retrieval backend classifies as retrieval unavailable");
			Check(!NaiAiProtocol.IsRetrievalUnavailable(401, "unauthorized"), "Auth failure never classifies as a retrieval outage");
			Check(!NaiAiProtocol.IsRetrievalUnavailable(400, "malformed request"), "Client validation errors stay raw");
			Check(!NaiAiProtocol.IsRetrievalUnavailable(503, "embeddings not available on any qualified provider"),
				"Non-retrieval 503 responses are not misclassified as an NPU outage");
			Check(!NaiAiProtocol.IsRetrievalUnavailable(503, null) && !NaiAiProtocol.IsRetrievalUnavailable(200, "ok"),
				"Unparseable or successful responses never trigger the fallback message");
			return count;
		}
		finally { Directory.Delete(root, recursive: true); }
	}
}
