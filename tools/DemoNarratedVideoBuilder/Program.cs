using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

// Pairs each demo clip (clip-NNN.webm) with its narration (narration/clip-NNN.wav), muxes the
// narration over the clip — freezing the last video frame if the narration runs longer than the demo
// — normalizes every segment to 1280x720 / 30fps / H.264 + AAC, then concatenates them in manifest
// order into one narrated MP4. Pure C# + the ffmpeg CLI (no ffprobe: durations come from an ffmpeg
// null-decode pass).
//
// Usage: dotnet run --project tools/DemoNarratedVideoBuilder -- [<manifest.json>] [<outputMp4>]
//   manifest default: frontend/blazor-demo-clips/manifest.json
//   output   default: docs/blazor-under30-narrated-demo.mp4

var manifestPath = args.Length > 0 ? args[0] : Path.Combine("frontend", "blazor-demo-clips", "manifest.json");
var outputMp4 = args.Length > 1 ? args[1] : Path.Combine("docs", "blazor-under30-narrated-demo.mp4");

if (!File.Exists(manifestPath)) { Console.Error.WriteLine($"Manifest not found: {manifestPath}"); return 1; }
var clipsDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
var narrationDir = Path.Combine(clipsDir, "narration");

var entries = JsonSerializer.Deserialize<List<Entry>>(
    File.ReadAllText(manifestPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
entries = entries.Where(e => !string.IsNullOrWhiteSpace(e.Clip)).OrderBy(e => e.Index).ToList();
if (entries.Count == 0) { Console.Error.WriteLine("No clips referenced in manifest."); return 1; }

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputMp4))!);
var tmp = Path.Combine(clipsDir, ".segments");
Directory.CreateDirectory(tmp);

const double Lead = 0.4;  // narration starts this many seconds into the clip
const double Tail = 0.6;  // hold this long after the longer of (video, narration) ends
var parts = new List<string>();

for (var i = 0; i < entries.Count; i++)
{
    var e = entries[i];
    var clip = Path.Combine(clipsDir, e.Clip!);
    if (!File.Exists(clip)) { Console.WriteLine($"  [missing clip] {e.Clip}"); continue; }
    var wav = Path.Combine(narrationDir, Path.ChangeExtension(e.Clip!, ".wav"));
    var hasNarr = File.Exists(wav);

    var vdur = await DurationAsync(clip);
    var adur = hasNarr ? await DurationAsync(wav) : 0.0;
    var target = Math.Max(vdur, adur + Lead) + Tail;
    var padV = Math.Max(0.0, target - vdur);
    var part = Path.Combine(tmp, $"part-{i:D3}.mp4");

    var vf = "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2,fps=30," +
             $"tpad=stop_mode=clone:stop_duration={F(padV)}";
    string[] ff = hasNarr
        ? ["-y", "-i", clip, "-i", wav,
           "-filter_complex", $"[0:v]{vf}[v];[1:a]adelay={(int)(Lead * 1000)}|{(int)(Lead * 1000)},apad[a]",
           "-map", "[v]", "-map", "[a]", "-t", F(target),
           "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "28", "-preset", "veryfast",
           "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2", part]
        : ["-y", "-i", clip, "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
           "-filter_complex", $"[0:v]{vf}[v]",
           "-map", "[v]", "-map", "1:a", "-t", F(target),
           "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "28", "-preset", "veryfast",
           "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2", part];

    Console.WriteLine($"  [{i + 1}/{entries.Count}] {e.Clip}  v={vdur:F1}s a={adur:F1}s -> {target:F1}s{(hasNarr ? "" : "  (no narration: silent)")}");
    var code = await RunAsync("ffmpeg", ff);
    if (code != 0) { Console.Error.WriteLine($"  ffmpeg failed on {e.Clip} (exit {code})"); return code; }
    parts.Add(part);
}

if (parts.Count == 0) { Console.Error.WriteLine("No segments produced."); return 1; }

var listPath = Path.Combine(tmp, "concat.txt");
await File.WriteAllLinesAsync(listPath, parts.Select(p => $"file '{p.Replace('\\', '/')}'"));
Console.WriteLine($"Concatenating {parts.Count} narrated segments -> {outputMp4} ...");
var ccode = await RunAsync("ffmpeg", ["-y", "-f", "concat", "-safe", "0", "-i", listPath,
    "-c", "copy", "-movflags", "+faststart", outputMp4]);
if (ccode != 0) return ccode;

var fi = new FileInfo(outputMp4);
Console.WriteLine($"Wrote MP4: {fi.FullName} ({fi.Length / 1024.0 / 1024.0:F1} MB)");
return 0;

static string F(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

// Duration via an ffmpeg null-decode pass (ffprobe isn't installed). Parses the last "time=" emitted.
static async Task<double> DurationAsync(string file)
{
    var stderr = await CaptureStderrAsync("ffmpeg", ["-i", file, "-f", "null", "-"]);
    var matches = Regex.Matches(stderr, @"time=(\d+):(\d+):(\d+(?:\.\d+)?)");
    if (matches.Count > 0)
    {
        var m = matches[^1];
        return int.Parse(m.Groups[1].Value) * 3600.0
             + int.Parse(m.Groups[2].Value) * 60.0
             + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
    }
    var dur = Regex.Match(stderr, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
    if (dur.Success)
        return int.Parse(dur.Groups[1].Value) * 3600.0
             + int.Parse(dur.Groups[2].Value) * 60.0
             + double.Parse(dur.Groups[3].Value, CultureInfo.InvariantCulture);
    return 0.0;
}

static async Task<int> RunAsync(string exe, string[] argv)
{
    var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (var a in argv) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var err = await p.StandardError.ReadToEndAsync();
    await p.StandardOutput.ReadToEndAsync();
    await p.WaitForExitAsync();
    if (p.ExitCode != 0) Console.Error.WriteLine(err[Math.Max(0, err.Length - 1200)..]);
    return p.ExitCode;
}

static async Task<string> CaptureStderrAsync(string exe, string[] argv)
{
    var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (var a in argv) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var err = await p.StandardError.ReadToEndAsync();
    await p.StandardOutput.ReadToEndAsync();
    await p.WaitForExitAsync();
    return err;
}

internal sealed record Entry
{
    public string? Clip { get; init; }
    public int Index { get; init; }
    public string? Label { get; init; }
    public bool Ok { get; init; }
}
