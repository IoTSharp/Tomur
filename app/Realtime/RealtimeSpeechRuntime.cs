using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Tomur.Inference;
using Tomur.Multimodal;
using Tomur.Native;
using Tomur.Runtime;

namespace Tomur.Realtime;

internal sealed class RealtimeRuntimeFactory(
    LocalModelCatalog catalog, MultimodalExecutionService multimodal,
    LlamaImportResolver imports, NativeRuntimePreference preference,
    LocalInferenceService inference, Conversations.ConversationStore conversations)
{
    public LocalInferenceService Inference => inference;
    public Conversations.ConversationStore Conversations => conversations;

    public LocalModelDescriptor Resolve(string? id, string capability)
        => catalog.ListModels().FirstOrDefault(m =>
            (string.IsNullOrWhiteSpace(id) || m.Id == id) && m.Capabilities.Contains(capability))
            ?? throw new InferenceException("realtime_model_unavailable",
                $"A local {capability} model is required for this voice session.", ["Install the matching local model bundle."]);

    public RealtimeCapabilityStatus GetCapabilities()
    {
        var asr = multimodal.GetBackendStatus("asr").Status;
        var tts = multimodal.GetBackendStatus("tts").Status;
        var current = RealtimeDiagnostics.Snapshot;
        return new("available_unverified", current.ModelsResident ? "connected_degraded" : "requires_session_update",
            current.ModelsResident ? "session_loaded" : "requires_session_probe",
            current.AsrWarm ? "warm_executed" : asr == "ready" ? "model_ready" : asr,
            current.TtsWarm ? "warm_executed" : tts == "ready" ? "model_ready" : tts,
            "degraded_unverified", "pending");
    }

    public RealtimeSpeechRuntime Open(RealtimeSessionConfiguration config, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var asr = Resolve(config.AsrModel, "audio");
        var tts = Resolve(config.TtsModel, "audio-output");
        if (multimodal.GetBackendStatus("asr").Status != "ready" || multimodal.GetBackendStatus("tts").Status != "ready")
            throw Unavailable();
        var text = Resolve(config.Model, "chat");
        var vad = multimodal.ResolveRequiredBundleAsset(asr, "vad");
        var vocoder = multimodal.ResolveRequiredBundleAsset(tts, "wavtokenizer");
        var estimatedBytes = checked((asr.SizeBytes + tts.SizeBytes + text.SizeBytes + new FileInfo(vocoder).Length) * 3 / 2 + 1024L * 1024 * 1024);
        var budget = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (budget > 0 && estimatedBytes > budget)
            throw new InferenceException("realtime_memory_budget_exceeded", "The selected resident voice models exceed the estimated process memory budget.",
                ["Select smaller local models before starting a voice session."]);
        imports.Register();
        using var variant = preference.UsePreferredVariant("cpu");
        try
        {
            if (RealtimeNativeMethods.SpeechAbi() != 1 || RealtimeNativeMethods.TtsAbi() != 1)
                throw Unavailable();
            // An idle ordinary model is not part of the resident voice budget.
            inference.Unload();
            var runtime = new RealtimeSpeechRuntime(asr.AbsolutePath, vad, tts.AbsolutePath, vocoder, cancellationToken);
            return runtime;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw Unavailable();
        }
    }

    private static InferenceException Unavailable() => new("realtime_native_abi_unavailable",
        "The installed speech libraries do not provide Realtime session ABI v1.",
        ["Rebuild the Whisper and TTS native bundles, then run tomur native prepare."]);
}

internal sealed class RealtimeSpeechRuntime : IDisposable
{
    private readonly SpeechSessionHandle speech;
    private readonly TtsSessionHandle tts;
    private readonly int threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
    private readonly float[] vadWindow = new float[512];
    private int vadCount;
    private float probability;

