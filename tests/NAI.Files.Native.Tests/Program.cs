using Files.App.Helpers;
using System.Diagnostics;

static void Check(bool value, string message)
{
	if (!value) throw new InvalidOperationException(message);
	Console.WriteLine("PASS " + message);
}

if (args.Contains("--semantic-fixture"))
{
	var semanticRoot = Path.Combine(Path.GetTempPath(), "kilo", "nai-files-semantic-" + Guid.NewGuid().ToString("N"));
	Directory.CreateDirectory(semanticRoot);
	await File.WriteAllTextAsync(Path.Combine(semanticRoot, "0001.txt"), "Service receipt R-1042. The bicycle workshop repaired the front brakes and replaced worn brake pads. Total paid: CZK 840. This is a synthetic test document.");
	await File.WriteAllTextAsync(Path.Combine(semanticRoot, "0002.txt"), "Vegetable garden schedule. Water the tomato seedlings every morning, inspect soil moisture and add compost. This is a synthetic test document.");
	await File.WriteAllTextAsync(Path.Combine(semanticRoot, "0003.txt"), "Concert rehearsal plan. Dancers practice their stage formation, music timing and lighting cues. This is a synthetic test document.");
	Console.WriteLine("SEMANTIC_FIXTURE_ROOT=" + semanticRoot);
	return;
}

using (var sampler = new NaiSystemTelemetry())
{
	var initial = sampler.Sample();
	Check(initial.Cpu is null, "Real CPU first sample is unknown");
	await Task.Delay(1200);
	var sample = sampler.Sample();
	Check(sample.Cpu is >= 0 and <= 100, "Real Windows CPU delta is measured");
	Check(sample.RamTotal is > 0 && sample.RamUsed <= sample.RamTotal, "Real physical RAM sample is bounded");
	Check(sample.DiskTotal is > 0 && sample.DiskFree <= sample.DiskTotal, "Real system-volume capacity is measured");
	Check(sample.Gpu.All(device => device.Percent is >= 0 and <= 100), "GPU engine aggregation is bounded");
	if (args.Contains("--require-gpu")) Check(sample.Gpu.Length > 0, "Installed GPU counter provider supplies actual adapter samples");
	Console.WriteLine($"MEASURED at={sample.At:O} CPU={sample.Cpu:F2}% RAM={sample.RamUsed}/{sample.RamTotal} GPU-adapters={sample.Gpu.Length} NPU-adapters={sample.Npu.Length} WiFi-bars={sample.WifiBars?.ToString() ?? "unavailable"}");
	foreach (var device in sample.Gpu) Console.WriteLine($"GPU {device.Adapter} peak-engine={device.Percent:F2}%");
	foreach (var device in sample.Npu) Console.WriteLine($"NPU {device.Adapter} peak-engine={device.Percent:F2}%");
	if (sample.Npu.Length == 0) Console.WriteLine("NPU: no usable Windows NPU Engine counter sample; no value inferred from reservations or GPU compute engines.");
}

var root = Path.Combine(Path.GetTempPath(), "kilo", "nai-files-native-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
	var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aXZ0AAAAASUVORK5CYII=");
	for (var index = 0; index < 10001; index++) File.WriteAllBytes(Path.Combine(root, $"fixture-{index:D5}.png"), png);
	var times = new List<double>();
	for (var attempt = 0; attempt < 3; attempt++)
	{
		var clock = Stopwatch.StartNew();
		var snapshot = await NaiFileWorkspace.SnapshotAsync(root, CancellationToken.None);
		times.Add(clock.Elapsed.TotalMilliseconds);
		Check(snapshot.Items.Length == 10000 && snapshot.Truncated && snapshot.Skipped == 0, "Real 10,001-file folder enumeration is bounded and reports truncation");
	}
	Console.WriteLine($"BENCH actual-folder entries=10001 median-ms={times.Order().ElementAt(1):F1} worst-ms={times.Max():F1}");
	using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
	var observedCancellation = false;
	try { await NaiFileWorkspace.SnapshotAsync(root, cancelled.Token); } catch (OperationCanceledException) { observedCancellation = true; }
	Check(observedCancellation, "Folder enumeration respects cancellation");
	if (args.Contains("--keep-fixture")) Console.WriteLine("SYNTHETIC_FIXTURE=" + root);
}
finally
{
	if (!args.Contains("--keep-fixture")) Directory.Delete(root, recursive: true);
}
