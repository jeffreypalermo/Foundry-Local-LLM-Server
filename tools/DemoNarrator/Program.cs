using System.Globalization;
using System.Speech.Synthesis;
using System.Text.Json;

// Reads the demo manifest produced by tests/blazor-demo-video.spec.ts and synthesizes one narration
// WAV per demo using the built-in Windows TTS engine (System.Speech). Whisper (the only speech model
// the server hosts) is speech-to-TEXT; generating speech FROM text needs a TTS engine, and the local,
// no-cloud option on Windows is System.Speech. Each WAV is named after its clip so the video builder
// can pair them: clip-007.webm -> narration/clip-007.wav.
//
// Usage: dotnet run --project tools/DemoNarrator -- [<manifest.json>] [<outDir>] [<voiceName>]
//   manifest default: frontend/blazor-demo-clips/manifest.json
//   outDir   default: <manifest dir>/narration
//   voice    default: Microsoft Zira Desktop (falls back to the first installed voice)

var manifestPath = args.Length > 0 ? args[0] : Path.Combine("frontend", "blazor-demo-clips", "manifest.json");
if (!File.Exists(manifestPath))
{
    Console.Error.WriteLine($"Manifest not found: {manifestPath}");
    return 1;
}
var clipsDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
var outDir = args.Length > 1 ? args[1] : Path.Combine(clipsDir, "narration");
var wantVoice = args.Length > 2 ? args[2] : "Microsoft Zira Desktop";
// Speaking rate (-10..10, 0 = default). A brisk +2 keeps each demo's frozen-frame tail short.
var rate = args.Length > 3 && int.TryParse(args[3], out var r) ? Math.Clamp(r, -10, 10)
         : int.TryParse(Environment.GetEnvironmentVariable("NARRATION_RATE"), out var er) ? Math.Clamp(er, -10, 10)
         : 2;

Directory.CreateDirectory(outDir);

var entries = JsonSerializer.Deserialize<List<Entry>>(
    File.ReadAllText(manifestPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

using var synth = new SpeechSynthesizer { Volume = 100, Rate = rate };
var voices = synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name).ToList();
var chosen = voices.FirstOrDefault(v => v.Equals(wantVoice, StringComparison.OrdinalIgnoreCase))
             ?? voices.FirstOrDefault(v => v.Contains("Zira", StringComparison.OrdinalIgnoreCase))
             ?? voices.FirstOrDefault();
if (chosen is not null) synth.SelectVoice(chosen);
Console.WriteLine($"Voice: {chosen ?? "(default)"}  Rate: {rate}  Narrating {entries.Count} demos -> {outDir}");

int written = 0, skipped = 0;
foreach (var e in entries)
{
    if (string.IsNullOrWhiteSpace(e.Clip) || string.IsNullOrWhiteSpace(e.Paragraph)) { skipped++; continue; }
    var wav = Path.Combine(outDir, Path.ChangeExtension(e.Clip, ".wav"));
    synth.SetOutputToWaveFile(wav);
    synth.Speak(e.Paragraph);
    var len = new FileInfo(wav).Length / 1024.0;
    Console.WriteLine($"  {Path.GetFileName(wav),-18} {len,7:F0} KB  {Trunc(e.Paragraph, 64)}");
    written++;
}
synth.SetOutputToNull();
Console.WriteLine($"Done: {written} WAV(s) written, {skipped} skipped (no clip/paragraph).");
return 0;

static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

internal sealed record Entry
{
    public string? Clip { get; init; }
    public string? Paragraph { get; init; }
    public int Index { get; init; }
    public bool Ok { get; init; }
}
