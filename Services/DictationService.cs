using Microsoft.UI.Dispatching;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;

namespace BiblioText.Services;

/// <summary>
/// On-device speech-to-text using Whisper.net (no internet required).
/// Captures audio from the default microphone, buffers speech chunks,
/// and transcribes them locally using the Whisper "base" model.
/// </summary>
public sealed class DictationService : IDisposable
{
    private WhisperProcessor? _processor;
    private WaveInEvent? _waveIn;
    private DispatcherQueue? _dispatcher;
    private CancellationTokenSource? _cts;

    private bool _isListening;
    private string _committedText = "";
    private readonly List<byte> _audioBuffer = new();
    private readonly object _bufferLock = new();
    private Task? _processingTask;

    // Silence detection: flush buffer after this duration of low-energy audio
    private const int SilenceThresholdMs = 1500;
    private const float SilenceEnergyThreshold = 0.01f;
    private const int MinBufferMs = 800; // minimum speech before processing
    private DateTime _lastSoundTime = DateTime.UtcNow;

    private static readonly string ModelDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BiblioText", "whisper-models");

    private const string ModelFileName = "ggml-base.bin";
    private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin";

    public event Action<string>? TextUpdated;
    public event Action<string>? StatusChanged;
    public event Action<bool>? ListeningChanged;

    public bool IsListening => _isListening;

    public async Task InitializeAsync(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;

        string modelPath = Path.Combine(ModelDir, ModelFileName);
        if (!File.Exists(modelPath))
        {
            StatusChanged?.Invoke("Downloading speech model (145 MB)...");
            await DownloadModelAsync(modelPath);
        }

        StatusChanged?.Invoke("Loading speech model...");
        try
        {
            var factory = WhisperFactory.FromPath(modelPath);
            _processor = factory.CreateBuilder()
                .WithLanguage("en")
                .WithNoContext()
                .WithSingleSegment()
                .Build();
            StatusChanged?.Invoke("Speech model ready (on-device).");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Failed to load model: {ex.Message}");
        }
    }

    public async Task StartAsync(string existingText = "")
    {
        if (_processor == null)
        {
            StatusChanged?.Invoke("Speech model not loaded.");
            return;
        }

        _committedText = existingText;
        _isListening = true;
        _cts = new CancellationTokenSource();
        ListeningChanged?.Invoke(true);

        lock (_bufferLock) _audioBuffer.Clear();
        _lastSoundTime = DateTime.UtcNow;

        // Capture at 16kHz mono 16-bit PCM (Whisper's expected format)
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 100
        };
        _waveIn.DataAvailable += OnAudioDataAvailable;
        _waveIn.RecordingStopped += (s, e) => { };

        try
        {
            _waveIn.StartRecording();
            StatusChanged?.Invoke("🎤 Listening (on-device)...");
            _processingTask = ProcessAudioLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            _isListening = false;
            ListeningChanged?.Invoke(false);
            StatusChanged?.Invoke($"Microphone error: {ex.Message}");
        }
    }

    public async Task StopAsync()
    {
        if (!_isListening) return;
        _isListening = false;
        _cts?.Cancel();

        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _waveIn = null;

        // Process any remaining audio in buffer
        await FlushBufferAsync();

        if (_processingTask != null)
        {
            try { await _processingTask; } catch { }
        }

        ListeningChanged?.Invoke(false);
        StatusChanged?.Invoke("Dictation stopped.");
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        // Calculate energy to detect silence
        float energy = CalculateEnergy(e.Buffer, e.BytesRecorded);
        if (energy > SilenceEnergyThreshold)
            _lastSoundTime = DateTime.UtcNow;

        lock (_bufferLock)
        {
            _audioBuffer.AddRange(new ReadOnlySpan<byte>(e.Buffer, 0, e.BytesRecorded));
        }
    }

    private async Task ProcessAudioLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(200, ct).ConfigureAwait(false);

            int bufferMs;
            lock (_bufferLock)
            {
                // 16kHz * 2 bytes * 1 channel = 32000 bytes/sec
                bufferMs = _audioBuffer.Count * 1000 / 32000;
            }

            bool silenceDetected = (DateTime.UtcNow - _lastSoundTime).TotalMilliseconds > SilenceThresholdMs;

            // Process when we have enough audio AND silence detected
            if (bufferMs >= MinBufferMs && silenceDetected)
            {
                await FlushBufferAsync();
            }
        }
    }

    private async Task FlushBufferAsync()
    {
        byte[] audioData;
        lock (_bufferLock)
        {
            if (_audioBuffer.Count < 32000 * MinBufferMs / 1000) return; // min threshold
            audioData = _audioBuffer.ToArray();
            _audioBuffer.Clear();
        }

        if (_processor == null) return;

        _dispatcher?.TryEnqueue(() => StatusChanged?.Invoke("🎤 Processing speech..."));

        try
        {
            // Convert 16-bit PCM to float samples
            float[] samples = ConvertToFloat(audioData);

            using var ms = new MemoryStream();
            WriteWavHeader(ms, samples.Length, 16000);
            foreach (var sample in samples)
            {
                ms.Write(BitConverter.GetBytes((short)(sample * 32767f)));
            }
            ms.Position = 0;

            string segmentText = "";
            await foreach (var segment in _processor.ProcessAsync(ms))
            {
                string text = segment.Text.Trim();
                // Skip whisper hallucinations (common with silence)
                if (!string.IsNullOrWhiteSpace(text) &&
                    !text.StartsWith("[") &&
                    !text.Contains("(") &&
                    text.Length > 1)
                {
                    segmentText += text + " ";
                }
            }

            if (!string.IsNullOrWhiteSpace(segmentText))
            {
                _committedText += segmentText;
                _dispatcher?.TryEnqueue(() =>
                {
                    TextUpdated?.Invoke(_committedText.TrimEnd());
                    StatusChanged?.Invoke("🎤 Listening (on-device)...");
                });
            }
            else
            {
                _dispatcher?.TryEnqueue(() => StatusChanged?.Invoke("🎤 Listening (on-device)..."));
            }
        }
        catch (Exception ex)
        {
            _dispatcher?.TryEnqueue(() => StatusChanged?.Invoke($"Transcription error: {ex.Message}"));
        }
    }

    private static float CalculateEnergy(byte[] buffer, int bytesRecorded)
    {
        double sum = 0;
        int sampleCount = bytesRecorded / 2;
        for (int i = 0; i < bytesRecorded - 1; i += 2)
        {
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            float normalized = sample / 32768f;
            sum += normalized * normalized;
        }
        return (float)Math.Sqrt(sum / sampleCount);
    }

    private static float[] ConvertToFloat(byte[] pcm16)
    {
        int sampleCount = pcm16.Length / 2;
        float[] samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            short s = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
            samples[i] = s / 32768f;
        }
        return samples;
    }

    private static void WriteWavHeader(Stream stream, int sampleCount, int sampleRate)
    {
        int dataSize = sampleCount * 2; // 16-bit = 2 bytes
        using var bw = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataSize);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16); // chunk size
        bw.Write((short)1); // PCM
        bw.Write((short)1); // mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2); // byte rate
        bw.Write((short)2); // block align
        bw.Write((short)16); // bits per sample
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);
    }

    private static async Task DownloadModelAsync(string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);
        using var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var fs = File.Create(targetPath);
        await response.Content.CopyToAsync(fs);
    }

    public async Task CleanupAsync()
    {
        if (_isListening)
            await StopAsync();

        _processor?.Dispose();
        _processor = null;
    }

    public void Dispose()
    {
        _isListening = false;
        _cts?.Cancel();
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _processor?.Dispose();
    }
}