    public unsafe RealtimeSpeechRuntime(string model, string vad, string acoustic, string vocoder, CancellationToken token)
    {
        using var load = CancellationTokenSource.CreateLinkedTokenSource(token);
        load.CancelAfter(TimeSpan.FromSeconds(60));
        var callback = GCHandle.Alloc(load.Token);
        try
        {
            speech = new SpeechSessionHandle(RealtimeNativeMethods.CreateSpeech(model, vad, threads, &LoadCancelled, GCHandle.ToIntPtr(callback)));
            if (speech.IsInvalid) { speech.Dispose(); load.Token.ThrowIfCancellationRequested(); throw Failed("asr_session_create_failed"); }
            try
            {
                load.Token.ThrowIfCancellationRequested();
                tts = new TtsSessionHandle(RealtimeNativeMethods.CreateTts(acoustic, vocoder, &LoadCancelled, GCHandle.ToIntPtr(callback)));
                if (tts.IsInvalid) { tts.Dispose(); load.Token.ThrowIfCancellationRequested(); throw Failed("tts_session_create_failed"); }
                if (load.IsCancellationRequested) { tts.Dispose(); load.Token.ThrowIfCancellationRequested(); }
            }
            catch { speech.Dispose(); throw; }
        }
        finally { callback.Free(); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int LoadCancelled(nint user)
    {
        try { return ((CancellationToken)GCHandle.FromIntPtr(user).Target!).IsCancellationRequested ? 1 : 0; }
        catch { return 1; }
    }

    public unsafe float Detect(ReadOnlySpan<byte> pcm)
    {
        for (var i = 0; i < pcm.Length; i += 2)
        {
            vadWindow[vadCount++] = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i, 2)) / 32768f;
            if (vadCount != vadWindow.Length) continue;
            fixed (float* samples = vadWindow)
                probability = RealtimeNativeMethods.Vad(speech, samples, vadCount);
            vadCount = 0;
            if (!float.IsFinite(probability) || probability is < 0 or > 1) throw Failed("vad_execution_failed");
        }
        return probability;
    }

    public void ResetVad()
    {
        vadCount = 0;
        probability = 0;
        RealtimeNativeMethods.ResetVad(speech);
    }

    public unsafe string Transcribe(byte[] pcm, string? language, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var samples = new float[Math.Max(1600, pcm.Length / 2)];
        for (var i = 0; i < pcm.Length / 2; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2, 2)) / 32768f;
        var output = new byte[16_384];
        try
        {
            RealtimeNativeMethods.ResetSpeech(speech);
            using var abort = token.Register(() => RealtimeNativeMethods.CancelSpeech(speech));
            int length;
            fixed (float* input = samples)
            fixed (byte* text = output)
                length = RealtimeNativeMethods.Transcribe(speech, input, samples.Length, language, threads, text, output.Length);
            token.ThrowIfCancellationRequested();
            if (length < 0) throw Failed(length == -2 ? "asr_timeout" : "asr_execution_failed");
            return Encoding.UTF8.GetString(output, 0, length).Trim();
        }
        finally { Array.Clear(samples); Array.Clear(output); }
    }

    public unsafe void Synthesize(string text, ChannelWriter<byte[]> writer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var callback = new AudioCallback(writer, token);
        var handle = GCHandle.Alloc(callback);
        try
        {
            RealtimeNativeMethods.ResetTts(tts);
            using var abort = token.Register(() => RealtimeNativeMethods.CancelTts(tts));
            var status = RealtimeNativeMethods.Synthesize(tts, text, threads, &OnPcm, GCHandle.ToIntPtr(handle));
            token.ThrowIfCancellationRequested();
            if (callback.Overflow) throw Failed("tts_output_overflow");
            if (status != 0) throw Failed("tts_execution_failed");
        }
        finally { handle.Free(); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int OnPcm(short* pcm, int count, nint user)
    {
        try
        {
            var callback = (AudioCallback)GCHandle.FromIntPtr(user).Target!;
            if (callback.Token.IsCancellationRequested || count is <= 0 or > 2400 || pcm == null) return 0;
            var bytes = new byte[count * 2];
            for (var i = 0; i < count; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), pcm[i]);
            if (callback.Writer.TryWrite(bytes)) return 1;
            callback.Overflow = true;
            return 0;
        }
        catch { return 0; }
    }

    private sealed class AudioCallback(ChannelWriter<byte[]> writer, CancellationToken token)
    {
        public ChannelWriter<byte[]> Writer { get; } = writer;
        public CancellationToken Token { get; } = token;
        public bool Overflow { get; set; }
    }

    public void Dispose()
    {
        try { tts.Dispose(); }
        finally { speech.Dispose(); Array.Clear(vadWindow); }
    }
    private static InferenceException Failed(string code) => new(code,
        "Local speech execution failed or exceeded its resource budget.", ["Inspect the local speech model and native runtime."]);
}
