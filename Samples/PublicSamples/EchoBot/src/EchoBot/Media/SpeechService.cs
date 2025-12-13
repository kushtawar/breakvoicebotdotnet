using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Skype.Bots.Media;
using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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
        private readonly PushAudioInputStream _audioInputStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
        private readonly AudioOutputStream _audioOutputStream = AudioOutputStream.CreatePullStream();

        private readonly SpeechConfig _speechConfig;
        private SpeechRecognizer _recognizer;
        private readonly SpeechSynthesizer _synthesizer;
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

            _speechConfig = SpeechConfig.FromSubscription(settings.SpeechConfigKey, settings.SpeechConfigRegion);
            _speechConfig.SpeechSynthesisLanguage = settings.BotLanguage;
            _speechConfig.SpeechRecognitionLanguage = settings.BotLanguage;

            var audioConfig = AudioConfig.FromStreamOutput(_audioOutputStream);
            _synthesizer = new SpeechSynthesizer(_speechConfig, audioConfig);
            Trace("SpeechService ctor END");
        }

        /// <summary>
        /// Appends the audio buffer.
        /// </summary>
        /// <param name="audioBuffer"></param>
        public async Task AppendAudioBuffer(AudioMediaBuffer audioBuffer)
        {
            Trace($"AppendAudioBuffer START length={audioBuffer?.Length}");
            if (!_isRunning)
            {
                Start();
                await ProcessSpeech();
            }

            try
            {
                // audio for a 1:1 call
                var bufferLength = audioBuffer.Length;
                if (bufferLength > 0)
                {
                    var buffer = new byte[bufferLength];
                    Marshal.Copy(audioBuffer.Data, buffer, 0, (int)bufferLength);

                    _audioInputStream.Write(buffer);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Exception happend writing to input stream");
            }
            finally
            {
                Trace("AppendAudioBuffer END");
            }
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

            if (_isRunning)
            {
                await _recognizer.StopContinuousRecognitionAsync();
                _recognizer.Dispose();
                _audioInputStream.Close();

                _audioInputStream.Dispose();
                _audioOutputStream.Dispose();
                _synthesizer.Dispose();

                _isRunning = false;
            }
            Trace("ShutDownAsync END");
        }

        /// <summary>
        /// Starts this instance.
        /// </summary>
        private void Start()
        {
            Trace("Start START");
            if (!_isRunning)
            {
                _isRunning = true;
            }
            Trace("Start END");
        }

        /// <summary>
        /// Processes this instance.
        /// </summary>
        private async Task ProcessSpeech()
        {
            Trace("ProcessSpeech START");
            try
            {
                var stopRecognition = new TaskCompletionSource<int>();

                using (var audioInput = AudioConfig.FromStreamInput(_audioInputStream))
                {
                    if (_recognizer == null)
                    {
                        _logger.LogInformation("init recognizer");
                        _recognizer = new SpeechRecognizer(_speechConfig, audioInput);
                    }
                }

                _recognizer.Recognizing += (s, e) =>
                {
                    _logger.LogInformation($"RECOGNIZING: Text={e.Result.Text}");
                };

                _recognizer.Recognized += async (s, e) =>
                {
                    if (e.Result.Reason == ResultReason.RecognizedSpeech)
                    {
                        if (string.IsNullOrEmpty(e.Result.Text))
                            return;

                        _logger.LogInformation($"RECOGNIZED: Text={e.Result.Text}");
                        LogRecognizedText(e.Result.Text);
                        var responseBody = await RelayToVoiceEndpointAsync(e.Result.Text);
                        var speechText = e.Result.Text;
                        if (!string.IsNullOrWhiteSpace(responseBody))
                        {
                            var formatted = BuildSpeechResponse(responseBody, speechText);
                            if (!string.IsNullOrWhiteSpace(formatted))
                            {
                                speechText = formatted;
                            }
                        }
                        await TextToSpeech(speechText);
                    }
                    else if (e.Result.Reason == ResultReason.NoMatch)
                    {
                        _logger.LogInformation($"NOMATCH: Speech could not be recognized.");
                    }
                };

                _recognizer.Canceled += (s, e) =>
                {
                    _logger.LogInformation($"CANCELED: Reason={e.Reason}");

                    if (e.Reason == CancellationReason.Error)
                    {
                        _logger.LogInformation($"CANCELED: ErrorCode={e.ErrorCode}");
                        _logger.LogInformation($"CANCELED: ErrorDetails={e.ErrorDetails}");
                        _logger.LogInformation($"CANCELED: Did you update the subscription info?");
                    }

                    stopRecognition.TrySetResult(0);
                };

                _recognizer.SessionStarted += async (s, e) =>
                {
                    _logger.LogInformation("\nSession started event.");
                    await TextToSpeech("Hello");
                };

                _recognizer.SessionStopped += (s, e) =>
                {
                    _logger.LogInformation("\nSession stopped event.");
                    _logger.LogInformation("\nStop recognition.");
                    stopRecognition.TrySetResult(0);
                };

                // Starts continuous recognition. Uses StopContinuousRecognitionAsync() to stop recognition.
                await _recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);

                // Waits for completion.
                // Use Task.WaitAny to keep the task rooted.
                Task.WaitAny(new[] { stopRecognition.Task });

                // Stops recognition.
                await _recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogError(ex, "The queue processing task object has been disposed.");
            }
            catch (Exception ex)
            {
                // Catch all other exceptions and log
                _logger.LogError(ex, "Caught Exception");
            }

            _isDraining = false;
            Trace("ProcessSpeech END");
        }

        private async Task TextToSpeech(string text)
        {
            Trace($"TextToSpeech START text=\"{text}\"");
            // convert the text to speech
            SpeechSynthesisResult result = await _synthesizer.SpeakTextAsync(text);
            // take the stream of the result
            // create 20ms media buffers of the stream
            // and send to the AudioSocket in the BotMediaStream
            using (var stream = AudioDataStream.FromResult(result))
            {
                var currentTick = DateTime.Now.Ticks;
                MediaStreamEventArgs args = new MediaStreamEventArgs
                {
                    AudioMediaBuffers = Util.Utilities.CreateAudioMediaBuffers(stream, currentTick, _logger)
                };
                OnSendMediaBufferEventArgs(this, args);
            }
            Trace("TextToSpeech END");
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

        private string? BuildSpeechResponse(string responseBody, string fallbackRecognizedText)
        {
            Trace("BuildSpeechResponse START");
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

        private static string ReasonOrFallback(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return "your issue";
            }
            return reason;
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
    }
}
