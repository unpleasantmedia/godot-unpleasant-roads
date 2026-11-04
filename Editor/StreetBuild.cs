#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantRoads.Editor;

/// <summary>
/// A street to lay: its name, its speed limit, its lanes each way, and the line it runs along,
/// seen from above (x and z), point to point. It follows the ground.
/// </summary>
public readonly record struct StreetSpec(string Name, float SpeedLimit, IReadOnlyList<Vector2> Line, int ForwardLanes = 1, int BackwardLanes = 1);

/// <summary>
/// Lays a set of streets in one go, as one step to undo: a junction wherever two of them cross or
/// one ends on another, and where one ends on a road already there (splitting that road there);
/// then each street as roads from junction to junction, with a point every so often along it on
/// the ground. An end on nothing is left loose.
/// </summary>
[Tool]
public partial class StreetBuild : RefCounted
{
    /// <summary>Ends and crossings nearer together than this are one junction, in metres.</summary>
    private const float Merge = 6f;

    /// <summary>How near a road already there a street's end must be to join it, in metres.</summary>
    private const float Reach = 12f;

    /// <summary>How near a road already there a street's end is put onto it, in metres.</summary>
    private const float SnapReach = 30f;

    /// <summary>How near a junction already there a street's end is put onto it, in metres.</summary>
    private const float JunctionReach = 15f;

    /// <summary>How far apart the points along a street are, at most, in metres.</summary>
    private const float PointSpacing = 50f;

    public RoadNetwork Network = null!;
    public Node Root = null!;
    public List<StreetSpec> Streets = new();

    // What it made, in order, for undoing and doing again.
    private readonly List<Node3D> _junctions = new();
    private readonly List<Node3D> _roads = new();
    private readonly List<RoadSplit> _splits = new();
    private bool _built;

    /// <summary>Lays the streets; the second time on (redoing), puts back the very same ones.</summary>
    public void Do()
    {
        if (!_built)
        {
            Build();
            _built = true;
            return;
        }

        foreach (var node in _junctions.Concat(_roads))
            Network.AddChild(node);
        foreach (var split in _splits)
            RoadEdits.Split(split);
    }

    /// <summary>Takes them away again, the roads they split put back whole.</summary>
    public void Undo()
    {
        foreach (var split in Enumerable.Reverse(_splits))
            RoadEdits.Unsplit(split);
        foreach (var node in Enumerable.Reverse(_roads).Concat(Enumerable.Reverse(_junctions)))
            Network.RemoveChild(node);
    }

