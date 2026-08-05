using System.Net;
using System.Text.Json;

namespace Teamscop.Engine.Auth;

/// <summary>HTTP API failure with parsed `{ "error": "…" }` body when present.</summary>
public sealed class ApiClientException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? ErrorCode { get; }
    public string RawBody { get; }

    public ApiClientException(HttpStatusCode statusCode, string message, string rawBody, string? errorCode = null)
        : base(message)
    {
        StatusCode = statusCode;
        RawBody = rawBody;
        ErrorCode = errorCode;
    }

    public static async Task ThrowIfUnsuccessfulAsync(
        HttpResponseMessage response,
        string apiName,
        CancellationToken cancellationToken = default)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var (message, code) = ParseError(body);
        var status = (int)response.StatusCode;
        throw new ApiClientException(
            response.StatusCode,
            string.IsNullOrWhiteSpace(message)
                ? $"{apiName} error {status}"
                : $"{apiName}: {message}",
            body,
            code);
    }

    public static (string? Message, string? Code) ParseError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? message = null;
            string? code = null;
            if (root.TryGetProperty("error", out var err))
            {
                message = err.ValueKind == JsonValueKind.String ? err.GetString() : err.ToString();
            }

            if (root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
            {
                code = c.GetString();
            }

            return (message, code);
        }
        catch (JsonException)
        {
            return (body.Length > 200 ? body[..200] : body, null);
        }
    }
}
