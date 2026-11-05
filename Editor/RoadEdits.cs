#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantRoads.Editor;

/// <summary>
/// Everything needed to split a road at a point along it, and to put it back: the nodes it takes
/// and makes are kept here between doing and undoing, so redoing uses the very same ones.
/// </summary>
[Tool]
public partial class RoadSplit : RefCounted
{
    public Road Road = null!;
    /// <summary>What the road is split at: a junction, or a fork (see RoadFork).</summary>
    public Node3D Junction = null!;
    public float Distance;

    /// <summary>
    /// For a road joined back onto itself: which of its ends (the one being drawn) comes to the
    /// junction too, closing the loop, and the point that brings it there.
    /// </summary>
    public bool ClosesEnd;
    public bool ClosesStart;
    public RoadPoint? Closer;

    // Made on the first split, kept for redoing it.
    public Road? Second;
    public RoadPoint? EndOfFirst;
    public RoadPoint? StartOfSecond;

    // How it was, for undoing it.
    public List<RoadPoint> Original = new();
    public bool WasClosed;
    public Node3D? WasEndJunction;

    /// <summary>What was built along the road (its viaducts, tunnels and ramps): each with where it was along it.</summary>
    public List<(Node3D Node, float Start, float Length)> Built = new();

    /// <summary>The pieces a split cut off what was built across the split, on the far side of it.</summary>
    public List<RoadStructure> Pieces = new();

    /// <summary>The points either side of the split, pinned to the way the road ran through them, and how each was before.</summary>
    public List<(RoadPoint Point, Vector3 Tangent, bool Followed, Basis Basis)> Pinned = new();

    /// <summary>The way the road ran where it was split.</summary>
    public Vector3 Way;
}

/// <summary>
/// Everything needed to merge one road onto the loose end of another, and to take it apart again:
/// the road kept, the road merged into it (at its end, or its start), and whether the merged one
/// runs the other way and so is turned round.
/// </summary>
[Tool]
public partial class RoadMerge : RefCounted
{
    public Road Kept = null!;
    public Road Merged = null!;
    public bool AtEnd;
    public bool Reversed;

    // How it was, for undoing it.
    public Node? MergedParent;
    public int MergedIndex;
    public List<RoadPoint> KeptPoints = new();
    public List<RoadPoint> MergedPoints = new();
    public Node3D? KeptStartJunction;
    public Node3D? KeptEndJunction;
    public Node3D? MergedStartJunction;
    public Node3D? MergedEndJunction;
    public string KeptStreetName = "";
    public List<(Node3D Node, Road Road, float Start, float Length, RoadSide Side)> Built = new();
}

/// <summary>A fork put into a road (see RoadsPlugin.AddFork): its split, and the straight branch's points taken out over its length.</summary>
[Tool]
public partial class ForkMake : RefCounted
{
    public RoadSplit Split = null!;
    public bool Merge;
    public Vector3 At;
    public float Length;
    public Road? Straight;
    public int TrunkLanes;
    public List<(RoadPoint Point, Vector3 At, int Index)> Dropped = new();
}

