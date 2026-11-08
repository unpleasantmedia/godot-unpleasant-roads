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

    /// <summary>How far the smallest parts of long things are drawn (a guard rail's posts), in metres: its rail goes on further.</summary>
    public const float Near = 150f;

    /// <summary>The group of things drawn only as far as <see cref="Near"/>, or the view distance set if that is less.</summary>
    public const string NearGroup = "NearObjectViewDistance";

    /// <summary>A node and everything drawn under it, drawn no further off than <see cref="Near"/> (or the view distance set, if less).</summary>
    public static void LimitNear(Node node)
    {
        foreach (var drawn in (node is GeometryInstance3D self ? new[] { self } : []).Concat(node.FindChildren("*", "GeometryInstance3D", true, false).OfType<GeometryInstance3D>()))
        {
            drawn.VisibilityRangeEnd = Mathf.Min(Near, !Engine.IsEditorHint() && ViewDistance is { } set ? set : Near);
            drawn.VisibilityRangeEndMargin = Margin;
            drawn.AddToGroup(Group, true);
            drawn.AddToGroup(NearGroup, true);
        }
    }

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

    /// <summary>The group of things drawn as far as the level is (see <see cref="LevelViewDistance"/>).</summary>
    public const string LevelGroup = "LevelViewDistance";

    /// <summary>In a game, how far off the level is drawn, in metres, as its player set it; unset, as far as can be seen.</summary>
    public static float? LevelViewDistance { get; set; }

    /// <summary>A big part of the roads (a baked square of them), drawn no further off than the level is.</summary>
    public static void LimitLevel(GeometryInstance3D drawn)
    {
        drawn.VisibilityRangeEnd = LevelViewDistance ?? 0f;
        drawn.VisibilityRangeEndMargin = Margin;
        drawn.AddToGroup(LevelGroup);
    }

    /// <summary>The group of everything limited here, for a game's view distance setting to find (see <see cref="ViewDistance"/>).</summary>
    public const string Group = "ObjectViewDistance";

    /// <summary>In a game, how far all of it is drawn instead, in metres, as its player set it; unset, each its own distance as built.</summary>
    public static float? ViewDistance { get; set; }

    private static void Limit(GeometryInstance3D drawn, float range)
    {
        drawn.VisibilityRangeEnd = !Engine.IsEditorHint() && ViewDistance is { } set ? set : range;
        drawn.VisibilityRangeEndMargin = Margin;
        drawn.AddToGroup(Group, true);
    }
}
