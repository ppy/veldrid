namespace Veldrid
{
    /// <summary>
    ///     Indicates the low latency mode to use for a <see cref="GraphicsDevice" />.
    ///     <see cref="OnWithBoost" /> only has an effect beyond <see cref="On" /> on certain platforms such as NVIDIA Reflex.
    /// </summary>
    public enum LowLatencyMode : byte
    {
        Off,
        On,
        OnWithBoost,
    }
}
