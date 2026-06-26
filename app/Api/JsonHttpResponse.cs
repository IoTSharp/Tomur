using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;

namespace Tomur.Api;

public static class JsonHttpResponse
{
    public static async Task WriteAsync<T>(
        HttpContext context,
        T value,
        JsonTypeInfo<T> jsonTypeInfo,
        int statusCode = StatusCodes.Status200OK)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            value,
            jsonTypeInfo,
            context.RequestAborted);
    }
}
