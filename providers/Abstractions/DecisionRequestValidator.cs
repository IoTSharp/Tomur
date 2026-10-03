using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Tomur.Providers;

/// <summary>
/// Validates the typed decision request before a provider allocates model or
/// tokenizer resources. This validates an already materialized request; the
/// HTTP body and tokenizer budgets remain host/provider responsibilities.
/// </summary>
public static class DecisionRequestValidator
{
    public static DecisionError? Validate(
        DecisionRequest? request,
        DecisionRequestLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var effectiveLimits = limits ?? DecisionRequestLimits.Default;
        try
        {
            effectiveLimits.Validate();
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return Invalid(exception.Message);
        }

        if (request is null)
        {
            return Invalid("The decision request is required.");
        }

        var context = new ValidationContext(effectiveLimits, cancellationToken);
        return context.Validate(request);
    }

    private static DecisionError Invalid(string message) => new()
    {
        Code = DecisionErrorCodes.InvalidRequest,
        Message = message,
        Retryable = false,
    };

    private static DecisionError Limited(string message, string? code = null) => new()
    {
        Code = code ?? DecisionErrorCodes.InputLimitExceeded,
        Message = message,
        Retryable = false,
    };

    private sealed class ValidationContext
    {
        private readonly DecisionRequestLimits _limits;
        private readonly CancellationToken _cancellationToken;
        private long _visitedNodes;
        private int _totalJsonBytes;

        public ValidationContext(DecisionRequestLimits limits, CancellationToken cancellationToken)
        {
            _limits = limits;
            _cancellationToken = cancellationToken;
        }

        public DecisionError? Validate(DecisionRequest request)
        {
            var modelError = ValidateIdentifier(request.Model, "model");
            if (modelError is not null)
            {
                return modelError;
            }

            if (request.State.ValueKind == JsonValueKind.Undefined)
            {
                return Invalid("state must contain a defined JSON value.");
            }

            if (request.Questions is null)
            {
                return Invalid("questions is required.");
            }

            if (request.Questions.Count == 0 || request.Questions.Count > _limits.MaxQuestions)
            {
                return Limited(
                    $"questions must contain between one and {_limits.MaxQuestions} entries.",
                    request.Questions.Count > _limits.MaxQuestions
                        ? DecisionErrorCodes.QuestionLimitExceeded
                        : null);
            }

            var stateError = ValidateJson(request.State, "state", allowNull: true, fieldLimit: _limits.MaxStateBytes);
            if (stateError is not null)
            {
                return stateError;
            }

            var index = 0;
            foreach (var pair in request.Questions)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (++index % 16 == 0)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                }

                var questionIdError = ValidateIdentifier(pair.Key, "question identifier");
                if (questionIdError is not null)
                {
                    return questionIdError;
                }

                if (pair.Value is null)
                {
                    return Invalid($"question '{pair.Key}' must not be null.");
                }

                var questionError = ValidateQuestion(pair.Key, pair.Value);
                if (questionError is not null)
                {
                    return questionError;
                }
            }

