using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Skype.Bots.Media;
using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EchoBot.Media
{
    /// <summary>
    /// Class SpeechService.
    /// </summary>
    public class SpeechService
    {
        /// <summary>
        /// The is the indicator if the media stream is running
        /// </summary>
        private bool _isRunning = false;
        /// <summary>
        /// The is draining indicator
        /// </summary>
        protected bool _isDraining;

        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger _logger;
        private readonly AppSettings _settings;
        private readonly string _callId;
        private readonly string _logDirectory;
        private readonly string _traceLogPath;
        private readonly object _traceLock = new object();
        private static readonly HttpClient _httpClient = new HttpClient();
        static SpeechService()
        {
            _httpClient.Timeout = TimeSpan.FromSeconds(60);
        }
        private readonly PushAudioInputStream _audioInputStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
        private readonly AudioOutputStream _audioOutputStream = AudioOutputStream.CreatePullStream();

        private readonly SpeechConfig _speechConfig;
        private SpeechRecognizer _recognizer;
        private AudioConfig _audioInputConfig;
        private bool _recognizerStarted;
        private readonly SpeechSynthesizer _synthesizer;
        private const string DefaultProcessingHint = "Please hold while I check ServiceNow.";
        private Task _speechLoopTask;
        private TaskCompletionSource<bool> _shutdownSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> _restartSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _restartPending;
        private bool _hasWelcomed;
        private bool _userInteracted;
        private long _bufferSampleCount;
        private long _bufferSampleBytes;
        private readonly object _bufferLogLock = new object();
        private DateTime _bufferWindowStartUtc = DateTime.UtcNow;
        private readonly TimeSpan _bufferLogInterval = TimeSpan.FromSeconds(5);
        private readonly TimeSpan _minRestartDelay = TimeSpan.FromMilliseconds(500);
        private readonly TimeSpan _maxRestartDelay = TimeSpan.FromSeconds(5);
        private TimeSpan _currentRestartDelay;
        private DateTime _lastRestartRequestUtc = DateTime.MinValue;
        private volatile bool _suppressInput;
        private readonly SemaphoreSlim _ttsLock = new(1, 1);
        private readonly object _lastSpokenLock = new();
        private string? _lastSpokenText;
        private readonly object _dictationLock = new();
        private DictationState _dictationState = DictationState.Idle;
        private DateTime _dictationStartUtc;
        private DateTime _dictationStopUtc;
        private int _dictationDurationSeconds;
        private readonly StringBuilder _dictationTranscript = new();
        private System.Timers.Timer? _dictationTimer;

        private const int DictationMaxSeconds = 60;
        private const int DictationMinSeconds = 5;
        private const string DictationStartCommand = "begin lab note";
        private const string DictationStopCommand = "stop recording";
        private const string DictationConfirmCommand = "confirm upload";
        private const string DictationYesCommand = "yes";
        private const string DictationCancelCommand = "cancel";
        private const string DictationDiscardCommand = "discard";

        private const string DictationStartPrompt =
            "Lab note recording started.\nYou may speak now. Maximum duration is one minute.";
        private const string DictationStopPrompt =
            "Recording stopped.\nI've prepared a transcript of your note.\nWould you like to share and save it?";
        private const string DictationSavedPrompt = "Your lab note has been saved successfully.";
        private const string DictationDiscardPrompt = "Okay. Discarding the note.";
        private const string DictationTooShortPrompt = "Please continue for a few more seconds.";
        private const string DictationTranscribeFailedPrompt = "I couldn't transcribe this note. Please retry.";
        private const string RepeatMissingPrompt = "I don't have a previous message to repeat.";
        /// <summary>
        /// Initializes a new instance of the <see cref="SpeechService" /> class.
        public SpeechService(AppSettings settings, ILogger logger, string callId)
        {
            _logger = logger;
            _settings = settings;
            _callId = callId ?? string.Empty;
            _logDirectory = string.IsNullOrWhiteSpace(settings.TranscriptionLogDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EchoBot", "speechlogs")
                : settings.TranscriptionLogDirectory;
            Directory.CreateDirectory(_logDirectory);
            _traceLogPath = Path.Combine(_logDirectory, $"speechservice-trace-{DateTime.UtcNow:yyyyMMddHHmmss}.log");
            Trace("SpeechService ctor START");
            Trace($"Settings snapshot -> BotLanguage={settings.BotLanguage}, SpeechRegion={settings.SpeechConfigRegion}, VoiceEndpoint={(string.IsNullOrWhiteSpace(settings.VoiceSttEndpoint) ? "<none>" : settings.VoiceSttEndpoint)}, SpeechKeyPresent={(!string.IsNullOrWhiteSpace(settings.SpeechConfigKey))}, UseSpeechService={settings.UseSpeechService}");

            _speechConfig = SpeechConfig.FromSubscription(settings.SpeechConfigKey, settings.SpeechConfigRegion);
            _speechConfig.SpeechSynthesisLanguage = settings.BotLanguage;
            _speechConfig.SpeechRecognitionLanguage = settings.BotLanguage;

            var audioConfig = AudioConfig.FromStreamOutput(_audioOutputStream);
            _synthesizer = new SpeechSynthesizer(_speechConfig, audioConfig);
            _currentRestartDelay = _minRestartDelay;
            Trace("SpeechService ctor END");
        }

        /// <summary>
        /// Appends the audio buffer.
        /// </summary>
        /// <param name="audioBuffer"></param>
        public Task AppendAudioBuffer(AudioMediaBuffer audioBuffer)
        {
            if (!_isRunning)
            {
                Start();
            }

            try
            {
                // audio for a 1:1 call
                var bufferLength = audioBuffer.Length;
                if (bufferLength > 0)
                {
                    if (_suppressInput)
                    {
                        TrackBufferMetrics((int)bufferLength);
                        return Task.CompletedTask;
                    }
                    var buffer = new byte[bufferLength];
                    Marshal.Copy(audioBuffer.Data, buffer, 0, (int)bufferLength);

                    _audioInputStream.Write(buffer);
                    TrackBufferMetrics((int)bufferLength);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Exception happend writing to input stream");
            }

            return Task.CompletedTask;
        }

        public virtual void OnSendMediaBufferEventArgs(object sender, MediaStreamEventArgs e)
        {
            Trace("OnSendMediaBufferEventArgs START");
            if (SendMediaBuffer != null)
            {
                SendMediaBuffer(this, e);
            }
            Trace("OnSendMediaBufferEventArgs END");
        }

        public event EventHandler<MediaStreamEventArgs> SendMediaBuffer;

        /// <summary>
        /// Ends this instance.
        /// </summary>
        /// <returns>Task.</returns>
        public async Task ShutDownAsync()
        {
            Trace("ShutDownAsync START");
            if (!_isRunning)
            {
                Trace("ShutDownAsync END (not running)");
                return;
            }

            _shutdownSignal.TrySetResult(true);
            if (_speechLoopTask != null)
            {
                await _speechLoopTask.ConfigureAwait(false);
                _speechLoopTask = null;
            }

            _audioInputStream.Close();
            _audioInputStream.Dispose();
            _audioOutputStream.Dispose();
            _synthesizer.Dispose();

            _isRunning = false;
            Trace("ShutDownAsync END");
        }

        /// <summary>
        /// Starts this instance.
        /// </summary>
        private void Start()
        {
            Trace("Start START");
            if (_isRunning)
            {
                Trace("Start END (already running)");
                return;
            }

            _isRunning = true;
            _hasWelcomed = false;
            _restartPending = false;
            _shutdownSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _restartSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _speechLoopTask = Task.Run(ProcessSpeechLoopAsync);
            Trace("Start END");
        }

        private async Task ProcessSpeechLoopAsync()
        {
            Trace("ProcessSpeechLoopAsync START");
            try
            {
                while (!_shutdownSignal.Task.IsCompleted)
                {
                    await EnsureRecognizerRunningAsync().ConfigureAwait(false);

                    var completedTask = await Task.WhenAny(_shutdownSignal.Task, _restartSignal.Task).ConfigureAwait(false);
                    if (completedTask == _shutdownSignal.Task)
                    {
                        break;
                    }

                    _restartSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    await ResetRecognizerAsync().ConfigureAwait(false);
                    _restartPending = false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProcessSpeechLoopAsync faulted.");
            }
            finally
            {
                await ResetRecognizerAsync().ConfigureAwait(false);
                _restartPending = false;
                _currentRestartDelay = _minRestartDelay;
                Trace("ProcessSpeechLoopAsync END");
            }
        }

        private async Task EnsureRecognizerRunningAsync()
        {
            try
            {
                if (_recognizer == null)
                {
                    _logger.LogInformation("init recognizer");
                    _audioInputConfig = AudioConfig.FromStreamInput(_audioInputStream);
                    _recognizer = new SpeechRecognizer(_speechConfig, _audioInputConfig);

                    _recognizer.Recognizing += (s, e) =>
                    {
                        _logger.LogInformation($"RECOGNIZING: Text={e.Result.Text}");
                    };

                    _recognizer.Recognized += async (s, e) =>
                    {
                        if (e.Result.Reason == ResultReason.RecognizedSpeech)
                        {
                            var recognizedText = e.Result.Text;
                            if (string.IsNullOrWhiteSpace(recognizedText))
                                return;

                            _userInteracted = true;
                            _logger.LogInformation($"RECOGNIZED: Text={recognizedText}");
                            LogRecognizedText(recognizedText);

                            if (await TryHandleDictationAsync(recognizedText).ConfigureAwait(false))
                            {
                                return;
                            }

                            var holdPromptSpoken = false;
                            if (RequiresSnowIntent(recognizedText))
                            {
                                await TextToSpeech(DefaultProcessingHint);
                                holdPromptSpoken = true;
                            }

                            var responseBody = await RelayToVoiceEndpointAsync(recognizedText);
                            var speechText = recognizedText;
                            bool longRunning;
                            string processingHint;
                            longRunning = false;
                            processingHint = null;
                            if (!string.IsNullOrWhiteSpace(responseBody))
                            {
                                var formatted = BuildSpeechResponse(responseBody, speechText, out longRunning, out processingHint);
                                if (!string.IsNullOrWhiteSpace(formatted))
                                {
                                    speechText = formatted;
                                }
                            }

                            if (longRunning && !holdPromptSpoken)
                            {
                                var hint = string.IsNullOrWhiteSpace(processingHint) ? DefaultProcessingHint : processingHint;
                                await TextToSpeech(hint);
                                holdPromptSpoken = true;
                            }

                            await TextToSpeech(speechText);
                            _currentRestartDelay = _minRestartDelay;
                            _lastRestartRequestUtc = DateTime.UtcNow;
                        }
                        else if (e.Result.Reason == ResultReason.NoMatch)
                        {
                            _logger.LogInformation("NOMATCH: Speech could not be recognized.");
                        }
                    };

            _recognizer.Canceled += (s, e) =>
            {
                _logger.LogInformation($"CANCELED: Reason={e.Reason}");
                Trace($"Recognizer canceled -> Reason={e.Reason}, ErrorCode={(e.Reason == CancellationReason.Error ? e.ErrorCode.ToString() : "None")}, RestartPending={_restartPending}, RecognizerStarted={_recognizerStarted}");

                if (e.Reason == CancellationReason.Error)
                {
                    _logger.LogInformation($"CANCELED: ErrorCode={e.ErrorCode}");
                    _logger.LogInformation($"CANCELED: ErrorDetails={e.ErrorDetails}");
                            _logger.LogInformation("CANCELED: Did you update the subscription info?");
                        }

                        _recognizerStarted = false;
                        RequestRecognizerRestart("Canceled");
                    };

                    _recognizer.SessionStarted += async (s, e) =>
                    {
                        _logger.LogInformation("\nSession started event.");
                        if (!_hasWelcomed && _userInteracted)
                        {
                            _hasWelcomed = true;
                            await TextToSpeech("Hello");
                        }
                    };

                    _recognizer.SessionStopped += (s, e) =>
                    {
                        _logger.LogInformation("\nSession stopped event.");
                        _recognizerStarted = false;
                        RequestRecognizerRestart("SessionStopped");
                    };
                }

                if (!_recognizerStarted && _recognizer != null)
                {
                    await _recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);
                    _recognizerStarted = true;
                }
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogError(ex, "The queue processing task object has been disposed.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Caught Exception while ensuring recognizer");
                RequestRecognizerRestart("EnsureRecognizerRunningAsync Exception");
            }
        }

        private async Task ResetRecognizerAsync()
        {
            try
            {
                if (_recognizer != null)
                {
                    if (_recognizerStarted)
                    {
                        try
                        {
                            await _recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to stop recognizer during reset.");
                        }
                    }

                    _recognizer.Dispose();
                    _recognizer = null;
                    _recognizerStarted = false;
                }
            }
            finally
            {
                _audioInputConfig?.Dispose();
                _audioInputConfig = null;
            }
        }

        private void RequestRecognizerRestart(string reason)
        {
            if (_shutdownSignal.Task.IsCompleted)
            {
                Trace($"Recognizer restart ignored ({reason}) because shutdown requested");
                return;
            }

            if (_restartPending)
            {
                Trace($"Recognizer restart already pending (ignore {reason})");
                return;
            }

            _restartPending = true;
            var now = DateTime.UtcNow;
            var elapsed = now - _lastRestartRequestUtc;
            if (elapsed < _currentRestartDelay)
            {
                var delay = _currentRestartDelay - elapsed;
                Trace($"Recognizer restart deferred ({reason}) for {delay.TotalMilliseconds:F0}ms");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(delay).ConfigureAwait(false);
                        _restartSignal.TrySetResult(true);
                    }
                    finally
                    {
                        _lastRestartRequestUtc = DateTime.UtcNow;
                        var doubled = TimeSpan.FromMilliseconds(_currentRestartDelay.TotalMilliseconds * 2);
                        _currentRestartDelay = doubled > _maxRestartDelay ? _maxRestartDelay : doubled;
                    }
                });
            }
            else
            {
                Trace($"Recognizer restart requested ({reason})");
                _restartSignal.TrySetResult(true);
                _lastRestartRequestUtc = now;
                _currentRestartDelay = _minRestartDelay;
            }
        }

        private async Task TextToSpeech(string text)
        {
            Trace($"TextToSpeech START text=\"{text}\"");
            await _ttsLock.WaitAsync().ConfigureAwait(false);
            _suppressInput = true;
            try
            {
                SpeechSynthesisResult result = await _synthesizer.SpeakTextAsync(text).ConfigureAwait(false);
                using (var stream = AudioDataStream.FromResult(result))
                {
                    var currentTick = DateTime.Now.Ticks;
                    MediaStreamEventArgs args = new MediaStreamEventArgs
                    {
                        AudioMediaBuffers = Util.Utilities.CreateAudioMediaBuffers(stream, currentTick, _logger)
                    };
                    OnSendMediaBufferEventArgs(this, args);
                }
                StoreLastSpoken(text);
            }
            finally
            {
                _suppressInput = false;
                _ttsLock.Release();
                Trace("TextToSpeech END");
            }
        }

        private void LogRecognizedText(string text)
        {
            Trace($"LogRecognizedText START text=\"{text}\"");
            try
            {
                var file = Path.Combine(_logDirectory, "recognized.txt");
                File.AppendAllText(file, $"{DateTime.UtcNow:u} - {text}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Transcript log write failed.");
            }
            finally
            {
                Trace("LogRecognizedText END");
            }
        }

        private async Task<string?> RelayToVoiceEndpointAsync(string recognizedText)
        {
            Trace($"RelayToVoiceEndpointAsync START text=\"{recognizedText}\"");
            if (string.IsNullOrWhiteSpace(_settings?.VoiceSttEndpoint))
            {
                Trace("RelayToVoiceEndpointAsync END (no endpoint)");
                return null;
            }

            try
            {
                var payload = new
                {
                    text = recognizedText,
                    callId = _callId
                };

                using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(_settings.VoiceSttEndpoint, content).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                await LogVoiceSttResponseAsync(response.IsSuccessStatusCode, body);
                return response.IsSuccessStatusCode ? body : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to call /voice/stt endpoint.");
                return null;
            }
            finally
            {
                Trace("RelayToVoiceEndpointAsync END");
            }
        }

        private async Task LogVoiceSttResponseAsync(bool success, string body)
        {
            Trace("LogVoiceSttResponseAsync START");
            try
            {
                var file = Path.Combine(_logDirectory, "voice-stt-response.txt");
                var line = $"{DateTime.UtcNow:u} | callId={_callId} | success={success} | {body}";
                await File.AppendAllTextAsync(file, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to log /voice/stt response.");
            }
            finally
            {
                Trace("LogVoiceSttResponseAsync END");
            }
        }

        private string? BuildSpeechResponse(string responseBody, string fallbackRecognizedText, out bool longRunning, out string processingHint)
        {
            Trace("BuildSpeechResponse START");
            longRunning = false;
            processingHint = null;
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                Trace("BuildSpeechResponse END (empty body)");
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (!doc.RootElement.TryGetProperty("result", out var resultElement))
                {
                    return null;
                }

                if (resultElement.TryGetProperty("long_running", out var longRunningElement) && longRunningElement.ValueKind == JsonValueKind.True)
                {
                    longRunning = true;
                    processingHint = TryGetString(resultElement, "processing_hint") ?? DefaultProcessingHint;
                }

                var action = TryGetString(resultElement, "action")?.ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(action))
                {
                    return null;
                }

                switch (action)
                {
                    case "ticket_create":
                        var inc = TryGetString(resultElement, "inc_number");
                        var reason = TryGetString(resultElement, "reason");
                        if (!string.IsNullOrWhiteSpace(inc))
                        {
                            return $"I created ticket {inc} for {ReasonOrFallback(reason)}.";
                        }
                        if (!string.IsNullOrWhiteSpace(reason))
                        {
                            return $"I created a ticket for {reason}.";
                        }
                        return "I created a ticket for your issue.";

                    case "ticket_status":
                        var state = TryGetString(resultElement, "state");
                        var statusInc = TryGetString(resultElement, "inc_number") ?? "the incident";
                        var extra = TryGetString(resultElement, "short_description");
                        if (!string.IsNullOrWhiteSpace(state) && !string.IsNullOrWhiteSpace(extra))
                        {
                            return $"Status for {statusInc} is {state}. {extra}";
                        }
                        if (!string.IsNullOrWhiteSpace(state))
                        {
                            return $"Status for {statusInc} is {state}.";
                        }
                        return $"Status for {statusInc} is not available yet.";

                    case "password_reset":
                    case "help":
                    case "bot_profile":
                    case "ticket_howto":
                    case "direct_reply":
                        var reply = TryGetString(resultElement, "text");
                        return string.IsNullOrWhiteSpace(reply) ? fallbackRecognizedText : reply;

                    case "propose_ticket":
                        var propReason = TryGetString(resultElement, "reason") ?? "your issue";
                        var tips = TryGetString(resultElement, "tips");
                        if (!string.IsNullOrWhiteSpace(tips))
                        {
                            return $"I can create a ticket for {propReason}. {tips}";
                        }
                        return $"I can create a ticket for {propReason}. Should I go ahead?";

                    case "legacy":
                        var legacy = TryGetString(resultElement, "text");
                        return string.IsNullOrWhiteSpace(legacy)
                            ? $"You said: {fallbackRecognizedText}"
                            : $"You said: {legacy}";

                    case "repeat_last":
                        return GetLastSpokenOrFallback();

                    default:
                        return null;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse /voice/stt response JSON.");
                return null;
            }
            finally
            {
                Trace("BuildSpeechResponse END");
            }
        }

        private static string? TryGetString(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
            return null;
        }

        private enum DictationState
        {
            Idle,
            Recording,
            Transcribed
        }

        private static string NormalizeCommand(string text)
        {
            var lower = (text ?? string.Empty).Trim().ToLowerInvariant();
            lower = Regex.Replace(lower, @"[^a-z0-9\s]", " ");
            lower = Regex.Replace(lower, @"\s+", " ").Trim();
            return lower;
        }

        private async Task<bool> TryHandleDictationAsync(string recognizedText)
        {
            var normalized = NormalizeCommand(recognizedText);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            if (normalized == DictationStartCommand)
            {
                await StartDictationAsync().ConfigureAwait(false);
                return true;
            }

            lock (_dictationLock)
            {
                if (_dictationState == DictationState.Idle)
                {
                    return false;
                }
            }

            if (normalized == DictationStopCommand)
            {
                await StopDictationAsync(isTimeout: false).ConfigureAwait(false);
                return true;
            }

            if (normalized == DictationConfirmCommand || normalized == DictationYesCommand)
            {
                await ConfirmDictationUploadAsync().ConfigureAwait(false);
                return true;
            }

            if (normalized == DictationCancelCommand || normalized == DictationDiscardCommand)
            {
                await CancelDictationAsync().ConfigureAwait(false);
                return true;
            }

            lock (_dictationLock)
            {
                if (_dictationState == DictationState.Recording)
                {
                    AppendDictationTranscript(recognizedText);
                    return true;
                }
                if (_dictationState == DictationState.Transcribed)
                {
                    return true;
                }
            }

            return false;
        }

        private async Task StartDictationAsync()
        {
            lock (_dictationLock)
            {
                if (_dictationState == DictationState.Recording)
                {
                    return;
                }
                _dictationState = DictationState.Recording;
                _dictationStartUtc = DateTime.UtcNow;
                _dictationStopUtc = DateTime.MinValue;
                _dictationDurationSeconds = 0;
                _dictationTranscript.Clear();
                StartDictationTimer();
            }

            await TextToSpeech(DictationStartPrompt).ConfigureAwait(false);
        }

        private void StartDictationTimer()
        {
            _dictationTimer?.Stop();
            _dictationTimer?.Dispose();
            _dictationTimer = new System.Timers.Timer(TimeSpan.FromSeconds(DictationMaxSeconds).TotalMilliseconds)
            {
                AutoReset = false
            };
            _dictationTimer.Elapsed += async (_, __) =>
            {
                await StopDictationAsync(isTimeout: true).ConfigureAwait(false);
            };
            _dictationTimer.Start();
        }

        private async Task StopDictationAsync(bool isTimeout)
        {
            DateTime startUtc;
            lock (_dictationLock)
            {
                if (_dictationState != DictationState.Recording)
                {
                    return;
                }

                startUtc = _dictationStartUtc;
                var elapsed = DateTime.UtcNow - startUtc;
                if (!isTimeout && elapsed.TotalSeconds < DictationMinSeconds)
                {
                    _ = TextToSpeech(DictationTooShortPrompt);
                    return;
                }

                _dictationState = DictationState.Transcribed;
                _dictationStopUtc = DateTime.UtcNow;
                _dictationDurationSeconds = (int)Math.Round(elapsed.TotalSeconds);
                _dictationTimer?.Stop();
            }

            if (_dictationTranscript.Length == 0)
            {
                await ResetDictationAsync().ConfigureAwait(false);
                await TextToSpeech(DictationTranscribeFailedPrompt).ConfigureAwait(false);
                return;
            }

            await TextToSpeech(DictationStopPrompt).ConfigureAwait(false);
        }

        private async Task ConfirmDictationUploadAsync()
        {
            string transcript;
            int durationSeconds;
            DateTime startedUtc;
            DateTime timestampUtc;

            lock (_dictationLock)
            {
                if (_dictationState != DictationState.Transcribed)
                {
                    return;
                }
                transcript = _dictationTranscript.ToString().Trim();
                durationSeconds = _dictationDurationSeconds;
                startedUtc = _dictationStartUtc;
                timestampUtc = _dictationStopUtc == DateTime.MinValue ? DateTime.UtcNow : _dictationStopUtc;
            }

            if (string.IsNullOrWhiteSpace(transcript))
            {
                await ResetDictationAsync().ConfigureAwait(false);
                await TextToSpeech(DictationTranscribeFailedPrompt).ConfigureAwait(false);
                return;
            }

            var uploaded = await RelayLabNoteUploadAsync(transcript, durationSeconds, startedUtc, timestampUtc).ConfigureAwait(false);
            await ResetDictationAsync().ConfigureAwait(false);

            if (uploaded)
            {
                await TextToSpeech(DictationSavedPrompt).ConfigureAwait(false);
            }
            else
            {
                await TextToSpeech("Upload failed. Please try again.").ConfigureAwait(false);
            }
        }

        private async Task CancelDictationAsync()
        {
            await ResetDictationAsync().ConfigureAwait(false);
            await TextToSpeech(DictationDiscardPrompt).ConfigureAwait(false);
        }

        private Task ResetDictationAsync()
        {
            lock (_dictationLock)
            {
                _dictationState = DictationState.Idle;
                _dictationTranscript.Clear();
                _dictationStartUtc = DateTime.MinValue;
                _dictationStopUtc = DateTime.MinValue;
                _dictationDurationSeconds = 0;
                _dictationTimer?.Stop();
                _dictationTimer?.Dispose();
                _dictationTimer = null;
            }
            return Task.CompletedTask;
        }

        private void AppendDictationTranscript(string recognizedText)
        {
            if (string.IsNullOrWhiteSpace(recognizedText))
            {
                return;
            }

            if (_dictationTranscript.Length > 0)
            {
                _dictationTranscript.Append(' ');
            }
            _dictationTranscript.Append(recognizedText.Trim());
        }

        private async Task<bool> RelayLabNoteUploadAsync(string transcript, int durationSeconds, DateTime startedUtc, DateTime timestampUtc)
        {
            var endpoint = BuildLabNoteEndpoint();
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                _logger.LogWarning("Lab note upload skipped: VoiceSttEndpoint is not configured.");
                return false;
            }

            try
            {
                var payload = new
                {
                    transcript = transcript,
                    durationSeconds = durationSeconds,
                    startTimeUtc = startedUtc.ToUniversalTime().ToString("o"),
                    timestampUtc = timestampUtc.ToUniversalTime().ToString("o"),
                    callId = _callId
                };

                using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(endpoint, content).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                await LogVoiceSttResponseAsync(response.IsSuccessStatusCode, body);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to call /voice/labnote endpoint.");
                return false;
            }
        }

        private string? BuildLabNoteEndpoint()
        {
            var sttEndpoint = _settings?.VoiceSttEndpoint;
            if (string.IsNullOrWhiteSpace(sttEndpoint))
            {
                return null;
            }

            if (sttEndpoint.EndsWith("/voice/stt", StringComparison.OrdinalIgnoreCase))
            {
                return sttEndpoint[..^"/voice/stt".Length] + "/voice/labnote";
            }

            return sttEndpoint.TrimEnd('/') + "/voice/labnote";
        }

        private static string ReasonOrFallback(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return "your issue";
            }
            return reason;
        }

        private string GetLastSpokenOrFallback()
        {
            lock (_lastSpokenLock)
            {
                return string.IsNullOrWhiteSpace(_lastSpokenText) ? RepeatMissingPrompt : _lastSpokenText;
            }
        }

        private void StoreLastSpoken(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            lock (_lastSpokenLock)
            {
                _lastSpokenText = text.Trim();
            }
        }

        private void Trace(string message)
        {
            try
            {
                var line = $"{DateTime.UtcNow:u} | {message}";
                lock (_traceLock)
                {
                    File.AppendAllText(_traceLogPath, line + Environment.NewLine);
                }
                _logger.LogInformation(line);
            }
            catch
            {
                // tracing should never throw
            }
        }

        private void TrackBufferMetrics(int length)
        {
            lock (_bufferLogLock)
            {
                _bufferSampleCount++;
                _bufferSampleBytes += length;
                var now = DateTime.UtcNow;
                if ((now - _bufferWindowStartUtc) >= _bufferLogInterval)
                {
                    Trace($"AppendAudioBuffer summary -> chunks={_bufferSampleCount} bytes={_bufferSampleBytes}");
                    _bufferWindowStartUtc = now;
                    _bufferSampleCount = 0;
                    _bufferSampleBytes = 0;
                }
            }
        }

        private static bool RequiresSnowIntent(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var lower = text.ToLowerInvariant();
            if (lower.Contains("ticket") || lower.Contains("incident") || lower.Contains("status of"))
            {
                return true;
            }
            if (lower.Contains("servicenow"))
            {
                return true;
            }
            if (lower.Contains("vpn") || lower.Contains("password"))
            {
                return true;
            }

            return false;
        }
    }
}
