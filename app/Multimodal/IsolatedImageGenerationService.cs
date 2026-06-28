using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Tomur.Config;
using Tomur.Inference;
using Tomur.Runtime;
using Tomur.Serialization;

namespace Tomur.Multimodal;

public sealed class IsolatedImageGenerationService
{
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromMinutes(30);

    private readonly DataPaths paths;

    public IsolatedImageGenerationService(DataPaths paths)
    {
        this.paths = paths;
    }

    public async Task<NativeImageResult> GenerateImageAsync(
        LocalModelDescriptor model,
        ImageGenerationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);

        var workerDirectory = Path.Combine(Path.GetTempPath(), "tomur-image-worker", Guid.NewGuid().ToString("N"));
        var requestPath = Path.Combine(workerDirectory, "request.json");
        var responsePath = Path.Combine(workerDirectory, "response.json");
        Directory.CreateDirectory(workerDirectory);

        try
        {
            await WriteRequestAsync(requestPath, model.Id, options, cancellationToken).ConfigureAwait(false);
            var run = await RunWorkerAsync(requestPath, responsePath, cancellationToken).ConfigureAwait(false);
            var response = await ReadResponseAsync(responsePath, run, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(response.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                var error = response.Error ?? new RuntimeWorkerError(
                    "image_generation_worker_failed",
                    "The image generation worker returned an error without details.",
                    ["Use /api/runtime/multimodal to inspect image generation readiness."]);
                throw new InferenceException(error.Code, error.Message, error.Actions);
            }

            if (string.IsNullOrWhiteSpace(response.ImageBase64))
            {
                throw new InferenceException(
                    "image_generation_worker_failed",
                    "The image generation worker succeeded without returning image bytes.",
                    ["Use /api/runtime/multimodal to inspect image generation readiness."]);
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(response.ImageBase64);
            }
            catch (FormatException exception)
            {
                throw new InferenceException(
                    "image_generation_worker_failed",
                    "The image generation worker returned invalid base64 image data.",
                    ["Use /api/runtime/multimodal to inspect image generation readiness."],
                    exception);
            }

            return new NativeImageResult(
                bytes,
                string.IsNullOrWhiteSpace(response.Format) ? "png" : response.Format,
                TimeSpan.FromMilliseconds(Math.Max(0, response.ElapsedMs)),
                response.Diagnostics);
        }
        finally
        {
            TryDeleteDirectory(workerDirectory);
        }
    }

    private static async Task WriteRequestAsync(
        string requestPath,
        string model,
        ImageGenerationOptions options,
        CancellationToken cancellationToken)
    {
        await using var stream = File.Create(requestPath);
        await JsonSerializer.SerializeAsync(
                stream,
                new ImageGenerationWorkerRequest(model, options),
                AppJsonSerializerContext.Default.ImageGenerationWorkerRequest,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<WorkerRunResult> RunWorkerAsync(
        string requestPath,
        string responsePath,
        CancellationToken cancellationToken)
    {
        var invocation = ResolveExecutableInvocation();
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(invocation.ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var prefixArgument in invocation.PrefixArguments)
        {
            process.StartInfo.ArgumentList.Add(prefixArgument);
        }

        process.StartInfo.ArgumentList.Add("internal");
        process.StartInfo.ArgumentList.Add("image-worker");
        process.StartInfo.ArgumentList.Add("--data-dir");
        process.StartInfo.ArgumentList.Add(paths.DataDirectory);
        process.StartInfo.ArgumentList.Add("--request");
        process.StartInfo.ArgumentList.Add(requestPath);
        process.StartInfo.ArgumentList.Add("--response");
        process.StartInfo.ArgumentList.Add(responsePath);

        try
        {
            if (!process.Start())
            {
                throw new InferenceException(
                    "image_generation_worker_failed",
                    "The image generation worker process could not be started.",
                    ["Use /api/runtime/multimodal to inspect image generation readiness."]);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new InferenceException(
                "image_generation_worker_failed",
                $"The image generation worker process could not be started: {exception.Message}",
                ["Use /api/runtime/multimodal to inspect image generation readiness."],
                exception);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(WorkerTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new InferenceException(
                "image_generation_worker_timeout",
                $"The image generation worker exceeded the {WorkerTimeout.TotalMinutes:0}-minute timeout.",
                [
                    "Try a smaller image size or fewer generation steps.",
                    "Use /api/runtime/multimodal to inspect image generation readiness."
                ]);
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new WorkerRunResult(process.ExitCode, stdout, stderr);
    }

    private static async Task<ImageGenerationWorkerResponse> ReadResponseAsync(
        string responsePath,
        WorkerRunResult run,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(responsePath))
        {
            throw new InferenceException(
                "image_generation_worker_failed",
                $"The image generation worker exited with code {run.ExitCode} before writing a response.",
                BuildWorkerActions(run));
        }

        try
        {
            await using var stream = File.OpenRead(responsePath);
            return await JsonSerializer.DeserializeAsync(
                    stream,
                    AppJsonSerializerContext.Default.ImageGenerationWorkerResponse,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new JsonException("Worker response was empty.");
        }
        catch (JsonException exception)
        {
            throw new InferenceException(
                "image_generation_worker_failed",
                $"The image generation worker wrote an invalid response: {exception.Message}",
                BuildWorkerActions(run),
                exception);
        }
    }

    private static IReadOnlyList<string> BuildWorkerActions(WorkerRunResult run)
    {
        var actions = new List<string>
        {
            "Use /api/runtime/multimodal to inspect image generation readiness.",
            "Text, ASR, OCR and VLM endpoints are isolated from this image worker failure."
        };

        var stderr = TrimForDiagnostic(run.Stderr);
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            actions.Add($"worker-stderr: {stderr}");
        }

        var stdout = TrimForDiagnostic(run.Stdout);
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            actions.Add($"worker-stdout: {stdout}");
        }

        return actions;
    }

    private static string TrimForDiagnostic(string value)
    {
        var trimmed = value.Trim();
        const int maxLength = 800;
        return trimmed.Length <= maxLength ? trimmed : trimmed[^maxLength..];
    }

    private static WorkerInvocation ResolveExecutableInvocation()
    {
        var processPath = Environment.ProcessPath;
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var processName = string.IsNullOrWhiteSpace(processPath)
            ? string.Empty
            : Path.GetFileNameWithoutExtension(processPath);

        if (!string.IsNullOrWhiteSpace(processPath) &&
            processName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(assemblyPath) &&
            assemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return new WorkerInvocation(processPath, [assemblyPath]);
        }

        if (!string.IsNullOrWhiteSpace(processPath))
        {
            return new WorkerInvocation(processPath, []);
        }

        if (!string.IsNullOrWhiteSpace(assemblyPath))
        {
            return assemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? new WorkerInvocation("dotnet", [assemblyPath])
                : new WorkerInvocation(assemblyPath, []);
        }

        throw new InferenceException(
            "image_generation_worker_failed",
            "The current Tomur executable path could not be resolved.",
            ["Start Tomur from a normal executable host before using image generation."]);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record WorkerRunResult(int ExitCode, string Stdout, string Stderr);

    private sealed record WorkerInvocation(string ExecutablePath, IReadOnlyList<string> PrefixArguments);
}