            return null;
        }

        private DecisionError? ValidateQuestion(string id, DecisionQuestion question)
        {
            var instructionsError = ValidateJson(
                question.Instructions,
                $"instructions for '{id}'",
                allowNull: false,
                fieldLimit: _limits.MaxInstructionBytes);
            if (instructionsError is not null)
            {
                return instructionsError;
            }

            switch (question)
            {
                case DecisionChoiceQuestion choice:
                    if (choice.Criteria is null || choice.Criteria.Count < 2)
                    {
                        return Invalid($"choice question '{id}' must contain at least two criteria.");
                    }

                    if (choice.Criteria.Count > _limits.MaxCandidates)
                    {
                        return Limited($"choice question '{id}' exceeds MaxCandidates.");
                    }

                    foreach (var criterion in choice.Criteria)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                        var criterionIdError = ValidateIdentifier(criterion.Key, "choice criterion identifier");
                        if (criterionIdError is not null)
                        {
                            return criterionIdError;
                        }

                        var criterionError = ValidateJson(
                            criterion.Value,
                            $"criterion '{criterion.Key}' for '{id}'",
                            allowNull: false);
                        if (criterionError is not null)
                        {
                            return criterionError;
                        }
                    }

                    return null;

                case DecisionScoreQuestion score:
                    if (score.Criteria is null || score.Criteria.Count < 2)
                    {
                        return Invalid($"score question '{id}' must contain at least two criteria.");
                    }

                    if (score.Criteria.Count > _limits.MaxScoreCriteria)
                    {
                        return Limited($"score question '{id}' exceeds MaxScoreCriteria.");
                    }

                    for (var i = 0; i < score.Criteria.Count; i++)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                        var criterionError = ValidateJson(
                            score.Criteria[i],
                            $"score criterion {i} for '{id}'",
                            allowNull: false);
                        if (criterionError is not null)
                        {
                            return criterionError;
                        }
                    }

                    return null;

                case DecisionBooleanQuestion boolean:
                    if (boolean.Criteria is null)
                    {
                        return null;
                    }

                    var trueError = ValidateJson(boolean.Criteria.WhenTrue, $"true criterion for '{id}'", allowNull: false);
                    return trueError ?? ValidateJson(boolean.Criteria.WhenFalse, $"false criterion for '{id}'", allowNull: false);

                default:
                    return Invalid($"question '{id}' has an unsupported question type.");
            }
        }

        private DecisionError? ValidateJson(JsonElement element, string field, bool allowNull, int? fieldLimit = null)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (element.ValueKind == JsonValueKind.Undefined || (!allowNull && element.ValueKind == JsonValueKind.Null))
            {
                return Invalid($"{field} must contain a defined, non-null JSON value.");
            }

            try
            {
                Visit(element, depth: 1);
                CountUtf8(element, fieldLimit);
                return null;
            }
            catch (InputLimitException exception)
            {
                return Limited($"{field} exceeds the configured JSON or UTF-8 input limits: {exception.Message}");
            }
            catch (JsonException exception)
            {
                return Invalid($"{field} is not a valid JSON value: {exception.Message}");
            }
        }

        private void Visit(JsonElement element, int depth)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (++_visitedNodes > _limits.MaxRequestBytes)
            {
                throw new InputLimitException("JSON node visit limit exceeded.");
            }

            if (depth > _limits.MaxJsonDepth)
            {
                throw new InputLimitException("JSON depth limit exceeded.");
            }

            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var child in element.EnumerateArray())
                    {
                        Visit(child, depth + 1);
                    }

                    break;
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (Encoding.UTF8.GetByteCount(property.Name) > _limits.MaxIdentifierLength)
                        {
                            throw new InputLimitException("JSON property name exceeds MaxIdentifierLength.");
                        }

                        Visit(property.Value, depth + 1);
                    }

                    break;
            }
        }

        private void CountUtf8(JsonElement element, int? fieldLimit)
        {
            var remaining = _limits.MaxRequestBytes - _totalJsonBytes;
            var limit = fieldLimit.HasValue ? Math.Min(remaining, fieldLimit.Value) : remaining;
            var sink = new CountingBufferWriter(limit);
            var writer = new Utf8JsonWriter(sink, new JsonWriterOptions
            {
                MaxDepth = _limits.MaxJsonDepth,
                Indented = false,
            });

            try
            {
                element.WriteTo(writer);
                writer.Flush();
                _totalJsonBytes += sink.Count;
                if (fieldLimit.HasValue && sink.Count > fieldLimit.Value)
                {
                    throw new InputLimitException("per-field UTF-8 limit exceeded.");
                }

                if (_totalJsonBytes > _limits.MaxRequestBytes)
                {
                    throw new InputLimitException("aggregate JSON UTF-8 limit exceeded.");
                }
            }
            finally
            {
                writer.Dispose();
                sink.Dispose();
            }
        }

        private DecisionError? ValidateIdentifier(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Invalid($"{field} must be non-empty.");
            }

            if (Encoding.UTF8.GetByteCount(value) > _limits.MaxIdentifierLength)
            {
                return Limited($"{field} exceeds MaxIdentifierLength.");
            }

            return null;
        }
    }

    private sealed class CountingBufferWriter : IBufferWriter<byte>
    {
        private readonly int _limit;
        private byte[]? _buffer;
        private int _count;

        public CountingBufferWriter(int limit) => _limit = Math.Max(0, limit);

        public int Count => _count;

        public void Advance(int count)
        {
            if (_buffer is null || count < 0 || count > _buffer.Length)
            {
                throw new InvalidOperationException("Invalid UTF-8 writer advance.");
            }

            _count = checked(_count + count);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
            if (_count > _limit)
            {
                throw new InputLimitException("aggregate JSON UTF-8 limit exceeded.");
            }
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            var requested = Math.Max(1, sizeHint);
            if (requested > _limit - _count)
            {
                throw new InputLimitException("aggregate JSON UTF-8 limit exceeded.");
            }

            _buffer = ArrayPool<byte>.Shared.Rent(requested);
            return _buffer.AsMemory(0, requested);
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        public void Dispose()
        {
            if (_buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = null;
            }
        }
    }

    private sealed class InputLimitException(string message) : Exception(message);
}
