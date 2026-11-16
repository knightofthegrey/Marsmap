namespace Marsmap.AR
{
    /// <summary>Where the headset's passthrough camera is.</summary>
    public enum PassthroughState
    {
        NotStarted,
        Starting,   // asked for, the headset is switching it on
        Running,    // the real world is behind everything we draw
        Failed,     // won't come on; see Problem
    }

    /// <summary>Something that can switch the headset's passthrough camera on.</summary>
    public interface IPassthroughBackend
    {
        void Start();
        /// <summary>Switch passthrough off again (ARView gave up, or the headset stopped).</summary>
        void Stop();
        PassthroughState State { get; }
        /// <summary>Why it Failed (shown in the log), or null.</summary>
        string Problem { get; }
    }

    /// <summary>
    /// The link between ARView (in the main scripts) and the Meta XR Core SDK, which only some
    /// branches have installed. The Meta code (AR/Meta, compiled only when the package
    /// com.meta.xr.sdk.core is installed) puts itself in Backend; without the SDK Backend stays
    /// null and the project still compiles and runs, in VR.
    /// </summary>
    public static class Passthrough
    {
        public static IPassthroughBackend Backend;
    }
}
