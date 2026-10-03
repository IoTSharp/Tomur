using System.Text.Json;
using Tomur.Providers;
using Tomur.Serialization;

namespace Tomur.Decisions;

/// <summary>Strict, source-generated JSON boundary for typed decision requests.</summary>
public static class DecisionRequestReader
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = DecisionRequestLimits.Default.MaxJsonDepth,
    };

    public static bool TryRead(
        ReadOnlyMemory<byte> utf8Json,
        out DecisionRequest? request,
        out DecisionError? error,
        DecisionRequestLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        request = null;
        error = null;
        cancellationToken.ThrowIfCancellationRequested();

        var effectiveLimits = limits ?? DecisionRequestLimits.Default;
        try
        {
            effectiveLimits.Validate();
        }
        catch (ArgumentOutOfRangeException exception)
        {
            error = InvalidRequest(exception.Message);
            return false;
        }

        if (utf8Json.Length > effectiveLimits.MaxRequestBytes)
        {
            error = new DecisionError
            {
                Code = DecisionErrorCodes.InputLimitExceeded,
                Message = $"Decision request exceeds the {effectiveLimits.MaxRequestBytes} byte limit.",
                Retryable = false,
            };
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions
            {
                AllowTrailingCommas = DocumentOptions.AllowTrailingCommas,
                CommentHandling = DocumentOptions.CommentHandling,
                MaxDepth = effectiveLimits.MaxJsonDepth,
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = InvalidRequest("The decision request must be a JSON object.");
                return false;
            }

            if (HasDuplicateProperties(document.RootElement, cancellationToken))
            {
                error = InvalidRequest("Duplicate JSON property names are not accepted.");
                return false;
            }

            request = JsonSerializer.Deserialize(
                utf8Json.Span,
                AppJsonSerializerContext.Default.DecisionRequest);
            if (request is null)
            {
                error = InvalidRequest("The decision request must not be null.");
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            error = new DecisionError
            {
                Code = DecisionErrorCodes.InvalidJson,
                Message = exception.Message,
                Retryable = false,
            };
            return false;
        }
        catch (NotSupportedException exception)
        {
            error = InvalidRequest(exception.Message);
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        error = DecisionRequestValidator.Validate(request, effectiveLimits, cancellationToken);
        if (error is not null)
        {
            request = null;
            return false;
        }

        return true;
    }

    private static bool HasDuplicateProperties(JsonElement element, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value, cancellationToken))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicateProperties(item, cancellationToken))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static DecisionError InvalidRequest(string message)
        => new()
        {
            Code = DecisionErrorCodes.InvalidRequest,
            Message = message,
            Retryable = false,
        };
}
