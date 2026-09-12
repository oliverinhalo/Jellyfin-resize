using System;

using Jellyfin.Plugin.MediaOptimizer.Web;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

public class InjectionTests
{
    private const string Page = "<html><head><title>Jellyfin</title></head><body><div id=\"app\"></div></body></html>";

    [Fact]
    public void Injects_the_script_immediately_before_the_closing_body_tag()
    {
        var result = WebInjectionHostedService.InjectInto(Page);

        Assert.Contains("/MediaOptimizer/client.js", result, StringComparison.Ordinal);
        Assert.True(
            result.IndexOf("client.js", StringComparison.Ordinal) < result.IndexOf("</body>", StringComparison.Ordinal),
            "The script tag must land inside the body.");
    }

    [Fact]
    public void Injecting_twice_leaves_exactly_one_block()
    {
        // File Transformation can re-run the callback; a second pass must replace, not duplicate.
        var once = WebInjectionHostedService.InjectInto(Page);
        var twice = WebInjectionHostedService.InjectInto(once);

        Assert.Equal(once, twice);
        Assert.Equal(1, CountOccurrences(twice, "MediaOptimizer:start"));
    }

    [Fact]
    public void Leaves_a_document_without_a_body_tag_untouched()
    {
        const string Fragment = "<div>no body element here</div>";
        Assert.Equal(Fragment, WebInjectionHostedService.InjectInto(Fragment));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
