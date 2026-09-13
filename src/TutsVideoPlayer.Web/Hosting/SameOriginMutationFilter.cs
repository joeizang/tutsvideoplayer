using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using TutsVideoPlayer.Web.Features.Library;

namespace TutsVideoPlayer.Web.Hosting;

/// <summary>
/// Browser and local-network protection for mutating requests. There is no login, so the
/// only defence against a cross-site page driving filesystem scanning is to require the
/// shape of request a browser will not let a foreign origin produce: JSON content, a custom
/// same-origin header, a recognized Origin when one is sent, and non-cross-site Fetch
/// Metadata. This is not authentication; any permitted LAN client can still call the API.
/// </summary>
public sealed class SameOriginMutationFilter(
    IOptions<AppOptions> options,
    IConfiguration configuration,
    ILogger<SameOriginMutationFilter> logger) : IAsyncActionFilter
{
    /// <summary>
    /// A cross-origin <c>fetch</c> cannot set this header without a CORS preflight, and no
    /// CORS policy is configured, so the preflight can never be approved.
    /// </summary>
    public const string RequestHeaderName = "X-TutsVideoPlayer-Request";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;

        if (IsSafeMethod(request.Method))
        {
            await next();
            return;
        }

        if (!string.IsNullOrEmpty(request.ContentType) && !IsJsonContentType(request.ContentType))
        {
            // Simple form content types are exactly what a cross-site <form> can submit.
            Reject(context, StatusCodes.Status415UnsupportedMediaType, "Unsupported content type",
                "Mutating requests must use a JSON content type.");
            return;
        }

        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite is "cross-site" or "same-site")
        {
            Reject(context, StatusCodes.Status403Forbidden, "Cross-site request rejected",
                "This request was reported as cross-site by the browser.");
            return;
        }

        var origin = request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) && !IsRecognizedOrigin(origin))
        {
            Reject(context, StatusCodes.Status403Forbidden, "Unrecognized origin",
                "The request origin is not one of the configured origins.");
            return;
        }

        if (!request.Headers.ContainsKey(RequestHeaderName))
        {
            Reject(context, StatusCodes.Status403Forbidden, "Missing same-origin request header",
                $"Mutating requests must include the {RequestHeaderName} header.");
            return;
        }

        await next();
    }

    private static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method)
        || HttpMethods.IsHead(method)
        || HttpMethods.IsOptions(method)
        || HttpMethods.IsTrace(method);

    private static bool IsJsonContentType(string contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed))
        {
            return false;
        }

        var mediaType = parsed.MediaType.Value;
        return mediaType is not null
            && (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
    }

    private bool IsRecognizedOrigin(string origin)
    {
        var configured = options.Value.AllowedOrigins;
        if (configured.Length > 0)
        {
            return configured.Any(candidate =>
                string.Equals(candidate.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        }

        // Without explicit origins, fall back to the configured host allow-list rather than
        // the arbitrary Host value of this request. Host filtering has already rejected
        // unrecognized hosts, so this only accepts hosts the operator chose to serve.
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        var allowedHosts = configuration["AllowedHosts"];
        if (string.IsNullOrWhiteSpace(allowedHosts) || allowedHosts.Contains('*', StringComparison.Ordinal))
        {
            logger.LogWarning(
                "No App:AllowedOrigins are configured and AllowedHosts is unrestricted; the origin {Origin} was rejected.",
                origin);
            return false;
        }

        return allowedHosts
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(host => string.Equals(host.Trim('[', ']'), parsed.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase));
    }

    private void Reject(ActionExecutingContext context, int statusCode, string title, string detail)
    {
        logger.LogWarning(
            "Rejected {Method} {Path}: {Title}.",
            context.HttpContext.Request.Method, context.HttpContext.Request.Path, title);

        context.Result = new ObjectResult(new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail
        })
        {
            StatusCode = statusCode,
            ContentTypes = { "application/problem+json" }
        };
    }
}
