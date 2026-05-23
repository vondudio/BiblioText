using Microsoft.UI.Dispatching;
using System;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace BiblioText.Services;

public sealed class DictationService : IDisposable
{
    private SpeechRecognizer? _recognizer;
    private bool _isListening;
    private string _committedText = "";
    private DispatcherQueue? _dispatcher;

    public event Action<string>? TextUpdated;
    public event Action<string>? StatusChanged;
    public event Action<bool>? ListeningChanged;

    public bool IsListening => _isListening;

    public async Task InitializeAsync(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        await CleanupAsync();

        _recognizer = new SpeechRecognizer(SpeechRecognizer.SystemSpeechLanguage);
        _recognizer.StateChanged += OnStateChanged;

        _recognizer.Constraints.Add(
            new SpeechRecognitionTopicConstraint(
                SpeechRecognitionScenario.Dictation, "dictation"));

        var result = await _recognizer.CompileConstraintsAsync();
        if (result.Status != SpeechRecognitionResultStatus.Success)
        {
            StatusChanged?.Invoke($"Speech init failed: {result.Status}");
            return;
        }

        _recognizer.ContinuousRecognitionSession.ResultGenerated += OnResultGenerated;
        _recognizer.ContinuousRecognitionSession.Completed += OnCompleted;
        _recognizer.HypothesisGenerated += OnHypothesis;
    }

    public async Task StartAsync(string existingText = "")
    {
        if (_recognizer?.State != SpeechRecognizerState.Idle) return;
        _committedText = existingText;
        _isListening = true;
        ListeningChanged?.Invoke(true);

        try
        {
            await _recognizer.ContinuousRecognitionSession.StartAsync();
        }
        catch (Exception ex) when ((uint)ex.HResult == 0x80045509)
        {
            _isListening = false;
            ListeningChanged?.Invoke(false);
            StatusChanged?.Invoke("Enable Online speech recognition in Settings → Privacy → Speech");
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-speech"));
        }
        catch (Exception ex)
        {
            _isListening = false;
            ListeningChanged?.Invoke(false);
            StatusChanged?.Invoke($"Speech error: {ex.Message}");
        }
    }

    public async Task StopAsync()
    {
        if (!_isListening || _recognizer == null) return;
        _isListening = false;

        if (_recognizer.State != SpeechRecognizerState.Idle)
        {
            try
            {
                await _recognizer.ContinuousRecognitionSession.StopAsync();
            }
            catch { }
        }

        ListeningChanged?.Invoke(false);
    }

    public string GetCommittedText() => _committedText;

    private void OnResultGenerated(SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionResultGeneratedEventArgs args)
    {
        if (args.Result.Confidence == SpeechRecognitionConfidence.Medium ||
            args.Result.Confidence == SpeechRecognitionConfidence.High)
        {
            _committedText += args.Result.Text + " ";
            _dispatcher?.TryEnqueue(() => TextUpdated?.Invoke(_committedText.TrimEnd()));
        }
    }

    private void OnHypothesis(SpeechRecognizer sender,
        SpeechRecognitionHypothesisGeneratedEventArgs args)
    {
        string live = _committedText + args.Hypothesis.Text + "…";
        _dispatcher?.TryEnqueue(() => TextUpdated?.Invoke(live));
    }

    private void OnCompleted(SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionCompletedEventArgs args)
    {
        _dispatcher?.TryEnqueue(() =>
        {
            _isListening = false;
            ListeningChanged?.Invoke(false);

            switch (args.Status)
            {
                case SpeechRecognitionResultStatus.Success:
                case SpeechRecognitionResultStatus.UserCanceled:
                    break;
                case SpeechRecognitionResultStatus.TimeoutExceeded:
                    StatusChanged?.Invoke("Dictation stopped (silence timeout).");
                    break;
                case SpeechRecognitionResultStatus.NetworkFailure:
                    StatusChanged?.Invoke("Network error — speech recognition requires internet.");
                    break;
                case SpeechRecognitionResultStatus.MicrophoneUnavailable:
                    StatusChanged?.Invoke("Microphone unavailable.");
                    break;
                default:
                    StatusChanged?.Invoke($"Recognition ended: {args.Status}");
                    break;
            }
        });
    }

    private void OnStateChanged(SpeechRecognizer sender, SpeechRecognizerStateChangedEventArgs args)
    {
        _dispatcher?.TryEnqueue(() =>
            StatusChanged?.Invoke(args.State switch
            {
                SpeechRecognizerState.Capturing => "🎤 Listening...",
                SpeechRecognizerState.Processing => "Processing speech...",
                SpeechRecognizerState.SoundStarted => "🎤 Hearing sound...",
                SpeechRecognizerState.SpeechDetected => "🎤 Speech detected...",
                _ => ""
            }));
    }

    public async Task CleanupAsync()
    {
        if (_recognizer == null) return;

        if (_isListening)
        {
            try { await _recognizer.ContinuousRecognitionSession.CancelAsync(); }
            catch { }
        }

        _recognizer.ContinuousRecognitionSession.ResultGenerated -= OnResultGenerated;
        _recognizer.ContinuousRecognitionSession.Completed -= OnCompleted;
        _recognizer.HypothesisGenerated -= OnHypothesis;
        _recognizer.StateChanged -= OnStateChanged;

        _recognizer.Dispose();
        _recognizer = null;
        _isListening = false;
    }

    public void Dispose() => CleanupAsync().GetAwaiter().GetResult();
}
