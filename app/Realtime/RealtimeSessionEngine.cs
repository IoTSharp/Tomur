using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Tomur.Conversations;
using Tomur.Inference;
using Tomur.Runtime;

namespace Tomur.Realtime;

internal sealed class RealtimeSessionEngine : IAsyncDisposable
{
    private readonly RealtimeRuntimeFactory factory;
    private readonly Func<RealtimePipelineUpdate, CancellationToken, Task> emit;
    private readonly Func<byte[], long, CancellationToken, Task> sendAudio;
    private readonly CancellationTokenSource lifetime;
    private readonly RealtimeResourceCoordinator.Reservation reservation;
    private readonly RealtimeUtteranceBuffer input = new();
    private readonly object responseGate = new();
    private RealtimeSpeechRuntime? runtime;
    private LocalModelDescriptor? model;
    private CancellationTokenSource? partialCancellation;
    private Task partialTask = Task.CompletedTask;
    private Task responseTask = Task.CompletedTask;
    private Task drainingTask = Task.CompletedTask;
    private Response? response;
    private string utteranceId = Guid.NewGuid().ToString("N");
    private string? captureStreamId;
    private int lastPartialBytes;
    private int turns;
    private long epoch;
    private int disposed;
    private string lastPartial = string.Empty;

    public RealtimeSessionConfiguration Configuration { get; private set; }
    public string State { get; private set; } = "connecting";
    public int BufferedBytes => input.Length;
    public CancellationToken Stopping { get; }

    public RealtimeSessionEngine(RealtimeRuntimeFactory factory, RealtimeSessionConfiguration configuration,
        Func<RealtimePipelineUpdate, CancellationToken, Task> emit,
        Func<byte[], long, CancellationToken, Task> sendAudio, CancellationToken token)
    {
        this.factory = factory;
        this.emit = emit;
        this.sendAudio = sendAudio;
        Configuration = configuration;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Stopping = lifetime.Token;
        lifetime.CancelAfter(RealtimeProtocol.MaximumSessionDuration);
        try { reservation = RealtimeResourceCoordinator.Reserve(lifetime); }
        catch { lifetime.Dispose(); throw; }
        RealtimeDiagnostics.Start();
    }