/// <summary>Changes to roads bigger than moving a point: splitting a road at a junction, merging two into one, and putting them back.</summary>
public static class RoadEdits
{
    /// <summary>
    /// Splits a road where the junction is, at a distance along it: an open road becomes two, the
    /// first ending at the junction and the second starting there (and ending where the first
    /// used to); a closed one opens there, starting and ending at the junction.
    /// </summary>
    public static void Split(RoadSplit split)
    {
        var road = split.Road;
        var root = road.Owner;
        var points = road.Points.ToList();
        var positions = points.ToDictionary(p => p, p => p.GlobalPosition);
        if (split.Original.Count == 0)
        {
            split.Original = points;
            split.WasClosed = road.Closed;
            split.WasEndJunction = road.EndJunction;
        }

        var at = split.Junction.GlobalPosition;
        split.EndOfFirst ??= new RoadPoint { Name = "RoadPoint" };
        split.StartOfSecond ??= new RoadPoint { Name = "RoadPoint" };

        // Which points lie before the split and which after, by how far along the road each is.
        var frames = road.FullFrames();
        var total = frames.Count > 0 ? frames[^1].Distance : 0f;
        if (split.Built.Count == 0)
        {
            split.Built = road.Structures.Select(b => ((Node3D)b, b.Start, b.Length))
                .Concat(road.Ramps.Select(r => ((Node3D)r, r.Start, r.Length))).ToList();
        }
        var along = PointsAlong(frames, points, road.Closed);

        var before = points.Where((p, i) => along[i] < split.Distance).ToList();
        var after = points.Where((p, i) => along[i] >= split.Distance).ToList();

        // The road keeps its shape: the points either side of the split, and the new ends at the
        // junction, hold the way it ran through them, rather than turning towards new neighbours.
        if (split.Pinned.Count == 0 && frames.Count > 1 && points.Count >= 2)
        {
            var curve = new RoadCurve(points.Select(p => p.ToCurvePoint()).ToList(), road.Closed);
            foreach (var point in new[] { before.LastOrDefault(), after.FirstOrDefault() }.OfType<RoadPoint>().Distinct())
                split.Pinned.Add((point, curve.TangentAt(points.IndexOf(point)).ToGodot(), point.FollowRotation, point.GlobalBasis));
            split.Way = RoadCurve.At(frames, split.Distance).Forward.ToGodot();
        }

        foreach (var (point, tangent, followed, _) in split.Pinned)
        {
            if (!followed)
                point.Pin(tangent);
        }

        if (split.WasClosed)
        {
            // Open the ring here: from the junction on round, back to the junction.
            road.Closed = false;
            foreach (var point in points)
                road.RemoveChild(point);
            Put(road, split.StartOfSecond, at, root);
            foreach (var point in after.Concat(before))
                Put(road, point, positions[point], root);
            Put(road, split.EndOfFirst, at, root);
            split.StartOfSecond.Pin(split.Way);
            split.EndOfFirst.Pin(split.Way);
            road.StartJunction = split.Junction;
            road.EndJunction = split.Junction;
            // Everything built along it now counts from the junction on round.
            foreach (var (node, start, length) in split.Built)
            {
                var from = start >= split.Distance ? start - split.Distance : start + total - split.Distance;
                if (node is RoadStructure structure && from + length > total)
                {
                    // Across the split: the part past it carries on from the new start.
                    Set(structure, from, total - from);
                    Piece(split, road, structure, 0f, from + length - total, root);
                }
                else
                {
                    Set(node, from, length);
                }
            }

            return;
        }

        split.Second ??= new Road
        {
            Name = road.Name,
            StreetName = road.StreetName,
            SpeedLimit = road.SpeedLimit,
            Buildings = road.Buildings,
            ForwardLanes = road.ForwardLanes,
            BackwardLanes = road.BackwardLanes,
            LaneWidth = road.LaneWidth,
            ShoulderWidth = road.ShoulderWidth,
            MedianWidth = road.MedianWidth,
            Median = road.Median,
            ShoulderHeight = road.ShoulderHeight,
            ShoulderAngle = road.ShoulderAngle,
            SurfaceMaterial = road.SurfaceMaterial,
            WhiteLineMaterial = road.WhiteLineMaterial,
            YellowLineMaterial = road.YellowLineMaterial,
            BankMaterial = road.BankMaterial,
            StructureMaterial = road.StructureMaterial,
        };
        var parent = road.GetParent();
        parent.AddChild(split.Second, true);
        parent.MoveChild(split.Second, road.GetIndex() + 1);
        if (root is not null)
            split.Second.Owner = root;

        foreach (var point in after)
            road.RemoveChild(point);
        Put(road, split.EndOfFirst, at, root);
        Put(split.Second, split.StartOfSecond, at, root);
        foreach (var point in after)
            Put(split.Second, point, positions[point], root);
        split.EndOfFirst.Pin(split.Way);
        split.StartOfSecond.Pin(split.Way);
        // The new road's origin in its middle, not where the one it came from had it.
        if (split.Second.Points is { Count: > 0 } secondPoints)
        {
            var box = new Aabb(secondPoints[0].GlobalPosition, Vector3.Zero);
            foreach (var point in secondPoints)
                box = box.Expand(point.GlobalPosition);
            split.Second.MoveOrigin(box.GetCenter());
        }

        split.Second.StartJunction = split.Junction;
        split.Second.EndJunction = split.WasEndJunction;
        road.EndJunction = split.Junction;

        // Joined back onto itself: the end being drawn comes round to the junction as well.
        if (split.ClosesEnd || split.ClosesStart)
        {
            split.Closer ??= new RoadPoint { Name = "RoadPoint" };
            if (split.ClosesEnd)
            {
                Put(split.Second, split.Closer, at, root);
                split.Second.EndJunction = split.Junction;
            }
            else
            {
                Put(road, split.Closer, at, root);
                road.MoveChild(split.Closer, 0);
                road.StartJunction = split.Junction;
            }
        }

        // What is built past the split goes with the second road; what is built across it is cut in two there.
        foreach (var (node, start, length) in split.Built)
        {
            if (start >= split.Distance)
            {
                node.GetParent()?.RemoveChild(node);
                split.Second.AddChild(node, true);
                if (root is not null)
                    node.Owner = root;
                Set(node, start - split.Distance, length);
            }
            else if (node is RoadStructure structure && start + length > split.Distance)
            {
                Set(structure, start, split.Distance - start);
                Piece(split, split.Second, structure, 0f, start + length - split.Distance, root);
            }
        }
    }

