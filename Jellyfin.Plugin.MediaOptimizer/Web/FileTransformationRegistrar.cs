using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaOptimizer.Web;

/// <summary>How registering with the File Transformation plugin went.</summary>
public enum RegistrationOutcome
{
    /// <summary>The transformation is registered and the script will be served.</summary>
    Registered = 0,

    /// <summary>The File Transformation plugin is not installed.</summary>
    PluginNotInstalled = 1,

    /// <summary>The plugin is installed but its API did not look the way we expect.</summary>
    ApiMismatch = 2,

    /// <summary>Registration threw.</summary>
    Failed = 3
}

/// <summary>The result of an attempt, with enough detail to act on.</summary>
public class RegistrationResult
{
    /// <summary>Gets or sets what happened.</summary>
    public RegistrationOutcome Outcome { get; set; }

    /// <summary>Gets or sets a human-readable explanation.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>Gets or sets the detected File Transformation version, when installed.</summary>
    public string? DetectedVersion { get; set; }
}

/// <summary>
/// Registers a transformation with IAmParadox27's File Transformation plugin.
/// <para>
/// This goes through that plugin's DI service directly rather than its HTTP endpoint. From
/// version 2.5 the endpoint carries [Authorize(Policy = RequiresElevation)], so an unauthenticated
/// loopback POST is rejected with 401 and the UI silently never appears. Talking to the service
/// in-process avoids authentication entirely, and skips a needless HTTP round trip.
/// </para>
/// <para>
/// Everything is reflection-based on purpose: a compile-time reference would make this plugin
/// fail to load whenever File Transformation is absent or a different version.
/// </para>
/// </summary>
public class FileTransformationRegistrar
{
    private const string AssemblyName = "Jellyfin.Plugin.FileTransformation";
    private const string WriteServiceTypeName = "Jellyfin.Plugin.FileTransformation.Library.IWebFileTransformationWriteService";
    private const string DelegateTypeName = "Jellyfin.Plugin.FileTransformation.Library.TransformFile";

    private readonly IServiceProvider _services;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="FileTransformationRegistrar"/> class.</summary>
    /// <param name="services">The Jellyfin service provider, shared by all plugins.</param>
    /// <param name="logger">Logger.</param>
    public FileTransformationRegistrar(IServiceProvider services, ILogger logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>Finds the File Transformation assembly, if the plugin is loaded.</summary>
    /// <returns>The assembly, or null.</returns>
    public static Assembly? FindAssembly()
    {
        foreach (var context in AssemblyLoadContext.All)
        {
            foreach (var assembly in context.Assemblies)
            {
                if (string.Equals(assembly.GetName().Name, AssemblyName, StringComparison.Ordinal))
                {
                    return assembly;
                }
            }
        }

        return null;
    }

    /// <summary>Registers a transformation for files matching a pattern.</summary>
    /// <param name="id">A stable id; re-registering with the same id replaces the previous entry.</param>
    /// <param name="fileNamePattern">The file to transform, e.g. index.html.</param>
    /// <param name="transform">
    /// Callback receiving the file path and a read/write stream of its current contents.
    /// </param>
    /// <returns>What happened, for the diagnostics panel.</returns>
    public RegistrationResult Register(Guid id, string fileNamePattern, Func<string, Stream, Task> transform)
    {
        var assembly = FindAssembly();
        if (assembly is null)
        {
            return new RegistrationResult
            {
                Outcome = RegistrationOutcome.PluginNotInstalled,
                Detail = "The File Transformation plugin is not installed. Install it from "
                    + "https://www.iamparadox.dev/jellyfin/plugins/manifest.json to get the in-app buttons."
            };
        }

        var version = assembly.GetName().Version?.ToString();

        try
        {
            var serviceType = assembly.GetType(WriteServiceTypeName);
            var delegateType = assembly.GetType(DelegateTypeName);

            if (serviceType is null || delegateType is null)
            {
                return new RegistrationResult
                {
                    Outcome = RegistrationOutcome.ApiMismatch,
                    DetectedVersion = version,
                    Detail = "File Transformation is installed but does not expose the API this plugin expects. "
                        + "It is probably a newer or older major version."
                };
            }

            var service = _services.GetService(serviceType);
            if (service is null)
            {
                return new RegistrationResult
                {
                    Outcome = RegistrationOutcome.ApiMismatch,
                    DetectedVersion = version,
                    Detail = "File Transformation is loaded but its transformation service is not registered yet. "
                        + "Restarting Jellyfin usually resolves this."
                };
            }

            // Their delegate is Task TransformFile(string, Stream); bind it to an adapter whose
            // signature matches exactly, since a lambda cannot be converted to a foreign type.
            var adapter = new TransformAdapter(transform);
            var adapterMethod = typeof(TransformAdapter).GetMethod(
                nameof(TransformAdapter.InvokeAsync),
                BindingFlags.Instance | BindingFlags.Public);

            var boundDelegate = Delegate.CreateDelegate(delegateType, adapter, adapterMethod!);

            var addMethod = serviceType.GetMethod("AddTransformation");
            if (addMethod is null)
            {
                return new RegistrationResult
                {
                    Outcome = RegistrationOutcome.ApiMismatch,
                    DetectedVersion = version,
                    Detail = "File Transformation does not expose AddTransformation; its API has changed."
                };
            }

            addMethod.Invoke(service, [id, fileNamePattern, boundDelegate]);

            _logger.LogInformation(
                "[MediaOptimizer] Registered '{Pattern}' transformation with File Transformation {Version}",
                fileNamePattern,
                version);

            return new RegistrationResult
            {
                Outcome = RegistrationOutcome.Registered,
                DetectedVersion = version,
                Detail = "The client script is being injected into the web client."
            };
        }
        catch (Exception ex) when (ex is TargetInvocationException or MissingMethodException
            or InvalidOperationException or ArgumentException or TypeLoadException)
        {
            _logger.LogError(ex, "[MediaOptimizer] Registering with File Transformation failed");
            return new RegistrationResult
            {
                Outcome = RegistrationOutcome.Failed,
                DetectedVersion = version,
                Detail = "Registering with File Transformation failed: " + (ex.InnerException ?? ex).Message
            };
        }
    }

    /// <summary>Holds the callback so it can be bound to a delegate type from another assembly.</summary>
    private sealed class TransformAdapter
    {
        private readonly Func<string, Stream, Task> _transform;

        public TransformAdapter(Func<string, Stream, Task> transform) => _transform = transform;

        /// <summary>Signature must match File Transformation's TransformFile delegate exactly.</summary>
        /// <param name="path">The file being served.</param>
        /// <param name="contents">Read/write stream of the file's current contents.</param>
        /// <returns>A task.</returns>
        public Task InvokeAsync(string path, Stream contents) => _transform(path, contents);
    }
}