    private void Build()
    {
        var existing = Network.Roads.ToList();
        var existingJunctions = Network.Junctions.ToList();
        // The roads already there as lines seen from above, every few metres along.
        var existingLines = existing.Where(r => r.Points.Count >= 2 && r.StartJunction is not RoadRamp && r.EndJunction is not RoadRamp)
            .Select(r => r.FullFrames().Where((_, i) => i % 4 == 0).Select(f => new Vector2(f.Position.X, f.Position.Z)).ToList())
            .Where(l => l.Count >= 2).ToList();

        // Each street's line, its ends put onto a junction already there, or the road already there, they are near.
        var lines = Streets.Select(street => street.Line.ToList()).ToList();
        foreach (var line in lines)
        {
            foreach (var end in new[] { 0, line.Count - 1 })
            {
                var junction = existingJunctions.Select(j => new Vector2(j.GlobalPosition.X, j.GlobalPosition.Z)).OrderBy(j => j.DistanceTo(line[end])).FirstOrDefault(j => j.DistanceTo(line[end]) < JunctionReach);
                if (junction != default)
                {
                    line[end] = junction;
                    continue;
                }

                var onRoad = existingLines.Select(l => Nearest(l, line[end])).OrderBy(n => n.Distance).FirstOrDefault();
                if (onRoad.Distance < SnapReach)
                    line[end] = onRoad.At;
            }
        }

        // Every place along each street it has to stop at: its ends, where others cross it, where
        // others end on it, and where it crosses a road already there.
        var stops = Streets.Select(_ => new List<(float Along, Vector2 At, bool End)>()).ToList();
        for (var s = 0; s < Streets.Count; s++)
        {
            var line = lines[s];
            stops[s].Add((0f, line[0], true));
            stops[s].Add((Length(line), line[^1], true));
            foreach (var road in existingLines)
            {
                foreach (var crossing in Crossings(line, road))
                    stops[s].Add((Along(line, crossing), crossing, false));
            }

            for (var o = 0; o < Streets.Count; o++)
            {
                if (o == s)
                    continue;
                var other = lines[o];
                foreach (var crossing in Crossings(line, other))
                    stops[s].Add((Along(line, crossing), crossing, false));
                foreach (var end in new[] { other[0], other[^1] })
                {
                    if (Nearest(line, end) is var (at, along, distance) && distance < Merge && along > Merge && along < Length(line) - Merge)
                        stops[s].Add((along, at, false));
                }
            }
        }

        // One junction for each place, where several streets share it; an end on no other street or road is loose.
        var places = new List<(Vector2 At, int Count)>();
        int PlaceOf(Vector2 at)
        {
            for (var i = 0; i < places.Count; i++)
            {
                if (places[i].At.DistanceTo(at) < Merge)
                {
                    places[i] = (places[i].At, places[i].Count + 1);
                    return i;
                }
            }

            places.Add((at, 1));
            return places.Count - 1;
        }

        var stopPlaces = stops.Select(list => list.Select(stop => (stop.Along, Place: PlaceOf(stop.At), stop.End)).OrderBy(stop => stop.Along).ToList()).ToList();
        var junctions = new Dictionary<int, RoadJunction>();
        RoadJunction? JunctionAt(int place)
        {
            if (junctions.TryGetValue(place, out var had))
                return had;
            var at = places[place].At;
            RoadJunction junction;
            // A junction already there.
            if (existingJunctions.FirstOrDefault(j => new Vector2(j.GlobalPosition.X, j.GlobalPosition.Z).DistanceTo(at) < 1f) is { } there)
                return junctions[place] = there;

            // On a road already there: split it there, at its own height.
            if (NearestRoad(at) is var (road, distance, frame) && frame.Position.ToGodot().DistanceTo(new Vector3(at.X, frame.Position.Y, at.Y)) < Reach)
            {
                junction = NewJunction(frame.Position.ToGodot());
                var split = new RoadSplit { Road = road, Junction = junction, Distance = distance };
                RoadEdits.Split(split);
                _splits.Add(split);
            }
            else if (places[place].Count > 1)
            {
                junction = NewJunction(new Vector3(at.X, Ground(at), at.Y));
            }
            else
            {
                return null;
            }

            return junctions[place] = junction;
        }

        // The roads already there (including the pieces splits make of them) nearest a place.
        (Road Road, float Distance, RoadFrame Frame)? NearestRoad(Vector2 at)
        {
            (Road, float, RoadFrame)? best = null;
            var bestDistance = float.MaxValue;
            foreach (var road in Network.Roads.Where(r => !_roads.Contains(r) && (existing.Contains(r) || _splits.Any(s => s.Second == r))))
            {
                foreach (var frame in road.FullFrames())
                {
                    var d = new Vector2(frame.Position.X, frame.Position.Z).DistanceTo(at);
                    if (d < bestDistance)
                        (best, bestDistance) = ((road, frame.Distance, frame), d);
                }
            }

            return best;
        }

        // Each street: a road from each junction (or loose end) along it to the next.
        for (var s = 0; s < Streets.Count; s++)
        {
            var street = Streets[s];
            var line = lines[s];
            var along = stopPlaces[s];
            for (var i = 0; i + 1 < along.Count; i++)
            {
                var (from, to) = (along[i], along[i + 1]);
                if (to.Along - from.Along < 1f)
                    continue;
                var (start, end) = (JunctionAt(from.Place), JunctionAt(to.Place));
                var road = new Road
                {
                    Name = street.Name.Replace(" ", ""),
                    StreetName = street.Name,
                    SpeedLimit = street.SpeedLimit,
                    ForwardLanes = street.ForwardLanes,
                    BackwardLanes = street.BackwardLanes,
                };
                Network.AddChild(road, true);
                road.Owner = Root;
                _roads.Add(road);

                var count = System.Math.Max(1, (int)System.MathF.Ceiling((to.Along - from.Along) / PointSpacing));
                for (var k = 0; k <= count; k++)
                {
                    var at = PointAt(line, from.Along + (to.Along - from.Along) * k / count);
                    var position = k == 0 && start is not null ? start.GlobalPosition
                        : k == count && end is not null ? end.GlobalPosition
                        : new Vector3(at.X, Ground(at), at.Y);
                    var point = new RoadPoint { Name = "RoadPoint" };
                    road.AddChild(point, true);
                    point.Owner = Root;
                    point.GlobalPosition = position;
                }

                road.StartJunction = start;
                road.EndJunction = end;
            }
        }

        // Traffic lights only where two faster streets cross; elsewhere signs, the faster road first.
        foreach (var junction in _junctions.OfType<RoadJunction>())
        {
            // By street: one running straight through is two arms, but one street.
            var fast = junction.Arms().Select(a => a.Road).Where(r => r.EffectiveSpeedLimit >= 50f).Select(r => r.StreetName).Distinct().Count();
            if (!_splits.Any(s => s.Junction == junction) && fast < 2)
                junction.Control = JunctionControl.Signs;
        }
    }

