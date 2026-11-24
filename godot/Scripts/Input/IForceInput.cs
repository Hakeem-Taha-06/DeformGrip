using System;
using DeformGrip.Core;

namespace DeformGrip.App.Input
{
    /// <summary>
    /// Open application-side force input interface (CONTRACT §4.4, godot/README.md Task 3).
    /// Implementations fill a caller-provided buffer with current NodalLoad entries (contract coords,
    /// SI newtons) without allocating per frame. A future external VR controller / force sensor
    /// plugs in by implementing this interface with zero changes to core/ or the mesh renderer.
    /// </summary>
    public interface IForceInput
    {
        string SourceLabel { get; }
        double CurrentAmplitudeN { get; }

        /// <summary>
        /// Fills <paramref name="buffer"/> with the active nodal loads at simulation time
        /// <paramref name="timeSec"/> and returns the number of entries written.
        /// </summary>
        int FillLoads(double timeSec, Span<NodalLoad> buffer);
    }
}
