using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// A stretch of road seen from above: its centre line, and how far it reaches left and right of
/// it (signed out from the centre line, as a cross-section's bands are). A junction's outline is a
/// stretch with no width, running round it and back to its first point.
/// </summary>
public readonly record struct FootprintStretch(IReadOnlyList<RoadFrame> Frames, float Left, float Right);

/// <summary>A flat box seen from above: x and z, from Min to Max.</summary>
public readonly record struct FlatBounds(Vector2 Min, Vector2 Max)
{
    public FlatBounds Merge(FlatBounds other) => new(Vector2.Min(Min, other.Min), Vector2.Max(Max, other.Max));

    public FlatBounds Grow(float by) => new(Min - new Vector2(by), Max + new Vector2(by));

    public bool Intersects(FlatBounds other) => Min.X <= other.Max.X && other.Min.X <= Max.X && Min.Y <= other.Max.Y && other.Min.Y <= Max.Y;

    public bool Contains(Vector3 at) => at.X >= Min.X && at.X <= Max.X && at.Z >= Min.Y && at.Z <= Max.Y;
}

/// <summary>
/// Where roads lie, for keeping viaducts' pillars off them: what a road covers, which part of it a
/// change moved, and whether a point is over one.
/// </summary>
public static class RoadFootprint
{
    /// <summary>How far a road's line may move and still count as where it was, in metres.</summary>
    public const float Unmoved = 0.01f;

    /// <summary>The grid a line is sorted into, to find the pieces of it near a point quickly, in metres.</summary>
    private const float Cell = 8f;

    /// <summary>The box a stretch covers, its width included; none for no frames.</summary>
    public static FlatBounds? Bounds(FootprintStretch stretch)
    {
        if (stretch.Frames.Count == 0)
            return null;
        var reach = MathF.Max(MathF.Abs(stretch.Left), MathF.Abs(stretch.Right));
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var frame in stretch.Frames)
        {
            var p = new Vector2(frame.Position.X, frame.Position.Z);
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }

        return new FlatBounds(min, max).Grow(reach);
    }

    /// <summary>The box several stretches cover; none if none have frames.</summary>
    public static FlatBounds? Bounds(IEnumerable<FootprintStretch> stretches)
    {
        FlatBounds? all = null;
        foreach (var stretch in stretches)
        {
            if (Bounds(stretch) is { } b)
                all = all is { } a ? a.Merge(b) : b;
        }

        return all;
    }

    /// <summary>
    /// Where a road lies differently from before: the box round every bit of its line that moved
    /// (the old and new places alike), widths included; none if nothing moved. A road resampled
    /// along the same line has not moved. A stretch added, taken away or made wider or narrower
    /// counts as moved all along.
    /// </summary>
    public static FlatBounds? Changed(IReadOnlyList<FootprintStretch> before, IReadOnlyList<FootprintStretch> after)
    {
        if (before.Count != after.Count)
            return Bounds(before.Concat(after));
        FlatBounds? changed = null;
        void Add(FlatBounds? more)
        {
            if (more is { } m)
                changed = changed is { } c ? c.Merge(m) : m;
        }

        for (var i = 0; i < before.Count; i++)
        {
            var (was, now) = (before[i], after[i]);
            if (MathF.Abs(was.Left - now.Left) > Unmoved || MathF.Abs(was.Right - now.Right) > Unmoved)
            {
                Add(Bounds(was));
                Add(Bounds(now));
                continue;
            }

            var reach = MathF.Max(MathF.Abs(now.Left), MathF.Abs(now.Right));
            Add(Moved(was.Frames, now.Frames)?.Grow(reach));
            Add(Moved(now.Frames, was.Frames)?.Grow(reach));
        }

        return changed;
    }

    /// <summary>The box round the points of one line that are off another line; none if all are on it.</summary>
    private static FlatBounds? Moved(IReadOnlyList<RoadFrame> points, IReadOnlyList<RoadFrame> line)
    {
        if (points.Count == 0)
            return null;
        if (line.Count < 2)
            return Bounds(new FootprintStretch(points, 0f, 0f));
        var grid = Grid(line);
        FlatBounds? moved = null;
        foreach (var frame in points)
        {
            var p = frame.Position;
            if (Nearest(grid, line, p) <= Unmoved)
                continue;
            var here = new FlatBounds(new Vector2(p.X, p.Z), new Vector2(p.X, p.Z));
            moved = moved is { } m ? m.Merge(here) : here;
        }

        return moved;
    }

    /// <summary>Each piece of a line (from frame i to i + 1) under every grid cell its box touches.</summary>
    private static Dictionary<(int, int), List<int>> Grid(IReadOnlyList<RoadFrame> line)
    {
        var grid = new Dictionary<(int, int), List<int>>();
        for (var i = 0; i + 1 < line.Count; i++)
        {
            var (a, b) = (line[i].Position, line[i + 1].Position);
            for (var x = CellOf(MathF.Min(a.X, b.X)); x <= CellOf(MathF.Max(a.X, b.X)); x++)
            {
                for (var z = CellOf(MathF.Min(a.Z, b.Z)); z <= CellOf(MathF.Max(a.Z, b.Z)); z++)
                {
                    if (!grid.TryGetValue((x, z), out var pieces))
                        grid[(x, z)] = pieces = new List<int>();
                    pieces.Add(i);
                }
            }
        }

        return grid;
    }

    private static int CellOf(float v) => (int)MathF.Floor(v / Cell);

    /// <summary>How far a point is from the nearest piece of a line in its grid cell, in 3D; no nearer than infinity if none are.</summary>
    private static float Nearest(Dictionary<(int, int), List<int>> grid, IReadOnlyList<RoadFrame> line, Vector3 p)
    {
        if (!grid.TryGetValue((CellOf(p.X), CellOf(p.Z)), out var pieces))
            return float.MaxValue;
        var best = float.MaxValue;
        foreach (var i in pieces)
        {
            var (a, b) = (line[i].Position, line[i + 1].Position);
            var ab = b - a;
            var t = ab.LengthSquared() > 1e-10f ? Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f) : 0f;
            best = MathF.Min(best, Vector3.Distance(p, a + ab * t));
        }

        return best;
    }

    /// <summary>
    /// Whether a road stretch runs under a point: at least underBy metres below it, and within its
    /// width and margin more either side, as far along as a pillar half metres wide reaches.
    /// </summary>
    public static bool Under(FootprintStretch stretch, Vector3 at, float half, float margin, float underBy, float spacing)
    {
        var (left, right) = (stretch.Left - margin, stretch.Right + margin);
        var reach = MathF.Max(-left, right) + spacing;
        foreach (var frame in stretch.Frames)
        {
            var offset = at - frame.Position;
            if (frame.Position.Y > at.Y - underBy || offset.X * offset.X + offset.Z * offset.Z > reach * reach)
                continue;
            var level = frame.Forward with { Y = 0f };
            if (level.LengthSquared() < 1e-8f)
                continue;
            var across = Vector3.Dot(offset, frame.Right);
            if (MathF.Abs(Vector3.Dot(offset, Vector3.Normalize(level))) <= spacing / 2f + half && across >= left && across <= right)
                return true;
        }

        return false;
    }
}