    public async Task OpenAsync()
    {
        try
        {
            using var scope = reservation.Enter();
            model = factory.Resolve(Configuration.Model, "chat");
            var conversation = string.IsNullOrWhiteSpace(Configuration.ConversationId)
                ? factory.Conversations.Create(new ConversationCreateRequest("Voice", model.Id, null))
                : factory.Conversations.Get(Configuration.ConversationId, 1).Conversation;
            Configuration = Configuration with { ConversationId = conversation.Id, Model = model.Id };
            runtime = await Task.Run(() => factory.Open(Configuration, lifetime.Token), lifetime.Token).ConfigureAwait(false);
            RealtimeDiagnostics.Update(status => status with { ModelsResident = true });
            await StateAsync("listening").ConfigureAwait(false);
            await emit(new("session.ready", State, ConversationId: conversation.Id,
                Status: "models_resident_degraded_unverified"), lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            RealtimeDiagnostics.Update(status => status with { LastError = error is InferenceException inference
                ? inference.Code : error is OperationCanceledException ? "session_open_cancelled" : "realtime_session_open_failed" });
            throw;
        }
    }

    public bool IsCurrent(long value)
    {
        lock (responseGate) return response is { } current && current.Epoch == value && !current.Cancellation.IsCancellationRequested;
    }

    public bool TryStartResponseSend(long value, int? textCharacters, long? audioSequence, Func<ValueTask> send, out ValueTask operation)
    {
        lock (responseGate)
        {
            var current = response;
            if (current is null || current.Epoch != value || current.Cancellation.IsCancellationRequested)
            {
                operation = default;
                return false;
            }
            operation = send();
            if (textCharacters is { } characters) current.SentCharacters = Math.Max(current.SentCharacters, characters);
            if (audioSequence is { } sequence) current.SentAudioSequence = Math.Max(current.SentAudioSequence, sequence);
            return true;
        }
    }

    public void RequestStop()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async Task PushAsync(ReadOnlyMemory<byte> pcm, Guid capture)
    {
        lifetime.Token.ThrowIfCancellationRequested();
        if (runtime is null) throw new RealtimeTransportException("session_not_ready", "Configure the voice session before sending audio.");
        // Without validated AEC the microphone stream still advances, but output
        // cannot trigger a new turn. Clients can explicitly cancel for push-to-talk.
        if (Configuration.Mode == "half_duplex" && IsResponding()) return;
        captureStreamId = capture.ToString("N");
        var detection = input.Push(pcm.Span, Configuration.TurnDetection == "manual" ? 1 : runtime.Detect(pcm.Span),
            Configuration.TurnDetection == "manual");
        RealtimeDiagnostics.Update(status => status with { InputBufferedBytes = input.Length });
        if (detection.Started)
        {
            await InterruptAsync(null, "barge_in").ConfigureAwait(false);
            await StateAsync("user_speaking").ConfigureAwait(false);
            await emit(new("input_audio_buffer.speech_started", State, UtteranceId: utteranceId,
                CaptureStreamId: captureStreamId), lifetime.Token).ConfigureAwait(false);
        }
        if (detection.Stopped)
        {
            await CommitAsync().ConfigureAwait(false);
            return;
        }
        if (input.Speaking && input.Length - lastPartialBytes >= 32_000 && partialTask.IsCompleted && responseTask.IsCompleted)
        {
            lastPartialBytes = input.Length;
            partialCancellation?.Dispose();
            partialCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            partialCancellation.CancelAfter(TimeSpan.FromSeconds(4));
            var token = partialCancellation.Token;
            var id = utteranceId;
            var captureId = captureStreamId;
            var audio = input.Snapshot(128_000);
            partialTask = Task.Run(async () =>
            {
                try
                {
                    var text = runtime.Transcribe(audio, Configuration.Language, token);
                    if (id == utteranceId && text.Length > 0 && text != lastPartial && !token.IsCancellationRequested)
                    {
                        lastPartial = text;
                        await emit(new("input_audio_transcription.delta", "user_speaking", UtteranceId: id, Text: text,
                            CaptureStreamId: captureId), token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (InferenceException) { /* Final transcription reports authoritative failures. */ }
                finally { Array.Clear(audio); }
            }, CancellationToken.None);
        }
    }

    public Task CommitAsync(string? requestedUtteranceId = null)
    {
        if (requestedUtteranceId is not null && (!Guid.TryParse(requestedUtteranceId, out var parsedId) || parsedId == Guid.Empty))
            throw new RealtimeTransportException("utterance_id_invalid", "utterance_id must be a non-empty UUID.");
        if (!input.Speaking || input.Length == 0)
            throw new RealtimeTransportException("input_audio_buffer_empty", "No active utterance is available to commit.");
        if (++turns > 100) throw new RealtimeTransportException("turn_limit_exceeded", "The session limit is 100 turns.");
        // At most one cancelled response may still be draining. Never chain an
        // unbounded set of inference tasks behind a slow native operation.
        if (!drainingTask.IsCompleted)
            throw new RealtimeTransportException("response_cancellation_pending", "The previous native response is still stopping; reconnect after it releases resources.");
        partialCancellation?.Cancel();
        var partial = partialTask;
        var audio = input.Snapshot();
        Response? next = null;
        try
        {
            var id = requestedUtteranceId is null ? utteranceId : Guid.Parse(requestedUtteranceId).ToString("N");
            input.Clear();
            runtime!.ResetVad();
            utteranceId = Guid.NewGuid().ToString("N");
            lastPartialBytes = 0;
            lastPartial = string.Empty;
            next = new Response(Interlocked.Increment(ref epoch), captureStreamId, lifetime.Token);
            var previousTask = responseTask;
            var previousResponse = response;
            var previousCompleted = previousTask.IsCompleted;
            RealtimeDiagnostics.Update(status => status with { Turns = turns, InputBufferedBytes = 0, OutputUnconsumedMs = 0, FirstAudioMs = null });
            FinalizeResponse(dispose: previousCompleted);
            drainingTask = previousTask;
            lock (responseGate) response = next;
            // The tracked response owns the snapshot and any draining cancellation
            // source before it can attempt a fallible transport operation.
            responseTask = RunResponseAsync(next, audio, id, partial, previousTask, previousCompleted ? null : previousResponse);
        }
        catch
        {
            Array.Clear(audio);
            next?.Cancellation.Dispose();
            throw;
        }
        return Task.CompletedTask;
    }

    private async Task RunResponseAsync(Response current, byte[] audio, string id, Task partial, Task previousTask, Response? previous)
    {
        using var scope = reservation.Enter();
        var token = current.Cancellation.Token;
        var previousCleanup = DrainPreviousResponseAsync(previousTask, previous);
        try
        {
            token.ThrowIfCancellationRequested();
            await emit(new("input_audio_buffer.committed", "transcribing", UtteranceId: id,
                BufferedAudioBytes: audio.Length, DurationMs: audio.Length / 32, CaptureStreamId: current.CaptureStreamId), token).ConfigureAwait(false);
            await emit(new("input_audio_buffer.speech_stopped", "transcribing", UtteranceId: id,
                CaptureStreamId: current.CaptureStreamId), token).ConfigureAwait(false);
            await StateAsync("transcribing", current).ConfigureAwait(false);
            await EmitResponseAsync(current, "response.created").ConfigureAwait(false);
            await previousCleanup.ConfigureAwait(false);
            await partial.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var asrStarted = Stopwatch.GetTimestamp();
            var text = await Task.Run(() => runtime!.Transcribe(audio, Configuration.Language, token), token).ConfigureAwait(false);
            RealtimeDiagnostics.Update(status => status with { AsrWarm = true, AsrFinalMs = (long)Stopwatch.GetElapsedTime(asrStarted).TotalMilliseconds });
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text)) throw new InferenceException("transcript_empty", "No speech was recognized.", []);
            factory.Conversations.AppendMessage(Configuration.ConversationId!,
                new("user", text, "audio", "ok", model!.Id, null, null, null, null));
            await EmitResponseAsync(current, "input_audio_transcription.done", text: text, utterance: id).ConfigureAwait(false);
            var history = factory.Conversations.ListRecentMessages(Configuration.ConversationId!, 24)
                .Where(m => m.Role is "user" or "assistant" or "system")
                .Select(m => new ChatTurn(m.Role, m.Content)).ToArray();
            await StateAsync("thinking", current).ConfigureAwait(false);
            await GenerateAsync(current, history).ConfigureAwait(false);
            await WaitForPlaybackAsync(current, 0).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (responseGate) { current.Completed = true; PersistResponse(current); }
            await EmitResponseAsync(current, "response.done").ConfigureAwait(false);
            await StateAsync("listening", current).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            current.Failed = !current.Interrupted;
            if (!lifetime.IsCancellationRequested && !current.Interrupted)
                await ErrorAsync("response_timeout", "The response exceeded its time budget.").ConfigureAwait(false);
        }
        catch (Exception error)
        {
            current.Failed = true;
            if (!lifetime.IsCancellationRequested && !current.Interrupted)
                await ErrorAsync(error is InferenceException inference ? inference.Code : "realtime_execution_failed",
                    error is InferenceException ? error.Message : "The local voice response failed.").ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(audio);
            if (!current.Completed) current.Cancellation.Cancel();
            // Transport failure before inference still owns the prior task's cleanup.
            try { await previousCleanup.ConfigureAwait(false); }
            catch { /* The originating response reports execution failures. */ }
            lock (responseGate) PersistResponse(current);
        }
    }

    private static async Task DrainPreviousResponseAsync(Task task, Response? previous)
    {
        try { await task.ConfigureAwait(false); }
        finally { previous?.Cancellation.Dispose(); }
    }

    private async Task GenerateAsync(Response current, ChatTurn[] history)
    {
        var token = current.Cancellation.Token;
        var tokens = Channel.CreateBounded<string>(256);
        var segments = Channel.CreateBounded<(string Text, int End)>(8);
        var generation = Task.Run(() =>
        {
            try
            {
                factory.Inference.Chat(model!, history, CompletionOptions.Default with { MaxOutputTokens = 512 }, token,
                    delta =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (!tokens.Writer.TryWrite(delta)) throw new InferenceException("text_queue_overflow", "The bounded token queue is full.", []);
                    });
                tokens.Writer.TryComplete();
            }
            catch (Exception error) { tokens.Writer.TryComplete(error); throw; }
        }, CancellationToken.None);
        var synthesis = SpeakAsync(current, segments.Reader);
        try
        {
            var segmenter = new RealtimeTextSegmenter();
            var segmentEnd = 0;
            var lastFlush = Stopwatch.GetTimestamp();
            for (var iteration = 0; iteration < 8192; iteration++)
            {
                token.ThrowIfCancellationRequested();
                string? delta = null;
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(token);
                tick.CancelAfter(TimeSpan.FromMilliseconds(250));
                var ended = false;
                try
                {
                    delta = await tokens.Reader.ReadAsync(tick.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                catch (ChannelClosedException error) when (error.InnerException is null) { ended = true; }
                if (delta is not null)
                {
                    lock (responseGate)
                    {
                        if (current.Generated.Length + delta.Length > 8192) throw new InferenceException("response_text_limit", "The voice text limit was exceeded.", []);
                        current.Generated.Append(delta);
                    }
                    segmenter.Append(delta);
                    await EmitResponseAsync(current, "response.text.delta", delta: delta).ConfigureAwait(false);
                }
                var flush = ended || Stopwatch.GetElapsedTime(lastFlush) >= TimeSpan.FromMilliseconds(750);
                for (var batch = 0; batch < 64; batch++)
                {
                    var segment = segmenter.Take(flush);
                    if (segment is null) break;
                    segmentEnd += segment.Length;
                    if (!segments.Writer.TryWrite((segment, segmentEnd)))
                        throw new InferenceException("tts_text_queue_overflow", "Speech synthesis cannot keep up with generated text.", []);
                    lastFlush = Stopwatch.GetTimestamp();
                }
                if (ended) break;
            }
            await generation.ConfigureAwait(false);
            segments.Writer.TryComplete();
            await EmitResponseAsync(current, "response.text.done").ConfigureAwait(false);
            await synthesis.ConfigureAwait(false);
            await EmitResponseAsync(current, "response.audio.done").ConfigureAwait(false);
        }
        finally
        {
            segments.Writer.TryComplete();
            if (!generation.IsCompletedSuccessfully || !synthesis.IsCompletedSuccessfully) current.Cancellation.Cancel();
            try { await Task.WhenAll(generation, synthesis).ConfigureAwait(false); }
            catch { current.Cancellation.Cancel(); }
        }
    }

    private async Task SpeakAsync(Response current, ChannelReader<(string Text, int End)> segments)
    {
        var token = current.Cancellation.Token;
        for (var segmentIndex = 0; segmentIndex < 128 && await segments.WaitToReadAsync(token).ConfigureAwait(false); segmentIndex++)
        {
            if (!segments.TryRead(out var segment)) continue;
            if (string.IsNullOrWhiteSpace(segment.Text)) continue;
            var audio = Channel.CreateBounded<byte[]>(320);
            var producer = Task.Run(() =>
            {
                try { runtime!.Synthesize(segment.Text, audio.Writer, token); audio.Writer.TryComplete(); }
                catch (Exception error) { audio.Writer.TryComplete(error); throw; }
            }, CancellationToken.None);
            try
            {
                var pending = new List<byte[]>(300);
                for (var chunk = 0; chunk < 301 && await audio.Reader.WaitToReadAsync(token).ConfigureAwait(false); chunk++)
                    if (audio.Reader.TryRead(out var bytes)) pending.Add(bytes);
                await producer.ConfigureAwait(false);
                if (pending.Count == 0) throw new InferenceException("tts_audio_empty", "Speech synthesis returned no audio.", []);
                for (var chunk = 0; chunk < pending.Count; chunk++)
                {
                    token.ThrowIfCancellationRequested();
                    var bytes = pending[chunk];
                    // Limit unconsumed audio to two seconds. A slow client has
                    // five seconds to advance its actual playback timeline.
                    await WaitForPlaybackAsync(current, 1_900_000).ConfigureAwait(false);
                    await StateAsync("speaking", current).ConfigureAwait(false);
                    var sequence = ++current.AudioSequence;
                    if (sequence == 1) RealtimeDiagnostics.Update(status => status with {
                        TtsWarm = true, FirstAudioMs = (long)Stopwatch.GetElapsedTime(current.Started).TotalMilliseconds });
                    var timestamp = current.AudioTimestampUs;
                    current.AudioTimestampUs += bytes.Length / 2 * 1_000_000L / 24_000;
                    RealtimeDiagnostics.Update(status => status with { OutputUnconsumedMs = (current.AudioTimestampUs - current.PlayedTimestampUs) / 1000 });
                    lock (responseGate) current.AudioBoundaries.Add((sequence, current.AudioTimestampUs, chunk == pending.Count - 1 ? segment.End : current.PlayableCharacters));
                    if (chunk == pending.Count - 1) current.PlayableCharacters = segment.End;
                    var frame = new byte[RealtimeProtocol.BinaryHeaderSize + bytes.Length];
                    RealtimeBinaryFrameCodec.WriteHeader(frame, new(RealtimeBinaryFrameKind.OutputAudio,
                        current.Id, (ulong)sequence, timestamp, bytes.Length));
                    bytes.CopyTo(frame, RealtimeProtocol.BinaryHeaderSize);
                    await sendAudio(frame, current.Epoch, token).ConfigureAwait(false);
                }
            }
            finally
            {
                if (!producer.IsCompleted) current.Cancellation.Cancel();
                try { await producer.ConfigureAwait(false); } catch { current.Cancellation.Cancel(); }
            }
        }
        if (await segments.WaitToReadAsync(token).ConfigureAwait(false))
            throw new InferenceException("tts_segment_limit", "The response exceeds the limit of 128 spoken segments.", []);
    }

    private static async Task WaitForPlaybackAsync(Response current, long maximumOutstandingUs)
    {
        var waiting = Stopwatch.GetTimestamp();
        for (var attempt = 0; attempt < 250; attempt++)
        {
            current.Cancellation.Token.ThrowIfCancellationRequested();
            if (current.AudioTimestampUs - Interlocked.Read(ref current.PlayedTimestampUs) <= maximumOutstandingUs) return;
            if (Stopwatch.GetElapsedTime(waiting) >= TimeSpan.FromSeconds(5)) break;
            await Task.Delay(20, current.Cancellation.Token).ConfigureAwait(false);
        }
        throw new InferenceException("playback_backpressure", "The client did not consume output audio within five seconds.", []);
    }

    public async Task InterruptAsync(long? requestedEpoch, string reason = "client_requested")
    {
        Response? current;
        lock (responseGate)
        {
            current = response;
            if (current is null || (requestedEpoch is not null && current.Epoch != requestedEpoch)) current = null;
            else
            {
                if (reason == "barge_in" && current.Completed && current.PlayedTimestampUs >= current.AudioTimestampUs) return;
                current.Interrupted = true;
                current.Cancellation.Cancel();
                PersistResponse(current);
            }
        }
        if (current is null)
        {
            if (requestedEpoch is { } requested)
                await emit(new("response.cancelled", State, requested, Status: "not_active"), lifetime.Token).ConfigureAwait(false);
            return;
        }
        await emit(new("response.cancelled", "interrupted", current.Epoch, current.Id.ToString("N"),
            current.ItemId, Status: reason), lifetime.Token).ConfigureAwait(false);
        await StateAsync("listening").ConfigureAwait(false);
    }

    public void Acknowledge(RealtimeTextDisplayedEvent acknowledgement)
    {
        lock (responseGate)
        {
            var current = response;
            if (current is null || current.Epoch != acknowledgement.ResponseEpoch || current.ItemId != acknowledgement.ItemId) return;
            if (acknowledgement.CharacterCount < current.Displayed || acknowledgement.CharacterCount > current.SentCharacters ||
                (acknowledgement.CharacterCount > 0 && char.IsHighSurrogate(current.Generated[acknowledgement.CharacterCount - 1])))
                throw new RealtimeTransportException("invalid_display_ack", "The displayed prefix exceeds the emitted text or moves backwards.");
            current.Displayed = acknowledgement.CharacterCount;
            PersistResponse(current);
        }
    }

    public void Acknowledge(RealtimePlaybackConsumedEvent acknowledgement)
    {
        lock (responseGate)
        {
            var current = response;
            if (current is null || current.Epoch != acknowledgement.ResponseEpoch) return;
            var boundary = current.AudioBoundaries.FirstOrDefault(b => b.Sequence == acknowledgement.AudioSequence);
            if (boundary.Sequence == 0 || boundary.Sequence > current.SentAudioSequence || boundary.Timestamp != acknowledgement.PlayedThroughTimestampUs || boundary.Timestamp < current.PlayedTimestampUs)
                throw new RealtimeTransportException("invalid_playback_ack", "Playback acknowledgement does not match an emitted audio boundary.");
            current.PlayedTimestampUs = boundary.Timestamp;
            RealtimeDiagnostics.Update(status => status with { OutputUnconsumedMs = (current.AudioTimestampUs - current.PlayedTimestampUs) / 1000 });
            current.PlayedCharacters = Math.Max(current.PlayedCharacters, boundary.Characters);
            current.AudioBoundaries.RemoveAll(b => b.Sequence < boundary.Sequence);
            PersistResponse(current);
        }
    }

    public void Clear()
    {
        partialCancellation?.Cancel();
        input.Clear();
        runtime?.ResetVad();
        utteranceId = Guid.NewGuid().ToString("N");
        captureStreamId = null;
        lastPartialBytes = 0;
        RealtimeDiagnostics.Update(status => status with { InputBufferedBytes = 0 });
        if (!IsResponding()) State = "listening";
    }

    private bool IsResponding()
    {
        lock (responseGate) return response is { } current && !current.Cancellation.IsCancellationRequested &&
            (!responseTask.IsCompleted || current.PlayedTimestampUs < current.AudioTimestampUs);
    }

    private void PersistResponse(Response current)
    {
        var count = Math.Max(current.Displayed, current.PlayedCharacters);
        if (count == 0) return;
        var status = current.Interrupted ? "interrupted" : current.Failed ? "error" : current.Completed ? "ok" : "streaming";
        if (count == current.PersistedCharacters && status == current.PersistedStatus) return;
        var content = current.Generated.ToString(0, count);
        if (current.StoredItemId is null)
            current.StoredItemId = factory.Conversations.AppendMessage(Configuration.ConversationId!,
                new("assistant", content, "audio", status, model!.Id, null, null, null, null)).Message.Id;
        else factory.Conversations.UpdateAcknowledgedVoiceMessage(Configuration.ConversationId!, current.StoredItemId, content, status);
        current.PersistedCharacters = count;
        current.PersistedStatus = status;
    }

    private void FinalizeResponse(bool dispose = true)
    {
        lock (responseGate)
        {
            if (response is not { } current) return;
            PersistResponse(current);
            if (dispose) current.Cancellation.Dispose();
            response = null;
        }
    }

    private Task EmitResponseAsync(Response current, string type, string? text = null, string? delta = null, string? utterance = null)
    {
        current.Cancellation.Token.ThrowIfCancellationRequested();
        return emit(new(type, State, current.Epoch, current.Id.ToString("N"), current.ItemId,
            utterance, text, delta, AudioSequence: current.AudioSequence, CharacterCount: current.Generated.Length,
            CaptureStreamId: utterance is null ? null : current.CaptureStreamId), current.Cancellation.Token);
    }

    private Task StateAsync(string state, Response? current = null)
    {
        lock (responseGate)
        {
            if (current is not null && (response != current || current.Cancellation.IsCancellationRequested)) return Task.CompletedTask;
            if (State == state) return Task.CompletedTask;
            State = state;
        }
        RealtimeDiagnostics.Update(status => status with { State = state });
        return emit(new("session.state", state, current?.Epoch), current?.Cancellation.Token ?? lifetime.Token);
    }

    private async Task ErrorAsync(string code, string message)
    {
        RealtimeDiagnostics.Update(status => status with { LastError = code });
        await emit(new("error", "failed", Code: code, Message: message), lifetime.Token).ConfigureAwait(false);
        await StateAsync("listening").ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        partialCancellation?.Cancel();
        lock (responseGate)
        {
            if (response is { } current)
            {
                if (!current.Completed) current.Interrupted = true;
                current.Cancellation.Cancel();
            }
        }
        try { await Task.WhenAll(responseTask, partialTask, drainingTask).ConfigureAwait(false); }
        finally
        {
            try { FinalizeResponse(); }
            finally
            {
                input.Clear();
                try { runtime?.Dispose(); }
                finally
                {
                    partialCancellation?.Dispose();
                    reservation.Dispose();
                    lifetime.Dispose();
                    State = "closed";
                    RealtimeDiagnostics.Update(status => status with { State = "closed", ModelsResident = false, AsrWarm = false, TtsWarm = false, InputBufferedBytes = 0, OutputUnconsumedMs = 0 });
                }
            }
        }
    }

    private sealed class Response
    {
        public Response(long epoch, string? captureStreamId, CancellationToken token)
        {
            Epoch = epoch;
            CaptureStreamId = captureStreamId;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Cancellation.CancelAfter(TimeSpan.FromSeconds(120));
        }
        public long Epoch { get; }
        public string? CaptureStreamId { get; }
        public long Started { get; } = Stopwatch.GetTimestamp();
        public Guid Id { get; } = Guid.NewGuid();
        public string ItemId { get; } = Guid.NewGuid().ToString("N");
        public CancellationTokenSource Cancellation { get; }
        public StringBuilder Generated { get; } = new();
        public List<(long Sequence, long Timestamp, int Characters)> AudioBoundaries { get; } = [];
        public long AudioSequence;
        public long AudioTimestampUs;
        public long PlayedTimestampUs;
        public int Displayed;
        public int SentCharacters;
        public long SentAudioSequence;
        public int PlayedCharacters;
        public int PlayableCharacters;
        public bool Completed;
        public bool Failed;
        public bool Interrupted;
        public string? StoredItemId;
        public int PersistedCharacters;
        public string? PersistedStatus;
    }
}
