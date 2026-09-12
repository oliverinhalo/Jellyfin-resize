using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaOptimizer.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaOptimizer.Tests;

/// <summary>
/// Work that outlives the request that asked for it. Measuring takes a minute and searching for a
/// quality setting takes several, and the read timeout in front of nearly every Jellyfin server is
/// sixty seconds — so the request has to hand back an id and stop holding the line open.
/// </summary>
public class OperationRegistryTests
{
    private static OperationRegistry Registry() => new OperationRegistry(NullLogger<OperationRegistry>.Instance);

    private static async Task<OperationState> SettleAsync(OperationRegistry registry, Guid id)
    {
        // Generous on purpose: this is a test of what the registry records, not of how fast a
        // loaded build server gets round to a background task.
        for (var i = 0; i < 1000; i++)
        {
            var state = registry.Get(id);
            Assert.NotNull(state);
            if (state!.Status != OperationStatus.Running)
            {
                return state;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The operation never finished.");
    }

    [Fact]
    public async Task A_finished_operation_hands_back_what_it_produced()
    {
        using var registry = Registry();

        var id = registry.Start("measure", _ => Task.FromResult<object>("the answer"));
        var state = await SettleAsync(registry, id);

        Assert.Equal(OperationStatus.Completed, state.Status);
        Assert.Equal("the answer", state.Result);
        Assert.Null(state.Error);
        Assert.NotNull(state.FinishedAt);
    }

    /// <summary>
    /// The request that started the work has already answered, so a failure has nowhere to be
    /// thrown to. An unobserved exception on a background task is a server that dies for a reason
    /// nobody can see; here it has to become a message the caller can read.
    /// </summary>
    [Fact]
    public async Task A_failure_becomes_a_message_rather_than_an_unhandled_crash()
    {
        using var registry = Registry();

        var id = registry.Start("measure", _ => throw new InvalidOperationException("ffmpeg is not installed"));
        var state = await SettleAsync(registry, id);

        Assert.Equal(OperationStatus.Failed, state.Status);
        Assert.Equal("ffmpeg is not installed", state.Error);
        Assert.Null(state.Result);
    }

    /// <summary>
    /// Closing the dialog has to stop the encoding. Without this, walking away from a five-minute
    /// search leaves the server working for five minutes for nobody.
    /// </summary>
    [Fact]
    public async Task Cancelling_stops_the_work()
    {
        using var registry = Registry();
        using var started = new SemaphoreSlim(0, 1);

        var id = registry.Start("search", async token =>
        {
            started.Release();
            await Task.Delay(Timeout.Infinite, token);
            return "never";
        });

        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(30)), "The work never started.");
        Assert.True(registry.Cancel(id));

        var state = await SettleAsync(registry, id);
        Assert.Equal(OperationStatus.Cancelled, state.Status);
    }

    [Fact]
    public async Task Cancelling_something_already_finished_says_so_without_throwing()
    {
        using var registry = Registry();

        var id = registry.Start("measure", _ => Task.FromResult<object>(1));
        await SettleAsync(registry, id);

        Assert.False(registry.Cancel(id));
        Assert.False(registry.Cancel(Guid.NewGuid()));
    }

    [Fact]
    public void An_unknown_operation_is_not_found_rather_than_invented()
    {
        using var registry = Registry();
        Assert.Null(registry.Get(Guid.NewGuid()));
    }

    /// <summary>
    /// Nothing here is persisted, so the only way this leaks is by remembering everything for
    /// ever. Past the cap the oldest finished operations are forgotten.
    /// </summary>
    [Fact]
    public async Task It_forgets_old_operations_rather_than_growing_without_limit()
    {
        using var registry = Registry();

        var ids = new Guid[OperationRegistry.MaxOperations + 10];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = registry.Start("measure", _ => Task.FromResult<object>(i));
            await SettleAsync(registry, ids[i]);
        }

        var remembered = 0;
        foreach (var id in ids)
        {
            if (registry.Get(id) is not null)
            {
                remembered++;
            }
        }

        Assert.True(
            remembered <= OperationRegistry.MaxOperations,
            $"It is holding {remembered} operations, past its own cap of {OperationRegistry.MaxOperations}.");

        // And the most recent one is always still there: forgetting the answer somebody is
        // waiting for right now would be worse than holding a few too many.
        Assert.NotNull(registry.Get(ids[^1]));
    }

    /// <summary>
    /// The result is declared as <c>object</c> so that one registry can carry an estimate or a
    /// search result. If that serialised as an empty object the browser would poll a measurement
    /// that took a minute and be handed nothing — so this is asserted rather than assumed.
    /// </summary>
    [Fact]
    public async Task The_result_survives_being_serialised_as_json()
    {
        using var registry = Registry();

        var id = registry.Start("search", _ => Task.FromResult<object>(new QualitySearchResult
        {
            Quality = 24,
            Metric = QualityProbe.Ssim,
            WorstScore = 0.9821d,
            WorstScoreText = "0.9821",
            Verdict = "very hard to tell apart from the source",
            Probes = 8
        }));

        var state = await SettleAsync(registry, id);

        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var json = JsonSerializer.Serialize(state, options);

        Assert.Contains("\"Quality\":24", json, StringComparison.Ordinal);
        Assert.Contains("0.9821", json, StringComparison.Ordinal);
        Assert.Contains("\"Status\":\"Completed\"", json, StringComparison.Ordinal);
    }

    /// <summary>Disposing the registry stops whatever is still running, rather than orphaning it.</summary>
    [Fact]
    public async Task Disposing_the_registry_stops_what_is_still_running()
    {
        var registry = Registry();
        using var started = new SemaphoreSlim(0, 1);
        var stopped = new TaskCompletionSource<bool>();

        registry.Start("search", async token =>
        {
            started.Release();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                stopped.TrySetResult(true);
                throw;
            }

            return "never";
        });

        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(30)), "The work never started.");
        registry.Dispose();

        var finished = await Task.WhenAny(stopped.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(stopped.Task, finished);
    }

    /// <summary>
    /// The work outlives the request that asked for it, which means nothing stops it if the
    /// browser goes away: a closed tab sends no cancellation, and before this work was moved off
    /// the request, a dropped connection was exactly what stopped the encoding. So it has a
    /// deadline of its own.
    /// </summary>
    [Fact]
    public async Task An_operation_nobody_is_waiting_for_any_more_runs_out_of_time()
    {
        using var registry = new OperationRegistry(
            NullLogger<OperationRegistry>.Instance,
            TimeSpan.FromMilliseconds(200));

        var id = registry.Start("search", async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return "never";
        });

        var state = await SettleAsync(registry, id);

        Assert.Equal(OperationStatus.Cancelled, state.Status);
        Assert.Contains("without finishing", state.Error!, StringComparison.Ordinal);
    }

    /// <summary>And an operation that finishes in time is not blamed for the deadline.</summary>
    [Fact]
    public async Task Finishing_in_time_is_not_reported_as_running_out_of_it()
    {
        using var registry = new OperationRegistry(
            NullLogger<OperationRegistry>.Instance,
            TimeSpan.FromSeconds(30));

        var id = registry.Start("measure", _ => Task.FromResult<object>("done"));
        var state = await SettleAsync(registry, id);

        Assert.Equal(OperationStatus.Completed, state.Status);
        Assert.Null(state.Error);
    }
}
