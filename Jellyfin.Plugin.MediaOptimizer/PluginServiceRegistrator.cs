using Jellyfin.Plugin.MediaOptimizer.Core;
using Jellyfin.Plugin.MediaOptimizer.Jobs;
using Jellyfin.Plugin.MediaOptimizer.Move;
using Jellyfin.Plugin.MediaOptimizer.Output;
using Jellyfin.Plugin.MediaOptimizer.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.MediaOptimizer;

/// <summary>Wires the plugin's services into Jellyfin's container at startup.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IFfmpegRunner, FfmpegRunner>();
        serviceCollection.AddSingleton<ICapabilityService, CapabilityService>();
        serviceCollection.AddSingleton<IMediaProbeService, MediaProbeService>();
        serviceCollection.AddSingleton<IEncodePlanner, EncodePlanner>();
        serviceCollection.AddSingleton<ISizeEstimator, SizeEstimator>();

        serviceCollection.AddSingleton<LibraryReconciler>();
        serviceCollection.AddSingleton<ILibraryReconciler>(sp => sp.GetRequiredService<LibraryReconciler>());
        serviceCollection.AddSingleton<IVerificationService, VerificationService>();
        serviceCollection.AddSingleton<IOutputPolicyService, OutputPolicyService>();

        serviceCollection.AddSingleton<IJobStore, JobStore>();

        // One instance serving both the hosted-service loop and the cancel API.
        serviceCollection.AddSingleton<JobQueueService>();
        serviceCollection.AddSingleton<IJobQueueService>(sp => sp.GetRequiredService<JobQueueService>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<JobQueueService>());

        // Moving media between drives is its own queue: it shares the library reconciler and the
        // job history limit with conversions, but a move never re-encodes anything, so a stalled
        // encode must not hold one up and vice versa.
        serviceCollection.AddSingleton<IMoveJobStore, MoveJobStore>();
        serviceCollection.AddSingleton<IMediaMoveService, MediaMoveService>();
        serviceCollection.AddSingleton<MoveQueueService>();
        serviceCollection.AddSingleton<IMoveQueueService>(sp => sp.GetRequiredService<MoveQueueService>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<MoveQueueService>());

        serviceCollection.AddHostedService<WebInjectionHostedService>();
    }
}
