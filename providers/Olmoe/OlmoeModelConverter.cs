using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Tomur.Providers.Glm;
using Tomur.Runtime;

namespace Tomur.Providers.Olmoe;

internal static class OlmoeModelConverter
{
    private const string OutputTensorFile = "model.int8.safetensors";
    private const string ConversionManifestFile = "conversion.manifest.json";
    private const int CopyBufferBytes = 1024 * 1024;

    public static ModelConversionResult Convert(
        ModelConversionRequest request,
        Action<ModelConversionProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);
        var sourceDirectory = Path.GetFullPath(request.SourceDirectory);
        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        ValidateDirectories(sourceDirectory, outputDirectory);

        var stopwatch = Stopwatch.StartNew();
        var sourceManifestPath = Path.Combine(sourceDirectory, ModelProviderManifest.FileName);
        var sourceInfo = new FileInfo(sourceManifestPath);
        var descriptor = new LocalModelDescriptor(
            "conversion-source",
            "OLMoE conversion source",
            ModelProviderManifest.FileName,
            ModelProviderManifest.FileName,
            sourceManifestPath,
            sourceInfo.Exists ? sourceInfo.Length : 0,
            sourceInfo.Exists ? sourceInfo.LastWriteTimeUtc : DateTime.MinValue,
            "managed-model",
            "olmoe",
            "source",
            ["completion", "chat"]);
        var probe = OlmoeModelDirectoryProbe.Read(descriptor, ManagedOlmoeProvider.ProviderId);
        if (!IsFloating(probe.Manifest.Quantization))
        {
            throw new InvalidDataException(
                $"OLMoE rowwise int8 conversion requires an F32, F16 or BF16 source, found '{probe.Manifest.Quantization}'.");
        }

        var outputTensors = BuildOutputTensors(probe.Tensors);
        var estimatedPayloadBytes = outputTensors.Sum(static tensor => tensor.Length);
        EnsureDiskCapacity(outputDirectory, estimatedPayloadBytes);

        var parentDirectory = Path.GetDirectoryName(outputDirectory)
            ?? throw new InvalidDataException("The output directory must have a parent directory.");
        Directory.CreateDirectory(parentDirectory);
        var stagingDirectory = Path.Combine(
            parentDirectory,
            $".{Path.GetFileName(outputDirectory)}.partial-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyAuxiliaryFiles(probe, stagingDirectory, cancellationToken);
            WriteProviderManifest(probe.Manifest, stagingDirectory);
            var outputTensorPath = Path.Combine(stagingDirectory, OutputTensorFile);
            WriteTensorFile(
                probe.Tensors,
                outputTensors,
                outputTensorPath,
                estimatedPayloadBytes,
                onProgress,
                cancellationToken);

            onProgress?.Invoke(new ModelConversionProgress(
                "checksum",
                null,
                outputTensors.Count,
                outputTensors.Count,
                new FileInfo(outputTensorPath).Length,
                estimatedPayloadBytes));
            var outputSha256 = ComputeSha256(outputTensorPath, cancellationToken);
            WriteConversionManifest(
                probe,
                stagingDirectory,
                outputTensorPath,
                outputSha256,
                cancellationToken);
            ValidateOutput(stagingDirectory);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagingDirectory, outputDirectory);

