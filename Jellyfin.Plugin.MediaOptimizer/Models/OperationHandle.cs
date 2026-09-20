using System;

namespace Jellyfin.Plugin.MediaOptimizer.Models;

/// <summary>
/// The id of work that is now running, handed back by the request that started it.
/// </summary>
public class OperationHandle
{
    /// <summary>Gets or sets the operation id.</summary>
    public Guid Id { get; set; }
}