    private static void Set(Node3D node, float start, float length)
    {
        switch (node)
        {
            case RoadStructure structure:
                structure.Start = start;
                structure.Length = length;
                break;
            case RoadRamp ramp:
                ramp.Start = start;
                break;
        }
    }

    /// <summary>A copy of a structure, over this stretch of a road: the part of it past a split.</summary>
    private static void Piece(RoadSplit split, Road road, RoadStructure of, float start, float length, Node? root)
    {
        var piece = (RoadStructure)of.Duplicate();
        piece.Start = start;
        piece.Length = length;
        road.AddChild(piece, true);
        if (root is not null)
            piece.Owner = root;
        split.Pieces.Add(piece);
    }

    /// <summary>Puts a split road back as it was: its points in their old order, the added ones gone.</summary>
    public static void Unsplit(RoadSplit split)
    {
        var road = split.Road;
        var root = road.Owner;
        var positions = split.Original.ToDictionary(p => p, p => p.GlobalPosition);

        foreach (var added in new Node?[] { split.EndOfFirst, split.StartOfSecond, split.Closer })
            added?.GetParent()?.RemoveChild(added);
        if (split.ClosesStart)
            road.StartJunction = null;
        foreach (var point in split.Original)
            point.GetParent()?.RemoveChild(point);
        foreach (var point in split.Original)
            Put(road, point, positions[point], root);
        foreach (var (point, _, followed, basis) in split.Pinned)
        {
            point.GlobalBasis = basis;
            point.FollowRotation = followed;
        }

        // What was built along it back where it was, the pieces cut off it gone.
        foreach (var piece in split.Pieces)
        {
            piece.GetParent()?.RemoveChild(piece);
            piece.QueueFree();
        }

        split.Pieces.Clear();
        foreach (var (node, start, length) in split.Built)
        {
            if (node.GetParent() != road)
            {
                node.GetParent()?.RemoveChild(node);
                road.AddChild(node, true);
                if (root is not null)
                    node.Owner = root;
            }

            Set(node, start, length);
            if (node is RoadRamp ramp)
                ramp.Length = length;
        }

        if (split.Second is { } second && second.GetParent() is { } parent)
            parent.RemoveChild(second);

        road.Closed = split.WasClosed;
        road.EndJunction = split.WasEndJunction;
        if (split.WasClosed)
            road.StartJunction = null;
    }

    /// <summary>
    /// Whether one road can be merged onto another's loose end: they are alike across, the merged
    /// one turned round (its lanes each way swapped) if it runs the other way.
    /// </summary>
    public static bool CanMerge(Road kept, Road merged, bool reversed)
    {
        var (a, b) = (kept.CrossSection, merged.CrossSection);
        if (reversed)
            b = b with { ForwardLanes = b.BackwardLanes, BackwardLanes = b.ForwardLanes };
        // Their banks may differ: the merged road takes the kept one's.
        b = b with { ShoulderHeight = a.ShoulderHeight, ShoulderAngle = a.ShoulderAngle };
        return a == b && !kept.Closed && !merged.Closed;
    }