    private RoadJunction NewJunction(Vector3 at)
    {
        var junction = new RoadJunction { Name = "Junction" };
        Network.AddChild(junction, true);
        junction.Owner = Root;
        junction.GlobalPosition = at;
        _junctions.Add(junction);
        return junction;
    }

    private float Ground(Vector2 at)
    {
        if (Network.Ground is not { } ground)
            return 0f;
        var height = ground.HeightAt(new Vector3(at.X, 0f, at.Y));
        return float.IsNaN(height) ? 0f : height;
    }

    private static float Length(IReadOnlyList<Vector2> line) => line.Zip(line.Skip(1), (a, b) => a.DistanceTo(b)).Sum();

    /// <summary>The point this far along a line.</summary>
    private static Vector2 PointAt(IReadOnlyList<Vector2> line, float along)
    {
        for (var i = 0; i + 1 < line.Count; i++)
        {
            var piece = line[i].DistanceTo(line[i + 1]);
            if (along <= piece || i + 2 == line.Count)
                return line[i].Lerp(line[i + 1], Mathf.Clamp(along / Mathf.Max(piece, 1e-4f), 0f, 1f));
            along -= piece;
        }

        return line[^1];
    }

    /// <summary>How far along a line a point on it is.</summary>
    private static float Along(IReadOnlyList<Vector2> line, Vector2 at) => Nearest(line, at).Along;

    /// <summary>The nearest point of a line to another, how far along the line it is, and how far off.</summary>
    private static (Vector2 At, float Along, float Distance) Nearest(IReadOnlyList<Vector2> line, Vector2 point)
    {
        var best = (line[0], 0f, float.MaxValue);
        var along = 0f;
        for (var i = 0; i + 1 < line.Count; i++)
        {
            var (a, b) = (line[i], line[i + 1]);
            var piece = a.DistanceTo(b);
            var t = piece > 1e-4f ? Mathf.Clamp((point - a).Dot(b - a) / (piece * piece), 0f, 1f) : 0f;
            var at = a.Lerp(b, t);
            if (at.DistanceTo(point) < best.Item3)
                best = (at, along + piece * t, at.DistanceTo(point));
            along += piece;
        }

        return best;
    }

    /// <summary>Where two lines cross, away from both their ends.</summary>
    private static IEnumerable<Vector2> Crossings(IReadOnlyList<Vector2> a, IReadOnlyList<Vector2> b)
    {
        for (var i = 0; i + 1 < a.Count; i++)
        {
            for (var j = 0; j + 1 < b.Count; j++)
            {
                var hit = Geometry2D.SegmentIntersectsSegment(a[i], a[i + 1], b[j], b[j + 1]);
                if (hit.VariantType != Variant.Type.Vector2)
                    continue;
                var at = hit.AsVector2();
                if (at.DistanceTo(a[0]) > Merge && at.DistanceTo(a[^1]) > Merge && at.DistanceTo(b[0]) > Merge && at.DistanceTo(b[^1]) > Merge)
                    yield return at;
            }
        }
    }
}
#endif
