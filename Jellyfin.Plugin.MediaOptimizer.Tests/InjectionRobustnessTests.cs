using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// The injection layer is the one part that talks to another plugin and to private jellyfin-web
/// markup. It must degrade into a clear message rather than an exception, because everything
/// else in the plugin has to keep working when it fails.
/// </summary>
public class InjectionRobustnessTests
{
    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    [Fact]
    public void Registrar_reports_a_missing_plugin_instead_of_throwing()
    {
        // File Transformation is not loaded in the test process, which is exactly the situation
        // a user without it installed is in.
        var registrar = new FileTransformationRegistrar(new EmptyServices(), NullLogger.Instance);

        var result = registrar.Register(Guid.NewGuid(), "index.html", (_, _) => Task.CompletedTask);

        Assert.Equal(RegistrationOutcome.PluginNotInstalled, result.Outcome);
        Assert.Contains("File Transformation", result.Detail, StringComparison.Ordinal);
        // The message has to tell the user what to actually do about it.
        Assert.Contains("iamparadox", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Registrar_finds_no_assembly_when_the_plugin_is_absent() =>
        Assert.Null(FileTransformationRegistrar.FindAssembly());

    [Fact]
    public void Injection_status_starts_populated_so_the_dashboard_always_has_something_to_say()
    {
        var status = WebInjectionHostedService.Status;
        Assert.NotNull(status);
        Assert.False(string.IsNullOrWhiteSpace(status.Detail));
    }

    [Fact]
    public async Task Transform_round_trip_leaves_a_valid_document_with_one_script_tag()
    {
        // Mirrors File Transformation's contract: it hands over a read/write stream positioned at
        // zero and re-reads whatever is left behind.
        const string Page = "<html><head></head><body><div id=\"app\"></div></body></html>";

        using var stream = new MemoryStream();
        var bytes = Encoding.UTF8.GetBytes(Page);
        await stream.WriteAsync(bytes);
        stream.Seek(0, SeekOrigin.Begin);

        var html = await new StreamReader(stream, Encoding.UTF8, leaveOpen: true).ReadToEndAsync();
        var patched = WebInjectionHostedService.InjectInto(html);
        stream.Seek(0, SeekOrigin.Begin);
        var patchedBytes = Encoding.UTF8.GetBytes(patched);
        await stream.WriteAsync(patchedBytes);
        stream.SetLength(patchedBytes.Length);

        stream.Seek(0, SeekOrigin.Begin);
        var result = await new StreamReader(stream, Encoding.UTF8).ReadToEndAsync();

        Assert.Contains("/MediaOptimizer/client.js", result, StringComparison.Ordinal);
        Assert.EndsWith("</html>", result, StringComparison.Ordinal);
        Assert.DoesNotContain("</body></html></body>", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transform_is_idempotent_across_repeated_serves()
    {
        // File Transformation re-runs the callback on every request for index.html.
        const string Page = "<html><body>x</body></html>";

        var once = WebInjectionHostedService.InjectInto(Page);
        var twice = WebInjectionHostedService.InjectInto(once);
        var thrice = WebInjectionHostedService.InjectInto(twice);

        Assert.Equal(once, thrice);

        var occurrences = 0;
        var index = 0;
        while ((index = thrice.IndexOf("client.js", index, StringComparison.Ordinal)) >= 0)
        {
            occurrences++;
            index += 9;
        }

        Assert.Equal(1, occurrences);
        await Task.CompletedTask;
    }
}
