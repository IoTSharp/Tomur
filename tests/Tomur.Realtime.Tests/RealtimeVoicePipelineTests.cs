using Tomur.Inference;

namespace Tomur.Realtime.Tests;

public sealed class RealtimeVoicePipelineTests
{
    [Fact]
    public void EndpointRetainsSpeechOnsetAndRequiresSustainedSilence()
    {
        var input = new RealtimeUtteranceBuffer();
        var silence = new byte[640];
        for (var i = 0; i < 50; i++) Assert.False(input.Push(silence, 0, false).Started);
        Assert.Equal(9600, input.Length);
        var onset = Enumerable.Repeat((byte)7, 640).ToArray();
        for (var i = 0; i < 4; i++) Assert.False(input.Push(onset, 0.9f, false).Started);
        Assert.True(input.Push(onset, 0.9f, false).Started);
        Assert.True(input.Snapshot().AsSpan(input.Length - 3200).ToArray().All(b => b == 7));
        for (var i = 0; i < 29; i++) Assert.False(input.Push(silence, 0, false).Stopped);
        Assert.True(input.Push(silence, 0, false).Stopped);
        input.Clear();
        Assert.False(input.Speaking);
        Assert.Empty(input.Snapshot());
    }

    [Fact]
    public void ShortNoiseDoesNotCreateAnUtterance()
    {
        var input = new RealtimeUtteranceBuffer();
        var frame = new byte[640];
        for (var i = 0; i < 4; i++) input.Push(frame, 1, false);
        input.Push(frame, 0, false);
        Assert.False(input.Speaking);
        Assert.False(input.Push(frame, 1, false).Started);
    }

    [Fact]
    public void ManualUtteranceFailsAtThirtySecondsWithoutDroppingAudio()
    {
        var input = new RealtimeUtteranceBuffer();
        var frame = new byte[640];
        for (var i = 0; i < 1500; i++) input.Push(frame, 0, true);
        Assert.Equal(960_000, input.Length);
        Assert.Equal("utterance_too_long", Assert.Throws<RealtimeTransportException>(() => input.Push(frame, 0, true)).Code);
        Assert.Equal(960_000, input.Length);
    }

    [Fact]
    public void SegmentBoundariesPreserveSurrogatePairsAndMultilingualPunctuation()
    {
        var input = new RealtimeTextSegmenter();
        var text = new string('x', 159) + "\ud83d\ude42" + "\u4f60\u597d\u3002";
        input.Append(text);
        var first = input.Take(false)!;
        var second = input.Take(true)!;
        Assert.False(char.IsHighSurrogate(first[^1]));
        Assert.Equal(text, first + second);
        Assert.Null(input.Take(true));
    }

    [Fact]
    public void ReservationRejectsConcurrentBatchInferenceUntilCleanup()
    {
        using var cancellation = new CancellationTokenSource();
        using (var reservation = RealtimeResourceCoordinator.Reserve(cancellation))
        {
            Assert.Equal("realtime_resource_busy", Assert.Throws<InferenceException>(() => RealtimeResourceCoordinator.EnterOperation()).Code);
            using (reservation.Enter())
            using (RealtimeResourceCoordinator.EnterOperation()) { }
            Assert.Throws<InferenceException>(() => RealtimeResourceCoordinator.EnterOperation(interrupt: true));
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Throws<InferenceException>(() => RealtimeResourceCoordinator.EnterOperation());
        }
        using var operation = RealtimeResourceCoordinator.EnterOperation();
        Assert.Throws<InferenceException>(() => RealtimeResourceCoordinator.Reserve(cancellation));
    }
}
