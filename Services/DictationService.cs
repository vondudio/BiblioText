using Microsoft.UI.Dispatching;
using NAudio.Wave;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Whisper.net;

namespace BiblioText.Services;

/// <summary>
/// Simple on-device voice-to-text: record a clip (up to 10s), transcribe with Whisper, return text.
/// </summary>
public sealed class DictationService : IDisposable
{
    private WhisperProcessor? _processor;
    private WaveInEvent? _waveIn;
    private MemoryStream? _audioStream;
    private bool _isRecording;
    private TaskCompletionSource<string>? _recordingTcs;
    private DispatcherQueue? _dispatcher;

    private static readonly string ModelDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BiblioText", "whisper-models");
    private const string ModelFileName = "ggml-base.bin";
    private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin";

    public event Action<string>? StatusChanged;
    public bool IsRecording => _isRecording;
    public bool IsReady => _processor != null;

    public async Task InitializeAsync(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        string modelPath = Path.Combine(ModelDir, ModelFileName);

        if (!File.Exists(modelPath))
        {
            NotifyStatus("Downloading speech model (145 MB)...");
            await DownloadModelAsync(modelPath);
        }

        NotifyStatus("Loading speech model...");
        var factory = WhisperFactory.FromPath(modelPath);
        _processor = factory.CreateBuilder()
            .WithLanguage("en")
            .WithNoContext()
            .WithSingleSegment()
            .Build();
        NotifyStatus("");
    }

    /// <summary>
    /// Start recording. Returns immediately. Call StopAndTranscribeAsync() to get text.
    /// Auto-stops after 10 seconds.
    /// </summary>
    public void StartRecording()
    {
        if (_isRecording || _processor == null) return;

        _audioStream = new MemoryStream();
        _recordingTcs = new TaskCompletionSource<string>();
        _isRecording = true;

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 100
        };
        _waveIn.DataAvailable += (s, e) =>
        {
            _audioStream?.Write(e.Buffer, 0, e.BytesRecorded);

            // Auto-stop at 10 seconds (16000 samples/s * 2 bytes = 320000 bytes/s)
            if (_audioStream != null && _audioStream.Length >= 320000 * 10)
            {
                _dispatcher?.TryEnqueue(() => _ = StopAndTranscribeAsync());
            }
        };
        _waveIn.StartRecording();
        NotifyStatus("🎤 Recording...");
    }

    /// <summary>
    /// Stop recording and transcribe the audio. Returns the recognized text.
    /// </summary>
    public async Task<string> StopAndTranscribeAsync()
    {
        if (!_isRecording) return "";
        _isRecording = false;

        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _waveIn = null;

        if (_audioStream == null || _audioStream.Length < 3200) // less than 0.1s
        {
            _audioStream?.Dispose();
            NotifyStatus("");
            return "";
        }

        NotifyStatus("Processing speech...");

        byte[] pcm = _audioStream.ToArray();
        _audioStream.Dispose();
        _audioStream = null;

        try
        {
            string result = await Transcribe(pcm);
            NotifyStatus("");
            return result;
        }
        catch (Exception ex)
        {
            NotifyStatus($"Transcription error: {ex.Message}");
            return "";
        }
    }

    private async Task<string> Transcribe(byte[] pcm16)
    {
        if (_processor == null) return "";

        // Build WAV in memory
        int sampleCount = pcm16.Length / 2;
        using var wav = new MemoryStream();
        WriteWavHeader(wav, sampleCount, 16000);
        wav.Write(pcm16);
        wav.Position = 0;

        string text = "";
        await foreach (var segment in _processor.ProcessAsync(wav))
        {
            string t = segment.Text.Trim();
            if (!string.IsNullOrWhiteSpace(t) && !t.StartsWith("[") && !t.StartsWith("(") && t.Length > 1)
                text += t + " ";
        }
        return text.Trim();
    }

    private static void WriteWavHeader(Stream stream, int sampleCount, int sampleRate)
    {
        int dataSize = sampleCount * 2;
        using var bw = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        bw.Write("RIFF"u8);
        bw.Write(36 + dataSize);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)1); // mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write("data"u8);
        bw.Write(dataSize);
    }

    private static async Task DownloadModelAsync(string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var fs = File.Create(targetPath);
        await response.Content.CopyToAsync(fs);
    }

    private void NotifyStatus(string msg) =>
        _dispatcher?.TryEnqueue(() => StatusChanged?.Invoke(msg));

    public void Dispose()
    {
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _audioStream?.Dispose();
        _processor?.Dispose();
    }
}
