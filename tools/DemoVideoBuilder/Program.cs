using System.Diagnostics;

// Transcodes the Playwright walkthrough recording (tests/demo-video.spec.ts → video.webm) into a
// broadly-compatible MP4 (H.264 / yuv420p, +faststart) using ffmpeg. Pure C# + the ffmpeg CLI.
//
// Usage: dotnet run --project tools/DemoVideoBuilder -- [<recordingsDir>] [<outputMp4>]
//   recordingsDir default: frontend/test-results-video
//   outputMp4     default: docs/foundry-local-demo.mp4

var recordingsDir = args.Length > 0 ? args[0] : Path.Combine("frontend", "test-results-video");
var outputMp4 = args.Length > 1 ? args[1] : Path.Combine("docs", "foundry-local-demo.mp4");

if (!Directory.Exists(recordingsDir))
{
    Console.Error.WriteLine($"Recordings directory not found: {recordingsDir}");
    return 1;
}

// Playwright writes one video.webm per test under a sanitized sub-folder; pick the newest.
var webm = new DirectoryInfo(recordingsDir)
    .EnumerateFiles("*.webm", SearchOption.AllDirectories)
    .OrderByDescending(f => f.LastWriteTimeUtc)
    .FirstOrDefault();

if (webm is null)
{
    Console.Error.WriteLine($"No .webm recording found under {recordingsDir}. Run the demo-video spec first.");
    return 1;
}

Console.WriteLine($"Source recording : {webm.FullName} ({webm.Length / 1024.0 / 1024.0:F1} MB)");

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputMp4))!);

// Re-encode to H.264 yuv420p so the MP4 plays everywhere; pad odd dimensions (libx264 needs even),
// normalize to 30 fps, and move the moov atom to the front for instant streaming/seeking.
var ffArgs = new[]
{
    "-y",
    "-i", webm.FullName,
    "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2,fps=30",
    "-c:v", "libx264",
    "-pix_fmt", "yuv420p",
    "-crf", "23",
    "-preset", "medium",
    "-movflags", "+faststart",
    "-an",
    outputMp4,
};

var psi = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
foreach (var a in ffArgs) psi.ArgumentList.Add(a);

Console.WriteLine($"Transcoding      : ffmpeg {string.Join(' ', ffArgs)}");
using var proc = Process.Start(psi);
if (proc is null) { Console.Error.WriteLine("Failed to start ffmpeg (is it on PATH?)."); return 1; }

var stderr = await proc.StandardError.ReadToEndAsync();
await proc.WaitForExitAsync();
if (proc.ExitCode != 0)
{
    Console.Error.WriteLine("ffmpeg failed:");
    Console.Error.WriteLine(stderr[Math.Max(0, stderr.Length - 1500)..]);
    return proc.ExitCode;
}

var outInfo = new FileInfo(outputMp4);
Console.WriteLine($"Wrote MP4        : {outInfo.FullName} ({outInfo.Length / 1024.0 / 1024.0:F1} MB)");
return 0;