    /// <summary>
    /// Merges one road onto the loose end of another: its points carry on the kept road's (turned
    /// round if it ran the other way), what is built along it moves to its new place along the kept
    /// road, its far end's junction becomes the kept road's there, and it is gone.
    /// </summary>
    public static void Merge(RoadMerge merge)
    {
        var (kept, merged) = (merge.Kept, merge.Merged);
        var root = kept.Owner;
        if (merge.KeptPoints.Count == 0)
        {
            merge.KeptPoints = kept.Points.ToList();
            merge.MergedPoints = merged.Points.ToList();
            merge.MergedParent = merged.GetParent();
            merge.MergedIndex = merged.GetIndex();
            merge.KeptStartJunction = kept.StartJunction;
            merge.KeptEndJunction = kept.EndJunction;
            merge.MergedStartJunction = merged.StartJunction;
            merge.MergedEndJunction = merged.EndJunction;
            merge.KeptStreetName = kept.StreetName;
            merge.Built = kept.Structures.Select(s => ((Node3D)s, kept, s.Start, s.Length, RoadSide.Right))
                .Concat(kept.Ramps.Select(r => ((Node3D)r, kept, r.Start, r.Length, r.Side)))
                .Concat(merged.Structures.Select(s => ((Node3D)s, merged, s.Start, s.Length, RoadSide.Right)))
                .Concat(merged.Ramps.Select(r => ((Node3D)r, merged, r.Start, r.Length, r.Side)))
                .ToList();
        }

        var mergedLength = Length(merged);
        var farJunction = merge.Reversed ? merged.StartJunction : merged.EndJunction;
        if (!merge.AtEnd)
            farJunction = merge.Reversed ? merged.EndJunction : merged.StartJunction;

        // Its points, in the order they now run, carrying on from the kept road's end (or before its start).
        var incoming = merge.Reversed ? merge.MergedPoints.AsEnumerable().Reverse().ToList() : merge.MergedPoints.ToList();
        var positions = incoming.ToDictionary(p => p, p => p.GlobalTransform);
        foreach (var point in incoming)
            merged.RemoveChild(point);
        for (var i = 0; i < incoming.Count; i++)
        {
            var point = incoming[i];
            kept.AddChild(point, true);
            kept.MoveChild(point, merge.AtEnd ? kept.GetChildCount() - 1 : i);
            if (root is not null)
                point.Owner = root;
            var transform = positions[point];
            // A point turned round faces the other way along the road.
            if (merge.Reversed && point.FollowRotation)
                transform.Basis = transform.Basis.Rotated(Vector3.Up, Mathf.Pi);
            point.GlobalTransform = transform;
        }

        if (merge.AtEnd)
            kept.EndJunction = farJunction;
        else
            kept.StartJunction = farJunction;
        merged.StartJunction = null;
        merged.EndJunction = null;

        // What is built along each, at its place along the merged road.
        var frames = kept.FullFrames();
        var keptPoints = kept.Points.ToList();
        var pointsAlong = frames.Count > 1 ? PointsAlong(frames, keptPoints, kept.Closed) : keptPoints.Select(_ => 0f).ToList();
        float AlongAt(RoadPoint? point) => point is not null && keptPoints.IndexOf(point) is var i and >= 0 ? pointsAlong[i] : 0f;
        var mergedFrom = AlongAt(incoming.FirstOrDefault());
        var keptFrom = AlongAt(merge.KeptPoints.FirstOrDefault());
        foreach (var (node, from, start, length, side) in merge.Built)
        {
            float at;
            var flip = false;
            if (from == kept)
            {
                at = merge.AtEnd ? start : keptFrom + start;
            }
            else
            {
                // The merged road's part begins where its first point (as it now runs) is.
                var offset = merge.AtEnd ? mergedFrom : 0f;
                at = merge.Reversed ? offset + mergedLength - start - length : offset + start;
                flip = merge.Reversed;
                node.GetParent()?.RemoveChild(node);
                kept.AddChild(node, true);
                if (root is not null)
                    node.Owner = root;
            }

            switch (node)
            {
                case RoadStructure structure:
                    structure.Start = at;
                    structure.Length = length;
                    break;
                case RoadRamp ramp:
                    ramp.Start = at;
                    if (flip)
                        ramp.Side = side == RoadSide.Right ? RoadSide.Left : RoadSide.Right;
                    break;
            }
        }

        // An unnamed road takes the name of the one merged into it.
        if (string.IsNullOrWhiteSpace(kept.StreetName))
            kept.StreetName = merged.StreetName;
        merged.GetParent()?.RemoveChild(merged);
    }

