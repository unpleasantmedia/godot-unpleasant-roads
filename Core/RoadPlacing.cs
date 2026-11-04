using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>Telling whether something is already where it is to be put.</summary>
public static class RoadPlacing
{
    /// <summary>
    /// How close counts as there, in metres. A node far from its parent's origin cannot be put
    /// exactly anywhere (floats lose the last digits): read back, it is a little off, and a check
    /// for exactly there would move it again, and again.
    /// </summary>
    public const float Tolerance = 0.001f;

    /// <summary>Whether two places are the same, as near as a node can be put.</summary>
    public static bool Near(this Vector3 a, Vector3 b) => a.DistanceSquaredTo(b) <= Tolerance * Tolerance;

    /// <summary>
    /// Whether a node has really moved since it was last asked, keeping where it is now. Entering
    /// the tree again (switching to its scene in the editor) tells it its transform changed when
    /// it did not, and building again for that would build the whole level again.
    /// </summary>
    public static bool Moved(this Node3D node, ref Transform3D? last)
    {
        var now = node.GlobalTransform;
        if (last is { } was && was.Origin.Near(now.Origin) && was.Basis.IsEqualApprox(now.Basis))
            return false;
        last = now;
        return true;
    }
}
