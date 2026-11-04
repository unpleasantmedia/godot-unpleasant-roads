using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// A stretch of one lane traffic drives along, first point to last: along a road between the
/// places ramps leave or join it, through a junction from one road's lane to another's, or down a
/// ramp. Its speed limit is in km/h; its street name is empty where it has none.
/// </summary>
public sealed record TrafficLane(int Id, IReadOnlyList<Vector3> Points, float Length, float SpeedLimit, string StreetName, string Source)
{
    /// <summary>How long it takes to drive at its speed limit, in seconds.</summary>
    public float Time => Length / MathF.Max(SpeedLimit / 3.6f, 0.1f);
}

/// <summary>
/// All the lanes of a road network, and how they join: which lead on from which (a road's lane
/// into a junction's turn, a turn into the next road's lane), and which run side by side going
/// the same way, so traffic can change between them. Routes are found through it by time.
/// </summary>
public sealed class LaneGraph
{
    /// <summary>How long changing lanes counts for, in seconds, so routes do not weave for nothing.</summary>
    public const float LaneChangeTime = 3f;

    private readonly List<TrafficLane> _lanes = new();
    private readonly Dictionary<int, List<int>> _next = new();
    private readonly Dictionary<int, List<int>> _previous = new();
    private readonly Dictionary<int, (int? Left, int? Right)> _beside = new();
    private readonly Dictionary<int, List<(float From, float To)>> _noChange = new();

    public IReadOnlyList<TrafficLane> Lanes => _lanes;

    /// <summary>Adds a lane along these points; its id.</summary>
    public int Add(IReadOnlyList<Vector3> points, float speedLimit, string streetName, string source)
    {
        var length = 0f;
        for (var i = 1; i < points.Count; i++)
            length += Vector3.Distance(points[i - 1], points[i]);
        var id = _lanes.Count;
        _lanes.Add(new TrafficLane(id, points, length, speedLimit, streetName, source));
        return id;
    }

    /// <summary>Traffic at the end of one lane carries on into the other.</summary>
    public void Link(int from, int to)
    {
        if (!_next.TryGetValue(from, out var next))
            _next[from] = next = new List<int>();
        if (!next.Contains(to))
            next.Add(to);
        if (!_previous.TryGetValue(to, out var previous))
            _previous[to] = previous = new List<int>();
        if (!previous.Contains(from))
            previous.Add(from);
    }

    /// <summary>Two lanes run side by side going the same way, the one on the left of the other.</summary>
    public void Beside(int left, int right)
    {
        _beside[left] = (_beside.GetValueOrDefault(left).Left, right);
        _beside[right] = (left, _beside.GetValueOrDefault(right).Right);
    }

    /// <summary>The lanes traffic can carry on into from the end of this one.</summary>
    public IReadOnlyList<int> Next(int lane) => _next.TryGetValue(lane, out var next) ? next : Array.Empty<int>();

    /// <summary>The lanes that lead into this one.</summary>
    public IReadOnlyList<int> Previous(int lane) => _previous.TryGetValue(lane, out var previous) ? previous : Array.Empty<int>();

    /// <summary>The lanes beside this one going the same way, left and right, if any.</summary>
    public (int? Left, int? Right) BesideOf(int lane) => _beside.GetValueOrDefault(lane);

    /// <summary>Traffic may not change out of this lane between these distances along it (near a junction, say).</summary>
    public void ForbidChange(int lane, float from, float to)
    {
        if (!_noChange.TryGetValue(lane, out var spans))
            _noChange[lane] = spans = new List<(float, float)>();
        spans.Add((MathF.Min(from, to), MathF.Max(from, to)));
    }

    /// <summary>Whether traffic may change out of this lane this far along it.</summary>
    public bool CanChange(int lane, float along) =>
        !_noChange.TryGetValue(lane, out var spans) || !spans.Any(s => along >= s.From && along <= s.To);

    /// <summary>How much of a lane traffic may change out of it along (all of it, less where it may not).</summary>
    public float ChangeableLength(int lane)
    {
        var length = _lanes[lane].Length;
        if (!_noChange.TryGetValue(lane, out var spans))
            return length;
        // Walked in small steps: forbidden stretches may overlap.
        const int steps = 64;
        var allowed = Enumerable.Range(0, steps).Count(i => CanChange(lane, (i + 0.5f) * length / steps));
        return length * allowed / steps;
    }

