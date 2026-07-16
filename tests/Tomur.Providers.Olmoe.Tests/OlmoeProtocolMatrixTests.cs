using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Tomur.Api;
using Tomur.Inference;
using Tomur.Providers;
using Tomur.Runtime;
using Xunit;

namespace Tomur.Providers.Olmoe.Tests;

public sealed class OlmoeProtocolMatrixTests
{
    [Theory]
    [InlineData("openai")]
    [InlineData("ollama")]
    [InlineData("anthropic")]
    public async Task NonStreamingProtocolsSerializeRealOlmoeCompletion(string protocol)
    {
        using var fixture = new OlmoeFixture();
        using var session = new ManagedOlmoeProvider().CreateSession(
            fixture.Descriptor,
            new ModelSessionOptions(8));
        var result = Generate(session);
        var context = CreateContext(out var body);
        await using (body)
        {
            switch (protocol)
            {
                case "openai":
                    await ApiRouteExtensions.WriteOpenAiChatCompletionSuccessAsync(
                        context, fixture.Descriptor.Id, result, stream: false);
                    break;
                case "ollama":
                    await ApiRouteExtensions.WriteOllamaChatSuccessAsync(
                        context, fixture.Descriptor.Id, result, stream: false);
                    break;
                case "anthropic":
                    await ApiRouteExtensions.WriteAnthropicMessageSuccessAsync(
                        context, fixture.Descriptor.Id, result);
                    break;
            }

            using var document = JsonDocument.Parse(body.ToArray());
            var root = document.RootElement;
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.Equal("hello", protocol switch
            {
                "openai" => root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString(),
                "ollama" => root.GetProperty("message").GetProperty("content").GetString(),
                _ => root.GetProperty("content")[0].GetProperty("text").GetString()
            });
        }
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("ollama")]
    [InlineData("anthropic")]
    public async Task StreamingProtocolsEmitRealOlmoeDeltaAndTerminalUsage(string protocol)
    {
        using var fixture = new OlmoeFixture();
        using var session = new ManagedOlmoeProvider().CreateSession(
            fixture.Descriptor,
            new ModelSessionOptions(8));
        var context = CreateContext(out var body);
        await using (body)
        {
            CompletionResult GenerateStream(Action<string> emit)
                => Generate(session, emit);

            switch (protocol)
            {
                case "openai":
                    await ApiRouteExtensions.WriteOpenAiChatCompletionStreamAsync(
                        context, fixture.Descriptor.Id, GenerateStream, CreateDiagnostic);
                    break;
                case "ollama":
                    await ApiRouteExtensions.WriteOllamaChatStreamAsync(
                        context, fixture.Descriptor.Id, GenerateStream, CreateDiagnostic);
                    break;
                case "anthropic":
                    await ApiRouteExtensions.WriteAnthropicMessageStreamAsync(
                        context, fixture.Descriptor.Id, inputTokens: 1, GenerateStream, CreateDiagnostic);
                    break;
            }

            var payload = Encoding.UTF8.GetString(body.ToArray());
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.Contains("hello", payload, StringComparison.Ordinal);
            if (protocol == "ollama")
            {
                var frames = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Assert.True(frames.Length >= 2);
                using var terminal = JsonDocument.Parse(frames[^1]);
                Assert.True(terminal.RootElement.GetProperty("done").GetBoolean());
                Assert.Equal(1, terminal.RootElement.GetProperty("eval_count").GetInt32());
            }
            else if (protocol == "openai")
            {
                Assert.Contains("data: [DONE]", payload, StringComparison.Ordinal);
                Assert.Contains("\"completion_tokens\":1", payload, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("event: message_stop", payload, StringComparison.Ordinal);
                Assert.Contains("\"output_tokens\":1", payload, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void SessionExposesDefinedPerformanceMeasurementsAfterGeneration()
    {
        using var fixture = new OlmoeFixture();
        using var session = new ManagedOlmoeProvider().CreateSession(
            fixture.Descriptor,
            new ModelSessionOptions(8));
        var result = Generate(session);
        var snapshot = session.GetSnapshot();

        Assert.NotNull(snapshot.LoadElapsedMilliseconds);
        Assert.NotNull(snapshot.LastFirstTokenMilliseconds);
        Assert.NotNull(snapshot.LastGenerationMilliseconds);
        Assert.NotNull(snapshot.LastOutputTokensPerSecond);
        Assert.Null(snapshot.LastDecodeTokensPerSecond);
        Assert.Contains(result.Diagnostics, value => value.StartsWith("first token ms: ", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, value => value.StartsWith("output tokens/s: ", StringComparison.Ordinal));
    }

    private static CompletionResult Generate(ITextGenerationSession session, Action<string>? onToken = null)
        => ((IChatGenerationSession)session).GenerateChat(
            [new ChatTurn("user", "hello")],
            CompletionOptions.Default with
            {
                ContextSize = 8,
                MaxOutputTokens = 1,
                Temperature = 0,
                TopK = 8,
                TopP = 1,
                StopSequences = []
            },
            CancellationToken.None,
            onToken);

    private static DefaultHttpContext CreateContext(out MemoryStream body)
    {
        var context = new DefaultHttpContext();
        body = new MemoryStream();
        context.Response.Body = body;
        return context;
    }

    private static RuntimeDiagnostic CreateDiagnostic(InferenceException exception)
        => new("error", exception.Code, exception.Message, "managed-olmoe", exception.Actions);
}
