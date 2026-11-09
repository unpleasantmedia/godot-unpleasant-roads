using System;
using System.Collections.Generic;
using Godot;
using V3 = System.Numerics.Vector3;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// The ground roads lie on and are fitted to, on a grid of samples in world space. The plugin knows
/// no terrain itself: a project connects its own through <see cref="RoadLinks.Ground"/>.
/// </summary>
public interface IRoadGround
{
    /// <summary>How many metres apart its samples are.</summary>
    float VertexSpacing { get; }

    /// <summary>How high the ground is at a place, or NaN where there is none.</summary>
    float HeightAt(Vector3 at);

    /// <summary>Where a ray first meets the ground, if it does.</summary>
    Vector3? Intersect(Vector3 from, Vector3 direction);

    /// <summary>A sample's height (a hole's too), or null outside the ground.</summary>
    float? SampleHeight(int x, int z);

    /// <summary>Whether the ground at a place has a hole in it.</summary>
    bool IsHole(Vector3 at);

    /// <summary>Sets samples' heights, and redraws the ground.</summary>
    void SetHeights(IEnumerable<(int X, int Z, float Height)> heights);

    /// <summary>Opens or closes holes at samples, and redraws the ground.</summary>
    void SetHoles(IEnumerable<(int X, int Z, bool Hole)> holes);

    /// <summary>Sets a mask by its name: convex shapes in world X and Z where the ground below its height in tops is neither drawn nor solid. None takes it away.</summary>
    void SetMask(string name, Vector2[][] shapes, float[] tops);

    /// <summary>Takes a mask away.</summary>
    void RemoveMask(string name);

    /// <summary>Writes its changes to its files.</summary>
    void Save();

    /// <summary>Has a callable called with the area (a Rect2, in world X and Z) whenever the ground changes there, if it isn't already.</summary>
    void Follow(Callable changed);
}

/// <summary>A place direction signs can point to, in the group <see cref="RoadLinks.DestinationGroup"/>.</summary>
public interface IRoadDestination
{
    /// <summary>What signs call it.</summary>
    string SignName { get; }

    /// <summary>How important it is: signs show the more important first.</summary>
    int Importance { get; }

    /// <summary>A test of whether a place is inside it, as it is now.</summary>
    Func<V3, bool> Inside();
}

/// <summary>What a barrier is along a road.</summary>
public enum RoadBarrierKind
{
    GuardRail,
    Barrier,
}

/// <summary>What the editor's clicks find a node of a project's by, for <see cref="RoadLinks.PickShape"/>: an outline to fill, moved up or down, or lines.</summary>
public sealed record RoadPickShape(IReadOnlyList<Vector3>? Outline, float Lift = 0f, IReadOnlyList<Vector3>? Lines = null);

/// <summary>What a project tells the plugin. All are optional.</summary>
public static class RoadLinks
{
    /// <summary>The group of what direction signs can point to.</summary>
    public const string DestinationGroup = "road_destinations";

    /// <summary>Gives the ground of a node: the one it is, or the first under it, or null. Without it, roads keep the heights they are drawn at.</summary>
    public static Func<Node?, IRoadGround?>? Ground { get; set; }

    /// <summary>
    /// Makes a barrier along points: its name, its kind, whether it is flipped, its points, and whether
    /// it is drawn. It is not where its network shows a baked look in its place (which draws it): it is
    /// then only to be solid. Without it, roads have none.
    /// </summary>
    public static Func<string, RoadBarrierKind, bool, Vector3[], bool, Node?>? MakeBarrier { get; set; }

    /// <summary>How far street lamps are on, from 0 to 1, as the time of day has it. Without it, they stay on.</summary>
    public static Func<float>? LampLevel { get; set; }

    /// <summary>How far off its lights are on, as the player has set it: they fade out towards there, over the length given, with no limit on how many. Without it, a network's own settings say, with no fade.</summary>
    public static Func<(float Distance, float Fade)?>? LightDistance { get; set; }

    /// <summary>Where a network's ground backup goes; without it, beside the level.</summary>
    public static string? GroundFolder { get; set; }

    /// <summary>Where baked meshes go; without it, res://BakedRoads.</summary>
    public static string? BakedFolder { get; set; }

    /// <summary>The scene of the tree on a parking island; without it, none.</summary>
    public static string? IslandTree { get; set; }

    /// <summary>The scene of the shrubs on a parking island; without it, none.</summary>
    public static string? IslandShrub { get; set; }

    /// <summary>For a node of a project's that clicks in the editor should find, its shape; null for any other.</summary>
    public static Func<Node3D, RoadPickShape?>? PickShape { get; set; }

    /// <summary>Adds a barrier under a node, if the project makes them.</summary>
    internal static void AddBarrier(Node parent, string name, RoadBarrierKind kind, bool flip, Vector3[] points)
    {
        var network = parent;
        while (network is not null and not RoadNetwork)
            network = network.GetParent();
        var drawn = network is not RoadNetwork { ShowsBaked: true };
        if (MakeBarrier?.Invoke(name, kind, flip, points, drawn) is { } barrier)
            parent.AddChild(barrier);
    }
}
