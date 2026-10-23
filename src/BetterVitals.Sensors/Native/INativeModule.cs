namespace BetterVitals.Sensors.Native;

/// <summary>
/// One gap-filling area of the native source (drives, memory, GPU, CPU, network). Called on the engine thread only.
/// </summary>
internal interface INativeModule : IDisposable
{
    string Name { get; }

    /// <summary>
    /// Finds what is missing upstream and registers it through <paramref name="builder"/>. Drops what the previous
    /// discovery held first. May throw: the source logs it and carries on with the other modules.
    /// </summary>
    void Discover(InventoryBuilder builder);

    /// <summary>Closes OS handles (before sleep, before the next discovery).</summary>
    void Release();
}
