using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.MediaOptimizer.Api;

/// <summary>
/// Serves the injected client bundle.
/// <para>
/// There used to be a companion endpoint here that accepted index.html by HTTP and returned it
/// with the script tag added, for older File Transformation versions that worked that way. It had
/// to be anonymous, because the caller carried no credentials — which made it an unauthenticated
/// endpoint that echoed whatever was posted to it back as text/html on the Jellyfin origin, and
/// anything reachable from a browser that reflects HTML is a cross-site scripting hole whatever it
/// was meant for. Registration now goes through File Transformation's in-process service
/// (see <see cref="Web.FileTransformationRegistrar"/>), so nothing needed it.
/// </para>
/// </summary>
[ApiController]
[Route("MediaOptimizer")]
public class ClientAssetController : ControllerBase
{
    private static ClientBundle? _bundle;

    private readonly ILogger<ClientAssetController> _logger;

    /// <summary>Initializes a new instance of the <see cref="ClientAssetController"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public ClientAssetController(ILogger<ClientAssetController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the assembled bundle, reading the embedded resources on first use. Reading and
    /// re-splicing two embedded files on every request is pure waste when neither can change
    /// without the process restarting.
    /// </summary>
    private static ClientBundle? Bundle
    {
        get
        {
            var cached = _bundle;
            if (cached is not null)
            {
                return cached;
            }

            var js = ReadResource("Web.Resources.client.js");
            if (js is null)
            {
                return null;
            }

            var css = ReadResource("Web.Resources.client.css") ?? string.Empty;

            // The stylesheet ships inside the script so the page makes one request, and so the
            // styles cannot arrive after the dialog has already been opened.
            var text = js.Replace(
                "/*__MEDIAOPTIMIZER_CSS__*/",
                System.Text.Json.JsonSerializer.Serialize(css),
                StringComparison.Ordinal);

            cached = new ClientBundle(text);
            _bundle = cached;
            return cached;
        }
    }

    /// <summary>Serves the client script. Anonymous because a script tag carries no auth header.</summary>
    /// <returns>The JavaScript bundle.</returns>
    [HttpGet("client.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetClientScript()
    {
        var bundle = Bundle;
        if (bundle is null)
        {
            _logger.LogError("[MediaOptimizer] The client script is missing from the plugin assembly");
            return NotFound();
        }

        // Every page load in every open browser asks for this. The contents only change when the
        // plugin is upgraded, so give it a validator and let the browser skip the transfer: the
        // tag is derived from the bundle itself, so an upgrade invalidates it without anyone
        // having to remember to bump a version. The revalidation window is deliberately short --
        // a stale script after an upgrade would be a confusing bug to chase.
        Response.Headers.ETag = bundle.Tag;
        Response.Headers.CacheControl = "public, max-age=300, must-revalidate";

        if (MatchesEtag(Request.Headers.IfNoneMatch, bundle.Tag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Content(bundle.Text, "application/javascript", Encoding.UTF8);
    }

    /// <summary>Checks an If-None-Match header against our tag, honouring the "*" wildcard.</summary>
    /// <param name="header">The header values as sent.</param>
    /// <param name="tag">Our current entity tag.</param>
    /// <returns>Whether the browser already holds this exact bundle.</returns>
    internal static bool MatchesEtag(StringValues header, string tag)
    {
        foreach (var value in header)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            foreach (var candidate in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var trimmed = candidate.StartsWith("W/", StringComparison.Ordinal) ? candidate[2..] : candidate;
                if (trimmed == "*" || string.Equals(trimmed, tag, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The assembled script and its ETag, built once per process.</summary>
    private sealed class ClientBundle
    {
        public ClientBundle(string text)
        {
            Text = text;
            Tag = "\"" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant() + "\"";
        }

        public string Text { get; }

        public string Tag { get; }
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
