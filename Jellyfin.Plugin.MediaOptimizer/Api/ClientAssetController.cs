using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Api;

/// <summary>
/// Serves the injected client bundle, and the transformation callback the File Transformation
/// plugin posts index.html to.
/// </summary>
[ApiController]
[Route("MediaOptimizer")]
public class ClientAssetController : ControllerBase
{
    private readonly ILogger<ClientAssetController> _logger;

    /// <summary>Initializes a new instance of the <see cref="ClientAssetController"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public ClientAssetController(ILogger<ClientAssetController> logger)
    {
        _logger = logger;
    }

    /// <summary>Serves the client script. Anonymous because a script tag carries no auth header.</summary>
    /// <returns>The JavaScript bundle.</returns>
    [HttpGet("client.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetClientScript()
    {
        var js = ReadResource("Web.Resources.client.js");
        if (js is null)
        {
            return NotFound();
        }

        var css = ReadResource("Web.Resources.client.css") ?? string.Empty;

        // The stylesheet ships inside the script so the page makes one request, and so the
        // styles cannot arrive after the dialog has already been opened.
        var bundle = js.Replace(
            "/*__MEDIAOPTIMIZER_CSS__*/",
            System.Text.Json.JsonSerializer.Serialize(css),
            StringComparison.Ordinal);

        return Content(bundle, "application/javascript", Encoding.UTF8);
    }

    /// <summary>
    /// Receives index.html from the File Transformation plugin and returns it with the script tag added.
    /// </summary>
    /// <returns>The transformed document.</returns>
    [HttpPost("Transform")]
    [AllowAnonymous]
    public async Task<ActionResult> Transform()
    {
        try
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);

            var html = ExtractContents(body);
            if (html is null)
            {
                _logger.LogWarning("[MediaOptimizer] Transformation payload did not contain document contents");
                return BadRequest();
            }

            return Content(WebInjectionHostedService.InjectInto(html), "text/html", Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            _logger.LogError(ex, "[MediaOptimizer] Failed to transform index.html");
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Pulls the document out of the transformation payload. Older builds post the raw file,
    /// newer ones wrap it in a JSON object, so both shapes are accepted.
    /// </summary>
    /// <param name="body">The raw request body.</param>
    /// <returns>The document, or null when it could not be found.</returns>
    internal static string? ExtractContents(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var trimmed = body.TrimStart();
        if (!trimmed.StartsWith('{'))
        {
            return body;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            foreach (var name in new[] { "contents", "Contents" })
            {
                if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON after all; treat the body as the document.
            return body;
        }

        return null;
    }

    private static string? ReadResource(string relativeName)
    {
        var assembly = typeof(ClientAssetController).Assembly;
        var fullName = typeof(Plugin).Namespace + "." + relativeName;

        using var stream = assembly.GetManifestResourceStream(fullName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