    /// <summary>The place a distance along a lane.</summary>
    public Vector3 PointAt(int lane, float along)
    {
        var points = _lanes[lane].Points;
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var step = Vector3.Distance(points[i], points[i + 1]);
            if (along <= step || i + 2 == points.Count)
                return Vector3.Lerp(points[i], points[i + 1], step > 1e-6f ? Math.Clamp(along / step, 0f, 1f) : 0f);
            along -= step;
        }

        return points[^1];
    }

    /// <summary>How far along a lane the point on it nearest a place is.</summary>
    public float AlongOf(int lane, Vector3 at)
    {
        var points = _lanes[lane].Points;
        var (best, bestDistance, along) = (0f, float.MaxValue, 0f);
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (a, b) = (points[i], points[i + 1]);
            var ab = b - a;
            var t = ab.LengthSquared() > 1e-8f ? Math.Clamp(Vector3.Dot(at - a, ab) / ab.LengthSquared(), 0f, 1f) : 0f;
            var distance = Vector3.DistanceSquared(at, a + ab * t);
            if (distance < bestDistance)
                (bestDistance, best) = (distance, along + ab.Length() * t);
            along += ab.Length();
        }

        return best;
    }

    /// <summary>
    /// Across from a place on a lane, on the lane beside it on one side: which lane that is, how far
    /// along it, and where; none if there is no lane there, or changing is not allowed here.
    /// </summary>
    public (int Lane, float Along, Vector3 At)? Across(int lane, float along, bool toLeft)
    {
        var (left, right) = BesideOf(lane);
        if ((toLeft ? left : right) is not { } other || !CanChange(lane, along))
            return null;
        var otherAlong = AlongOf(other, PointAt(lane, along));
        return (other, otherAlong, PointAt(other, otherAlong));
    }

    /// <summary>The lane nearest a place, and how far along it that is; none with no lanes.</summary>
    public (int Lane, float Along)? Nearest(Vector3 at)
    {
        (int Lane, float Along)? best = null;
        var bestDistance = float.MaxValue;
        foreach (var lane in _lanes)
        {
            var along = 0f;
            for (var i = 0; i + 1 < lane.Points.Count; i++)
            {
                var (a, b) = (lane.Points[i], lane.Points[i + 1]);
                var ab = b - a;
                var t = ab.LengthSquared() > 1e-8f ? Math.Clamp(Vector3.Dot(at - a, ab) / ab.LengthSquared(), 0f, 1f) : 0f;
                var distance = Vector3.DistanceSquared(at, a + ab * t);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = (lane.Id, along + ab.Length() * t);
                }

                along += ab.Length();
            }
        }

        return best;
    }

    /// <summary>
    /// The lanes passing near a place: each lane's nearest point to it and how far along the lane
    /// that is, for every lane coming within reach of the nearest one's distance.
    /// </summary>
    public IReadOnlyList<(int Lane, float Along, float Distance)> NearestLanes(Vector3 at, float reach = 30f)
    {
        var all = _lanes.Select(l =>
        {
            var along = AlongOf(l.Id, at);
            return (l.Id, along, Vector3.Distance(PointAt(l.Id, along), at));
        }).ToList();
        if (all.Count == 0)
            return all;
        var nearest = all.Min(l => l.Item3);
        return all.Where(l => l.Item3 <= nearest + reach).ToList();
    }

    /// <summary>
    /// The quickest way from one lane to another, as the lanes driven through in order (changing
    /// lanes along the way where that is quicker); none if it cannot be reached.
    /// </summary>
    public IReadOnlyList<int>? Route(int from, int to)
    {
        if (from < 0 || from >= _lanes.Count || to < 0 || to >= _lanes.Count)
            return null;
        var fastest = _lanes.Max(l => l.SpeedLimit) / 3.6f;
        var goal = _lanes[to].Points[^1];
        float Guess(int lane) => Vector3.Distance(_lanes[lane].Points[^1], goal) / MathF.Max(fastest, 0.1f);

        var time = new Dictionary<int, float> { [from] = 0f };
        var came = new Dictionary<int, int>();
        var open = new PriorityQueue<int, float>();
        open.Enqueue(from, Guess(from));
        var done = new HashSet<int>();
        while (open.TryDequeue(out var lane, out _))
        {
            if (lane == to)
                break;
            if (!done.Add(lane))
                continue;
            var steps = Next(lane).Select(n => (n, _lanes[n].Time)).ToList();
            var (left, right) = BesideOf(lane);
            // Over into a lane beside it, where it may change somewhere along it.
            foreach (var side in new[] { left, right })
            {
                if (side is { } s && ChangeableLength(lane) > 0.5f)
                    steps.Add((s, LaneChangeTime));
            }

            foreach (var (next, cost) in steps)
            {
                var arrive = time[lane] + cost;
                if (time.TryGetValue(next, out var had) && had <= arrive)
                    continue;
                time[next] = arrive;
                came[next] = lane;
                open.Enqueue(next, arrive + Guess(next));
            }
        }

        if (!time.ContainsKey(to))
            return null;
        var route = new List<int> { to };
        while (route[^1] != from)
            route.Add(came[route[^1]]);
        route.Reverse();
        return route;
    }

    /// <summary>
    /// How far it is to drive from the start of one lane to the start of every lane it leads to,
    /// the shortest way (changing lanes counted as a short stretch); lanes it cannot reach are
    /// left out.
    /// </summary>
    public IReadOnlyDictionary<int, float> DistancesFrom(int from, float laneChange = 10f)
    {
        var distance = new Dictionary<int, float> { [from] = 0f };
        var open = new PriorityQueue<int, float>();
        open.Enqueue(from, 0f);
        var done = new HashSet<int>();
        while (open.TryDequeue(out var lane, out _))
        {
            if (!done.Add(lane))
                continue;
            var steps = Next(lane).Select(n => (n, _lanes[lane].Length)).ToList();
            var (left, right) = BesideOf(lane);
            foreach (var side in new[] { left, right })
            {
                if (side is { } s && ChangeableLength(lane) > 0.5f)
                    steps.Add((s, laneChange));
            }

            foreach (var (next, cost) in steps)
            {
                var reach = distance[lane] + cost;
                if (distance.TryGetValue(next, out var had) && had <= reach)
                    continue;
                distance[next] = reach;
                open.Enqueue(next, reach);
            }
        }

        return distance;
    }

    /// <summary>
    /// A lane's points cut into pieces at distances along it (as measured by <paramref name="along"/>,
    /// one per point, rising in the way it goes), each piece with the points either side and a point
    /// exactly at each cut.
    /// </summary>
    public static IReadOnlyList<(float From, float To, IReadOnlyList<Vector3> Points)> Cut(IReadOnlyList<Vector3> points, IReadOnlyList<float> along, IEnumerable<float> cuts)
    {
        var pieces = new List<(float, float, IReadOnlyList<Vector3>)>();
        if (points.Count < 2)
            return pieces;
        var (first, last) = (along[0], along[^1]);
        var bounds = cuts.Where(c => c > first + 0.01f && c < last - 0.01f).Distinct().OrderBy(c => c).Prepend(first).Append(last).ToList();

        Vector3 At(float distance)
        {
            for (var i = 0; i + 1 < points.Count; i++)
            {
                if (distance > along[i + 1] && i + 2 < points.Count)
                    continue;
                var span = along[i + 1] - along[i];
                var t = span > 1e-6f ? Math.Clamp((distance - along[i]) / span, 0f, 1f) : 0f;
                return Vector3.Lerp(points[i], points[i + 1], t);
            }

            return points[^1];
        }

        for (var k = 0; k + 1 < bounds.Count; k++)
        {
            var (from, to) = (bounds[k], bounds[k + 1]);
            var piece = new List<Vector3> { At(from) };
            for (var i = 0; i < points.Count; i++)
            {
                if (along[i] > from + 1e-3f && along[i] < to - 1e-3f)
                    piece.Add(points[i]);
            }

            piece.Add(At(to));
            pieces.Add((from, to, piece));
        }

        return pieces;
    }
}