    /// <summary>Takes a merged road apart again, as it was before.</summary>
    public static void Unmerge(RoadMerge merge)
    {
        var (kept, merged) = (merge.Kept, merge.Merged);
        var root = kept.Owner;
        if (merge.MergedParent is { } parent && merged.GetParent() is null)
        {
            parent.AddChild(merged, true);
            parent.MoveChild(merged, Mathf.Min(merge.MergedIndex, parent.GetChildCount() - 1));
            if (root is not null)
                merged.Owner = root;
        }

        var transforms = merge.MergedPoints.ToDictionary(p => p, p => p.GlobalTransform);
        foreach (var point in merge.MergedPoints)
        {
            point.GetParent()?.RemoveChild(point);
            merged.AddChild(point, true);
            if (root is not null)
                point.Owner = root;
            var transform = transforms[point];
            if (merge.Reversed && point.FollowRotation)
                transform.Basis = transform.Basis.Rotated(Vector3.Up, Mathf.Pi);
            point.GlobalTransform = transform;
        }

        foreach (var (node, from, start, length, side) in merge.Built)
        {
            if (node.GetParent() != from)
            {
                node.GetParent()?.RemoveChild(node);
                from.AddChild(node, true);
                if (root is not null)
                    node.Owner = root;
            }

            switch (node)
            {
                case RoadStructure structure:
                    structure.Start = start;
                    structure.Length = length;
                    break;
                case RoadRamp ramp:
                    ramp.Start = start;
                    ramp.Side = side;
                    break;
            }
        }

        kept.StartJunction = merge.KeptStartJunction;
        kept.EndJunction = merge.KeptEndJunction;
        merged.StartJunction = merge.MergedStartJunction;
        merged.EndJunction = merge.MergedEndJunction;
        kept.StreetName = merge.KeptStreetName;
    }

    private static float Length(Road road)
    {
        var frames = road.FullFrames();
        return frames.Count > 0 ? frames[^1].Distance : 0f;
    }

    /// <summary>How far along a road's frames the point nearest this position is.</summary>
    /// <summary>
    /// How far along a road each of its points is, taken in order, each from where the one before
    /// it is on: a road coming back round to where it started has its last point there too, and
    /// that is its end, not its start.
    /// </summary>
    public static List<float> PointsAlong(IReadOnlyList<RoadFrame> frames, IReadOnlyList<RoadPoint> points, bool closed)
    {
        var along = new List<float>();
        var total = frames.Count > 0 ? frames[^1].Distance : 0f;
        for (var i = 0; i < points.Count; i++)
        {
            var from = i == 0 ? 0f : along[^1];
            along.Add(!closed && i == points.Count - 1 ? total : Along(frames.Where(f => f.Distance >= from).ToList(), points[i].GlobalPosition));
        }

        return along;
    }

    public static float Along(IReadOnlyList<RoadFrame> frames, Vector3 position)
    {
        var best = 0f;
        var bestDistance = float.MaxValue;
        foreach (var frame in frames)
        {
            var d = frame.Position.ToGodot().DistanceSquaredTo(position);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = frame.Distance;
            }
        }

        return best;
    }

    private static void Put(Road road, RoadPoint point, Vector3 at, Node? root)
    {
        road.AddChild(point, true);
        if (root is not null)
            point.Owner = root;
        point.GlobalPosition = at;
    }
}
#endif
