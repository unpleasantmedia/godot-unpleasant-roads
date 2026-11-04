using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// How far away small things are drawn (fences, road signs, traffic lights, gantries, footpaths): past
/// it they are not drawn at all, so they do not each cost a draw call while only a few pixels big.
/// </summary>
public static class RoadDrawDistance
{
    /// <summary>How far small things are drawn, in metres, and how far past that they go on being drawn once in view (so they do not flicker at the edge).</summary>
    public const float Small = 200f;

    /// <summary>How far fences are drawn: a little further than other small things, being long.</summary>
    public const float Fences = 250f;
    private const float Margin = 10f;

    /// <summary>A node and everything drawn under it, drawn no further off than <see cref="Small"/>.</summary>
    public static void LimitSmall(Node node, float range = Small)
    {
        if (node is GeometryInstance3D self)
            Limit(self, range);
        foreach (var drawn in node.FindChildren("*", "GeometryInstance3D", true, false).OfType<GeometryInstance3D>())
            Limit(drawn, range);
    }

    /// <summary>How big the squares are that many of one thing (fence pieces, lamp posts) are drawn together in.</summary>
    public const float Chunk = 100f;

    /// <summary>
    /// Places (in the world) grouped by the <see cref="Chunk"/>-metre square each is in: drawn a group
    /// at a time, each group is as far off as its own places are, so the far ones are left out while
    /// the near ones are drawn (drawn all together, they would be judged by the middle of them all).
    /// </summary>
    public static System.Collections.Generic.IEnumerable<System.Collections.Generic.List<Transform3D>> Chunks(System.Collections.Generic.IEnumerable<Transform3D> places) =>
        places.GroupBy(p => (Mathf.FloorToInt(p.Origin.X / Chunk), Mathf.FloorToInt(p.Origin.Z / Chunk))).Select(g => g.ToList());

    private static void Limit(GeometryInstance3D drawn, float range)
    {
        drawn.VisibilityRangeEnd = range;
        drawn.VisibilityRangeEndMargin = Margin;
    }
}