            stopwatch.Stop();
            var finalTensorPath = Path.Combine(outputDirectory, OutputTensorFile);
            return new ModelConversionResult(
                ManagedOlmoeProvider.ProviderId,
                sourceDirectory,
                outputDirectory,
                "int8",
                "rowwise-qs",
                probe.Tensors.Count,
                outputTensors.Count,
                probe.Tensors.TotalPayloadBytes,
                new FileInfo(finalTensorPath).Length,
                outputSha256,
                stopwatch.Elapsed);
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
    }

    private static IReadOnlyList<OutputTensor> BuildOutputTensors(SafeTensorCatalog tensors)
    {
        var output = new List<OutputTensor>();
        foreach (var descriptor in tensors.Items.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            if (!IsExpertWeight(descriptor.Name))
            {
                output.Add(new OutputTensor(
                    descriptor.Name,
                    descriptor.DataTypeName,
                    descriptor.LogicalShape,
                    descriptor.PhysicalLength,
                    descriptor,
                    OutputTensorKind.Copy));
                continue;
            }

            if (descriptor.LogicalShape.Count != 2 ||
                descriptor.DataType is not (
                    TensorDataType.Float32 or TensorDataType.Float16 or TensorDataType.BFloat16))
            {
                throw new InvalidDataException(
                    $"Expert tensor '{descriptor.Name}' must be a rank-2 F32, F16 or BF16 matrix before int8 conversion.");
            }

            var rows = descriptor.LogicalShape[0];
            output.Add(new OutputTensor(
                descriptor.Name,
                "I8",
                descriptor.LogicalShape,
                descriptor.ElementCount,
                descriptor,
                OutputTensorKind.QuantizedWeight));
            output.Add(new OutputTensor(
                ExpertDescriptorLayout.GetScaleTensorName(descriptor.Name, "rowwise-qs"),
                "F32",
                [rows],
                checked(rows * sizeof(float)),
                descriptor,
                OutputTensorKind.RowScales));
        }

        return output;
    }

    private static void WriteTensorFile(
        SafeTensorCatalog sourceCatalog,
        IReadOnlyList<OutputTensor> outputTensors,
        string outputPath,
        long estimatedPayloadBytes,
        Action<ModelConversionProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        var header = CreateHeader(outputTensors);
        using var source = new TensorDataSource(sourceCatalog, new TensorDataSourceOptions(TensorIoMode.RandomAccess));
        using var stream = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferBytes,
            FileOptions.SequentialScan);
        Span<byte> headerLength = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(headerLength, checked((ulong)header.Length));
        stream.Write(headerLength);
        stream.Write(header);

        long writtenPayloadBytes = 0;
        for (var index = 0; index < outputTensors.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tensor = outputTensors[index];
            onProgress?.Invoke(new ModelConversionProgress(
                tensor.Kind == OutputTensorKind.Copy ? "copy" : "quantize",
                tensor.Name,
                index,
                outputTensors.Count,
                writtenPayloadBytes,
                estimatedPayloadBytes));

            if (tensor.Kind == OutputTensorKind.Copy)
            {
                CopyTensor(source, tensor.Source, stream, cancellationToken);
                writtenPayloadBytes = checked(writtenPayloadBytes + tensor.Length);
                continue;
            }

            if (tensor.Kind != OutputTensorKind.QuantizedWeight ||
                index + 1 >= outputTensors.Count ||
                outputTensors[index + 1].Kind != OutputTensorKind.RowScales ||
                !ReferenceEquals(outputTensors[index + 1].Source, tensor.Source))
            {
                throw new InvalidDataException($"Quantized tensor layout is incomplete for '{tensor.Name}'.");
            }

            QuantizeTensor(source, tensor.Source, stream, cancellationToken);
            writtenPayloadBytes = checked(
                writtenPayloadBytes + tensor.Length + outputTensors[index + 1].Length);
            index++;
        }

        stream.Flush(flushToDisk: true);
        onProgress?.Invoke(new ModelConversionProgress(
            "write-complete",
            null,
            outputTensors.Count,
            outputTensors.Count,
            writtenPayloadBytes,
            estimatedPayloadBytes));
    }

    private static byte[] CreateHeader(IReadOnlyList<OutputTensor> tensors)
    {
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("__metadata__");
            writer.WriteStartObject();
            writer.WriteString("format", "pt");
            writer.WriteString("tomur_quantization", "rowwise-qs");
            writer.WriteEndObject();
            long offset = 0;
            foreach (var tensor in tensors)
            {
                writer.WritePropertyName(tensor.Name);
                writer.WriteStartObject();
                writer.WriteString("dtype", tensor.Dtype);
                writer.WriteStartArray("shape");
                foreach (var dimension in tensor.Shape)
                {
                    writer.WriteNumberValue(dimension);
                }

                writer.WriteEndArray();
                writer.WriteStartArray("data_offsets");
                writer.WriteNumberValue(offset);
                offset = checked(offset + tensor.Length);
                writer.WriteNumberValue(offset);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return memory.ToArray();
    }

    private static void QuantizeTensor(
        TensorDataSource source,
        TensorDescriptor descriptor,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var rows = checked((int)descriptor.LogicalShape[0]);
        var columns = checked((int)descriptor.LogicalShape[1]);
        var elementSize = descriptor.DataType.GetElementSize();
        var rowByteCount = checked(columns * elementSize);
        var rowsPerChunk = Math.Max(1, CopyBufferBytes / rowByteCount);
        var sourceBytes = ArrayPool<byte>.Shared.Rent(checked(rowsPerChunk * rowByteCount));
        var values = ArrayPool<float>.Shared.Rent(columns);
        var quantized = ArrayPool<byte>.Shared.Rent(columns);
        var scales = ArrayPool<float>.Shared.Rent(rows);
        try
        {
            for (var firstRow = 0; firstRow < rows; firstRow += rowsPerChunk)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunkRows = Math.Min(rowsPerChunk, rows - firstRow);
                var chunkBytes = sourceBytes.AsSpan(0, checked(chunkRows * rowByteCount));
                source.ReadExactly(
                    descriptor,
                    checked((long)firstRow * rowByteCount),
                    chunkBytes,
                    cancellationToken);
                for (var rowOffset = 0; rowOffset < chunkRows; rowOffset++)
                {
                    var row = firstRow + rowOffset;
                    var rowBytes = chunkBytes.Slice(rowOffset * rowByteCount, rowByteCount);
                    var rowValues = values.AsSpan(0, columns);
                    DecodeRow(descriptor, rowBytes, rowValues);
                    var scale = GetRowScale(descriptor.Name, row, rowValues);
                    scales[row] = scale;
                    var rowQuantized = quantized.AsSpan(0, columns);
                    for (var column = 0; column < columns; column++)
                    {
                        var value = Math.Clamp(
                            (int)MathF.Round(rowValues[column] / scale, MidpointRounding.ToEven),
                            -127,
                            127);
                        rowQuantized[column] = unchecked((byte)(sbyte)value);
                    }

                    destination.Write(rowQuantized);
                }
            }

            var scaleBytes = ArrayPool<byte>.Shared.Rent(checked(rows * sizeof(float)));
            try
            {
                var output = scaleBytes.AsSpan(0, checked(rows * sizeof(float)));
                for (var row = 0; row < rows; row++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(
                        output.Slice(row * sizeof(float), sizeof(float)),
                        scales[row]);
                }

                destination.Write(output);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scaleBytes, clearArray: true);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sourceBytes, clearArray: true);
            ArrayPool<float>.Shared.Return(values, clearArray: true);
            ArrayPool<byte>.Shared.Return(quantized, clearArray: true);
            ArrayPool<float>.Shared.Return(scales, clearArray: true);
        }
    }

    private static void DecodeRow(
        TensorDescriptor descriptor,
        ReadOnlySpan<byte> source,
        Span<float> destination)
    {
        var elementSize = descriptor.DataType.GetElementSize();
        for (var index = 0; index < destination.Length; index++)
        {
            var bytes = source.Slice(index * elementSize, elementSize);
            destination[index] = descriptor.DataType switch
            {
                TensorDataType.Float32 => BinaryPrimitives.ReadSingleLittleEndian(bytes),
                TensorDataType.Float16 => (float)BitConverter.UInt16BitsToHalf(
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes)),
                TensorDataType.BFloat16 => BitConverter.UInt32BitsToSingle(
                    (uint)BinaryPrimitives.ReadUInt16LittleEndian(bytes) << 16),
                _ => throw new InvalidDataException(
                    $"Expert tensor '{descriptor.Name}' has unsupported dtype {descriptor.DataTypeName}.")
            };
        }
    }

    private static float GetRowScale(string tensorName, int row, ReadOnlySpan<float> values)
    {
        var maximum = 0.0f;
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            if (!float.IsFinite(value))
            {
                throw new InvalidDataException(
                    $"Expert tensor '{tensorName}' row {row} contains a non-finite value at column {index}.");
            }

            maximum = Math.Max(maximum, Math.Abs(value));
        }

        return maximum == 0 ? 1.0f : maximum / 127.0f;
    }

    private static void CopyTensor(
        TensorDataSource source,
        TensorDescriptor descriptor,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            long offset = 0;
            while (offset < descriptor.PhysicalLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = checked((int)Math.Min(buffer.Length, descriptor.PhysicalLength - offset));
                var chunk = buffer.AsSpan(0, count);
                source.ReadExactly(descriptor, offset, chunk, cancellationToken);
                destination.Write(chunk);
                offset += count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void CopyAuxiliaryFiles(
        OlmoeModelProbe probe,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        var tensorPaths = probe.Tensors.Items
            .Select(static item => item.ShardPath)
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(probe.ModelDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tensorPaths.Contains(Path.GetFullPath(path)) ||
                Path.GetFileName(path).Equals(ModelProviderManifest.FileName, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).Equals(ConversionManifestFile, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).EndsWith(".safetensors.index.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException($"Conversion input must not contain linked assets: {path}");
            }

            File.Copy(path, Path.Combine(stagingDirectory, Path.GetFileName(path)), overwrite: false);
        }
    }

    private static void WriteProviderManifest(ModelProviderManifest source, string stagingDirectory)
    {
        using var stream = File.Create(Path.Combine(stagingDirectory, ModelProviderManifest.FileName));
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("schema_version", 1);
        writer.WriteString("provider", ManagedOlmoeProvider.ProviderId);
        writer.WriteString("architecture", "olmoe");
        writer.WriteString("display_name", source.DisplayName);
        writer.WriteString("config", source.ConfigFile);
        writer.WriteString("tokenizer", source.TokenizerFile);
        writer.WriteString("tensor_pattern", OutputTensorFile);
        writer.WriteString("quantization", "int8");
        writer.WriteString("quantization_layout", "rowwise-qs");
        writer.WriteStartArray("capabilities");
        foreach (var capability in source.Capabilities)
        {
            writer.WriteStringValue(capability);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteConversionManifest(
        OlmoeModelProbe probe,
        string stagingDirectory,
        string outputTensorPath,
        string outputSha256,
        CancellationToken cancellationToken)
    {
        using var stream = File.Create(Path.Combine(stagingDirectory, ConversionManifestFile));
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("schema_version", 1);
        writer.WriteString("provider", ManagedOlmoeProvider.ProviderId);
        writer.WriteString("source_quantization", probe.Manifest.Quantization);
        writer.WriteString("output_quantization", "int8");
        writer.WriteString("output_layout", "rowwise-qs");
        writer.WriteString("created_at_utc", DateTimeOffset.UtcNow);
        writer.WriteStartArray("source_files");
        foreach (var path in probe.Tensors.Items
                     .Select(static item => item.ShardPath)
                     .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                     .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            writer.WriteStartObject();
            writer.WriteString("name", info.Name);
            writer.WriteNumber("bytes", info.Length);
            writer.WriteString("sha256", ComputeSha256(path, cancellationToken));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartObject("output_file");
        writer.WriteString("name", Path.GetFileName(outputTensorPath));
        writer.WriteNumber("bytes", new FileInfo(outputTensorPath).Length);
        writer.WriteString("sha256", outputSha256);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void ValidateOutput(string stagingDirectory)
    {
        var manifestPath = Path.Combine(stagingDirectory, ModelProviderManifest.FileName);
        var info = new FileInfo(manifestPath);
        var descriptor = new LocalModelDescriptor(
            "conversion-output",
            "Converted OLMoE model",
            ModelProviderManifest.FileName,
            ModelProviderManifest.FileName,
            manifestPath,
            info.Length,
            info.LastWriteTimeUtc,
            "managed-model",
            "olmoe",
            "int8",
            ["completion", "chat"]);
        var probe = OlmoeModelDirectoryProbe.Read(descriptor, ManagedOlmoeProvider.ProviderId);
        if (!probe.Manifest.Quantization.Equals("int8", StringComparison.OrdinalIgnoreCase) ||
            !probe.Manifest.QuantizationLayout.Equals("rowwise-qs", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Converted OLMoE output did not pass rowwise int8 validation.");
        }
    }

    private static string ComputeSha256(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferBytes,
            FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
            }

            return System.Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void ValidateDirectories(string sourceDirectory, string outputDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"OLMoE conversion source was not found: {sourceDirectory}");
        }

        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new IOException($"OLMoE conversion output already exists: {outputDirectory}");
        }

        if (IsWithin(sourceDirectory, outputDirectory) || IsWithin(outputDirectory, sourceDirectory))
        {
            throw new InvalidDataException("OLMoE conversion source and output directories must not contain each other.");
        }
    }

    private static void EnsureDiskCapacity(string outputDirectory, long estimatedPayloadBytes)
    {
        var root = Path.GetPathRoot(outputDirectory);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var drive = new DriveInfo(root);
        var required = checked(estimatedPayloadBytes + (64L * 1024 * 1024));
        if (drive.IsReady && drive.AvailableFreeSpace < required)
        {
            throw new IOException(
                $"OLMoE conversion requires at least {required} free bytes, but {drive.AvailableFreeSpace} are available on {root}.");
        }
    }

    private static bool IsWithin(string candidateParent, string candidateChild)
    {
        var relative = Path.GetRelativePath(candidateParent, candidateChild);
        return relative == "." ||
            (!Path.IsPathRooted(relative) &&
             relative != ".." &&
             !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static bool IsFloating(string quantization)
        => quantization.Equals("f32", StringComparison.OrdinalIgnoreCase) ||
           quantization.Equals("f16", StringComparison.OrdinalIgnoreCase) ||
           quantization.Equals("bf16", StringComparison.OrdinalIgnoreCase);

    private static bool IsExpertWeight(string name)
        => name.Contains(".mlp.experts.", StringComparison.Ordinal) &&
           (name.EndsWith(".gate_proj.weight", StringComparison.Ordinal) ||
            name.EndsWith(".up_proj.weight", StringComparison.Ordinal) ||
            name.EndsWith(".down_proj.weight", StringComparison.Ordinal));

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

    private enum OutputTensorKind
    {
        Copy,
        QuantizedWeight,
        RowScales
    }

    private sealed record OutputTensor(
        string Name,
        string Dtype,
        IReadOnlyList<long> Shape,
        long Length,
        TensorDescriptor Source,
        OutputTensorKind Kind);
}
