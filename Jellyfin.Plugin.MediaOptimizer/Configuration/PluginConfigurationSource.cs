namespace Jellyfin.Plugin.MediaOptimizer.Configuration;

/// <summary>
/// Reads and writes the plugin's settings.
/// <para>
/// A seam over <c>Plugin.Instance.Configuration</c>, which is a static and therefore needs a
/// loaded Jellyfin server behind it. The parts that decide which of a user's files get rewritten
/// take this instead, so their behaviour can be tested against settings a test chose rather than
/// against whatever a server happens to hold.
/// </para>
/// </summary>
public interface IPluginConfigurationSource
{
    /// <summary>Gets the current settings.</summary>
    PluginConfiguration Configuration { get; }

    /// <summary>Persists changes made to the settings.</summary>
    void Save();
}

/// <inheritdoc />
public class PluginConfigurationSource : IPluginConfigurationSource
{
    /// <inheritdoc />
    public PluginConfiguration Configuration =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public void Save() => Plugin.Instance?.SaveConfiguration();
}
