using System.Diagnostics;

// Transcodes the Playwright walkthrough recording(s) into one broadly-compatible MP4
// (H.264 / yuv420p, +faststart) using ffmpeg. Pure C# + the ffmpeg CLI.
//
// - One .webm under <recordingsDir>  -> transcode it directly.
// - Several .webm (e.g. segmented runs) -> normalize each to a uniform intermediate, then concat
//   them in filename order into a single MP4.
//
// Usage: dotnet run --project tools/DemoVideoBuilder -- [<recordingsDir>] [<outputMp4>]
//   recordingsDir default: frontend/video-segments
//   outputMp4     default: docs/foundry-local-demo.mp4

var recordingsDir = args.Length > 0 ? args[0] : Path.Combine("frontend", "video-segments");
var outputMp4 = args.Length > 1 ? args[1] : Path.Combine("docs", "foundry-local-demo.mp4");
// Optional playback speed (e.g. 2.0 = 2x faster) to compress a long matrix walkthrough.
var speed = args.Length > 2 && double.TryParse(args[2], System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 1.0;

if (!Directory.Exists(recordingsDir))
{
    Console.Error.WriteLine($"Recordings directory not found: {recordingsDir}");
    return 1;
}

// All recordings, in filename order (segments are named 01-..., 02-... so they concat in order).
var webms = new DirectoryInfo(recordingsDir)
    .EnumerateFiles("*.webm", SearchOption.AllDirectories)
    .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
    .ToList();

if (webms.Count == 0)
{
    Console.Error.WriteLine($"No .webm recording found under {recordingsDir}. Run the demo-video spec first.");
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputMp4))!);

// Uniform encode settings: optional speed-up, pad to even dims, normalize to 1280x720 / 30 fps,
// H.264 yuv420p so every segment is identical and they can be concatenated without re-encoding the
// joined stream. crf 28 + veryfast keeps the (mostly static) screencast small and quick to encode.
static string[] EncodeArgs(string input, string output, double speed)
{
    var setpts = Math.Abs(speed - 1.0) < 1e-9
        ? ""
        : $",setpts=PTS/{speed.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    return
    [
        "-y", "-i", input,
        "-vf", $"scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2{setpts},fps=30",
        "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "28", "-preset", "veryfast", "-an", output,
    ];
}

static async Task<int> RunFfmpegAsync(string[] ffArgs)
{
    var psi = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
    foreach (var a in ffArgs) psi.ArgumentList.Add(a);
    using var proc = Process.Start(psi);
    if (proc is null) { Console.Error.WriteLine("Failed to start ffmpeg (is it on PATH?)."); return -1; }
    var stderr = await proc.StandardError.ReadToEndAsync();
    await proc.WaitForExitAsync();
    if (proc.ExitCode != 0)
        Console.Error.WriteLine(stderr[Math.Max(0, stderr.Length - 1500)..]);
    return proc.ExitCode;
}

Console.WriteLine($"Found {webms.Count} recording segment(s):");
foreach (var w in webms) Console.WriteLine($"  {w.FullName} ({w.Length / 1024.0 / 1024.0:F1} MB)");

if (webms.Count == 1)
{
    Console.WriteLine($"Transcoding single recording -> MP4 (speed {speed}x) ...");
    var code = await RunFfmpegAsync(EncodeArgs(webms[0].FullName, outputMp4, speed));
    if (code != 0) return code;
}
else
{
    var tmp = Path.Combine(recordingsDir, ".concat-tmp");
    Directory.CreateDirectory(tmp);
    var parts = new List<string>();
    for (var i = 0; i < webms.Count; i++)
    {
        var part = Path.Combine(tmp, $"part-{i:D2}.mp4");
        Console.WriteLine($"Normalizing segment {i + 1}/{webms.Count} (speed {speed}x) ...");
        var code = await RunFfmpegAsync(EncodeArgs(webms[i].FullName, part, speed));
        if (code != 0) return code;
        parts.Add(part);
    }

    // concat demuxer list (escaped, forward-slash paths)
    var listPath = Path.Combine(tmp, "concat.txt");
    await File.WriteAllLinesAsync(listPath, parts.Select(p => $"file '{p.Replace('\\', '/')}'"));

    Console.WriteLine("Concatenating segments -> MP4 ...");
    var ccode = await RunFfmpegAsync(["-y", "-f", "concat", "-safe", "0", "-i", listPath,
        "-c", "copy", "-movflags", "+faststart", outputMp4]);
    if (ccode != 0) return ccode;
}

var outInfo = new FileInfo(outputMp4);
Console.WriteLine($"Wrote MP4: {outInfo.FullName} ({outInfo.Length / 1024.0 / 1024.0:F1} MB)");
return 0;
