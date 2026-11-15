namespace Yaparr.Cloud;

internal static class Responses
{
    /// <summary>A page for a browser: never kept, never framed by another site, and with nothing it could load from anywhere.</summary>
    public static IResult Html(HttpContext http, string page, int status = StatusCodes.Status200OK)
    {
        var headers = http.Response.Headers;
        headers.CacheControl = "no-store";
        headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'";
        headers.XFrameOptions = "DENY";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        return Results.Content(page, "text/html; charset=utf-8", statusCode: status);
    }
}
