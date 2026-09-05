using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tomur.Realtime;

internal static partial class RealtimeNativeMethods
{
    [LibraryImport("whisper", EntryPoint = "tomur_realtime_speech_abi")]
    internal static partial int SpeechAbi();
    [LibraryImport("tomur-tts", EntryPoint = "tomur_realtime_tts_abi")]
    internal static partial int TtsAbi();
    [LibraryImport("whisper", EntryPoint = "tomur_realtime_speech_create", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial nint CreateSpeech(string model, string vad, int threads,
        delegate* unmanaged[Cdecl]<nint, int> cancelled, nint user);
    [LibraryImport("whisper", EntryPoint = "tomur_realtime_vad_process")]
    internal static unsafe partial float Vad(SpeechSessionHandle session, float* samples, int count);
    [LibraryImport("whisper", EntryPoint = "tomur_realtime_vad_reset")]
    internal static partial void ResetVad(SpeechSessionHandle session);
    [LibraryImport("whisper", EntryPoint = "tomur_realtime_speech_reset")]
    internal static partial void ResetSpeech(SpeechSessionHandle session);
    [LibraryImport("whisper", EntryPoint = "tomur_realtime_speech_cancel")]
    internal static partial void CancelSpeech(SpeechSessionHandle session);
    [LibraryImport("whisper", EntryPoint = "tomur_realtime_transcribe", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int Transcribe(SpeechSessionHandle session, float* samples, int count,
        string? language, int threads, byte* output, int capacity);
    [LibraryImport("whisper", EntryPoint = "tomur_realtime_speech_destroy")]
    internal static partial void DestroySpeech(nint session);
    [LibraryImport("tomur-tts", EntryPoint = "tomur_realtime_tts_create", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial nint CreateTts(string acoustic, string vocoder,
        delegate* unmanaged[Cdecl]<nint, int> cancelled, nint user);
    [LibraryImport("tomur-tts", EntryPoint = "tomur_realtime_tts_reset")]
    internal static partial void ResetTts(TtsSessionHandle session);
    [LibraryImport("tomur-tts", EntryPoint = "tomur_realtime_tts_cancel")]
    internal static partial void CancelTts(TtsSessionHandle session);
    [LibraryImport("tomur-tts", EntryPoint = "tomur_realtime_tts_synthesize", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int Synthesize(TtsSessionHandle session, string text, int threads,
        delegate* unmanaged[Cdecl]<short*, int, nint, int> callback, nint user);
    [LibraryImport("tomur-tts", EntryPoint = "tomur_realtime_tts_destroy")]
    internal static partial void DestroyTts(nint session);
}

internal sealed class SpeechSessionHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SpeechSessionHandle(nint value) : base(true) => SetHandle(value);
    protected override bool ReleaseHandle() { RealtimeNativeMethods.DestroySpeech(handle); return true; }
}

internal sealed class TtsSessionHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal TtsSessionHandle(nint value) : base(true) => SetHandle(value);
    protected override bool ReleaseHandle() { RealtimeNativeMethods.DestroyTts(handle); return true; }
}
