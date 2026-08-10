using System.Net;
using System.Text.Json;
using Teamscop.Engine.Auth;

namespace Teamscop.App.Composition;

/// <summary>
/// Thrown by <see cref="TeamscopApi"/> when a call needs a session and none is stored.
/// Callers do not test for a token; they handle this like any other failure.
/// </summary>
public sealed class NotSignedInException : Exception
{
    public NotSignedInException()
        : base("Sign in required.")
    {
    }
}

/// <summary>
/// The single exception-to-user-text mapping. Replaces the six near-identical private
/// FormatError helpers plus AuthViewModel's CleanApiMessage / ExtractApiError / Unwrap.
/// </summary>
public static class ApiError
{
    private static readonly string[] ApiPrefixes =
    [
        "Auth API: ",
        "Org API: ",
        "Police API: ",
        "Tracking API: ",
        "Lifecycle API: ",
        "BusinessTime API: ",
        "Ingest API: "
    ];

    /// <param name="fallback">Shown when the exception carries no usable message.</param>
    public static string Describe(Exception exception, string fallback = "Something went wrong.")
    {
        var ex = Unwrap(exception);

        switch (ex)
        {
            case NotSignedInException:
                return "Sign in required.";

            case ApiClientException { StatusCode: HttpStatusCode.Unauthorized }:
                return "Session no longer valid — sign in again.";

            case ApiClientException { StatusCode: HttpStatusCode.Forbidden } forbidden:
                return Or(Clean(forbidden.Message), "Not allowed.");

            case ApiClientException api:
                return Or(Clean(api.Message), fallback);

            case HttpRequestException http when http.Message.StartsWith("Auth API", StringComparison.Ordinal):
                return Or(ExtractApiError(http.Message), fallback);

            case HttpRequestException:
                return "Could not reach the server.";

            case TaskCanceledException or OperationCanceledException:
                return "Request timed out.";

            case JsonException:
                return "Invalid server response.";

            case IOException:
                return "File or storage error.";

            case UnauthorizedAccessException:
                return Or(ex.Message, "Not allowed.");

            default:
                return Or(ExtractApiError(ex.Message), fallback);
        }
    }

    /// <summary>True for the expected 403 a partial package set produces — hide, do not alarm.</summary>
    public static bool IsForbidden(Exception ex)
        => Unwrap(ex) is ApiClientException { StatusCode: HttpStatusCode.Forbidden };

    /// <summary>The stored token is dead — the shell returns to sign-in rather than retrying.</summary>
    public static bool IsUnauthorized(Exception ex)
        => Unwrap(ex) is ApiClientException { StatusCode: HttpStatusCode.Unauthorized } or NotSignedInException;

    private static string Or(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string Clean(string message)
    {
        foreach (var prefix in ApiPrefixes)
        {
            if (message.StartsWith(prefix, StringComparison.Ordinal))
            {
                return message[prefix.Length..];
            }
        }

        return ExtractApiError(message);
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is AggregateException { InnerException: { } inner })
        {
            ex = inner;
        }

        // Only look inside an exception this mapping does not already recognise. An
        // HttpRequestException carrying an inner IOException is a network failure, not a disk one,
        // and promoting the inner told the user "File or storage error." for every dropped socket.
        if (ex is NotSignedInException
            or ApiClientException
            or HttpRequestException
            or TaskCanceledException
            or OperationCanceledException
            or JsonException
            or IOException
            or UnauthorizedAccessException)
        {
            return ex;
        }

        return ex.InnerException is ApiClientException or HttpRequestException or TaskCanceledException or IOException
            ? ex.InnerException
            : ex;
    }

    /// <summary>Pulls the server's `{ "error": "…" }` out of an "Auth API error 400: {…}" message.</summary>
    private static string ExtractApiError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        const string marker = "Auth API error ";
        if (!message.StartsWith(marker, StringComparison.Ordinal))
        {
            return message;
        }

        var bodyStart = message.IndexOf(':');
        if (bodyStart < 0 || bodyStart + 1 >= message.Length)
        {
            return message;
        }

        var body = message[(bodyStart + 1)..].Trim();
        var (parsed, _) = ApiClientException.ParseError(body);
        return string.IsNullOrWhiteSpace(parsed) ? body : parsed;
    }
}
