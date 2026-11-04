using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// A mesh as plain arrays, for the engine to turn into its own: triangles as index triples,
/// wound clockwise seen from the side they face (as Godot draws them).
/// </summary>
public sealed class MeshData
{
    public List<Vector3> Vertices { get; } = new();
    public List<Vector3> Normals { get; } = new();
    public List<Vector2> UVs { get; } = new();
    public List<int> Indices { get; } = new();

    /// <summary>Another's triangles added to these.</summary>
    public void Append(MeshData other)
    {
        var first = Vertices.Count;
        Vertices.AddRange(other.Vertices);
        Normals.AddRange(other.Normals);
        UVs.AddRange(other.UVs);
        Indices.AddRange(other.Indices.Select(i => i + first));
    }

    public int Add(Vector3 at, Vector3 normal, Vector2 uv)
    {
        Vertices.Add(at);
        Normals.Add(normal);
        UVs.Add(uv);
        return Vertices.Count - 1;
    }

    /// <summary>A quad a-b-c-d, going round clockwise seen from the side it faces.</summary>
    public void Quad(int a, int b, int c, int d)
    {
        Indices.AddRange(new[] { a, b, c, a, c, d });
    }
}

/// <summary>
/// A stretch of a road's sidewalk left open (a driveway into a parking lot): on which side, and from
/// and to how far along. It is paved at the road's height instead, with a crosswalk across it.
/// </summary>
public readonly record struct SidewalkOpening(bool Left, float From, float To);

/// <summary>A road's meshes: its surface, its lines in each colour, the banks sloping away past its shoulders, and its sidewalks.</summary>
public sealed record RoadMeshes(MeshData Surface, MeshData WhiteLines, MeshData YellowLines, MeshData Banks, MeshData Sidewalks);

/// <summary>
/// Builds a road's meshes along its frames: the surface of each band but a grass median, with a
/// skirt down from its outer edges so no gap shows where the ground falls away; and its lines,
/// slightly above the surface, dashed where they divide lanes going the same way. Ramps built into
/// the road give up the shoulder on their side where they run alongside, with a broken line
/// between, and have their own surface and lines where they peel away. Past the outer shoulders,
/// banks slope away as the cross-section says (see <see cref="RoadCrossSection.ShoulderHeight"/>).
/// </summary>
public static class RoadMeshBuilder
{
    /// <summary>How far the lines stand above the surface, so they draw over it.</summary>
    public const float LineLift = 0.015f;

    public const float LineWidth = 0.15f;
    public const float DoubleLineGap = 0.12f;
    public const float DashLength = 3f;
    public const float DashGap = 6f;

    /// <summary>The broken line between a road's lane and a ramp's alongside it: wider, and closer set.</summary>
    public const float RampLineWidth = 0.3f;
    public const float RampDash = 3f;
    public const float RampDashGap = 3f;

    /// <summary>How far in from where a ramp's lane joins the road its broken line starts.</summary>
    public const float RampLineDelay = 12f;

    /// <summary>How far a ramp has to have come away from its road before each has its own edge line.</summary>
    public const float Apart = 0.3f;

    /// <summary>How far the road's edges run down into the ground.</summary>
    public const float SkirtDepth = 0.6f;

    /// <summary>
    /// How far a sidewalk's far side (away from the road) and its ends run down into the ground: far
    /// enough that ground lowered a good way beside it never shows the gap under it.
    /// </summary>
    public const float SidewalkSkirtDepth = 10f;

    /// <summary>The asphalt texture repeats every this many metres, along the road and across it.</summary>
    public const float TextureSize = 4f;

    /// <summary>
    /// A road's meshes along its frames, with its ramps built in. Where structures carry it (a
    /// viaduct or tunnel), it has no skirts or banks: they build their own sides. Where it has
    /// parking (see RoadParking), its side is paved out as far as the bays reach, its sidewalk (or
    /// bank) set back round them, and the lines between them painted.
    /// </summary>
    public static RoadMeshes Build(IReadOnlyList<RoadFrame> frames, RoadCrossSection section, bool closed, IReadOnlyList<RampSpec>? ramps = null,
        IReadOnlyList<StructureSpec>? structures = null, bool roundStart = false, bool roundEnd = false, IReadOnlyList<SidewalkOpening>? openings = null,
        IReadOnlyList<ParkingSpec>? parking = null)
    {
        openings ??= Array.Empty<SidewalkOpening>();
        parking ??= Array.Empty<ParkingSpec>();
        var surface = new MeshData();
        var white = new MeshData();
        var yellow = new MeshData();
        var banks = new MeshData();
        var sidewalks = new MeshData();
        if (frames.Count < 2)
            return new RoadMeshes(surface, white, yellow, banks, sidewalks);
        ramps ??= Array.Empty<RampSpec>();
        // A ring has no ends to close or round off, though it is built below as an open road.
        if (closed)
            roundStart = roundEnd = false;
        var ring = closed;
        // A ring closes with its first frame again, at the full distance round: built as an open
        // road from there, its last stretch carries on its texture and dashes like any other,
        // rather than squashing the whole way round back to the start into it.
        if (closed)
        {
            var round = frames[^1].Distance + Vector3.Distance(frames[^1].Position, frames[0].Position);
            frames = frames.Append(frames[0] with { Distance = round }).ToList();
            closed = false;
        }

        // Frames where ramps start and end, once a ring's closing stretch is there too, so one
        // ending right at a ring's start still has its road step in cleanly to meet it.
        frames = WithBreaks(frames, ramps);
        frames = WithParkingBreaks(frames, parking, MathF.Max(section.SidewalkLeft, section.SidewalkRight));

        // How far out the far side of its sidewalk is set there for its parking, for a sidewalk so wide.
        float Widened(RoadFrame frame, RoadSide side, float sidewalk)
        {
            var most = 0f;
            foreach (var spot in parking)
            {
                if (spot.Side == side)
                    most = MathF.Max(most, spot.SidewalkDepthAt(frame.Distance, sidewalk));
            }

            return most;
        }

        // How far out its side is set at a frame for its parking there, if any.
        float Parked(RoadFrame frame, RoadSide side)
        {
            var most = 0f;
            foreach (var spot in parking)
            {
                if (spot.Side == side)
                    most = MathF.Max(most, spot.DepthAt(frame.Distance));
            }

            return most;
        }

        // A ramp at this distance on this side, if any.
        RampSpec? RampAt(float distance, RoadSide side)
        {
            foreach (var ramp in ramps)
            {
                if (ramp.Side == side && distance >= ramp.Start && distance <= ramp.End)
                    return ramp;
            }

            return null;
        }

        // The road's outer edge on each side: its shoulder's edge, or, where a ramp runs
        // alongside, no further than the ramp's own inner edge.
        var runs = PavedRuns(section).ToList();
        float Outer(RoadFrame frame, RoadSide side, float normal)
        {
            if (RampAt(frame.Distance, side) is not { } ramp)
                return normal + (side == RoadSide.Right ? 1f : -1f) * Parked(frame, side);
            var (_, _, edgeIn, _) = RoadRampShaper.Across(ramp, section, frame.Distance);
            return side == RoadSide.Right ? MathF.Min(normal, edgeIn) : MathF.Max(normal, edgeIn);
        }

        // Where it lies on the ground, for its skirts and banks: all of it, or the runs between its structures.
        structures ??= Array.Empty<StructureSpec>();
        var onGround = structures.Count == 0
            ? new[] { (frames, closed) }
            : RoadStructureBuilder.OnGround(frames, structures).Select(run => (run, false)).ToArray();

        for (var r = 0; r < runs.Count; r++)
        {
            var (left, right) = runs[r];
            var first = r == 0;
            var last = r == runs.Count - 1;
            Func<RoadFrame, float> leftEdge = first ? f => Outer(f, RoadSide.Left, left) : _ => left;
            Func<RoadFrame, float> rightEdge = last ? f => Outer(f, RoadSide.Right, right) : _ => right;
            Strip(surface, frames, closed, leftEdge, rightEdge, 0f, TextureSize);
            foreach (var (ground, looped) in onGround)
            {
                Skirt(surface, ground, looped, leftEdge, true);
                Skirt(surface, ground, looped, rightEdge, false);
                // Its ends closed where the road itself ends (not where a viaduct or tunnel takes over):
                // straight down into the ground across it, facing along the road, as its skirts do at its sides.
                if (!looped && !ring && ground.Count >= 2)
                {
                    foreach (var (frame, along) in new[] { (ground[0], -1f), (ground[^1], 1f) })
                    {
                        if (frame.Distance > frames[0].Distance + 1e-3f && frame.Distance < frames[^1].Distance - 1e-3f)
                            continue;
                        var (a, b) = (frame.Across(leftEdge(frame)), frame.Across(rightEdge(frame)));
                        var facing = new Vector3(frame.Forward.X, 0f, frame.Forward.Z);
                        Cap(surface, a, b, b - Vector3.UnitY * SkirtDepth, a - Vector3.UnitY * SkirtDepth, Vector3.Normalize(facing) * along);
                    }
                }

                // Past a sidewalk, if there is one.
                // None past a sidewalk: the ground meets it there.
                if (first && section.SidewalkLeft <= 0f)
                    Bank(banks, ground, looped, leftEdge, true, section.ShoulderHeight, section.BankWidth);
                if (last && section.SidewalkRight <= 0f)
                    Bank(banks, ground, looped, rightEdge, false, section.ShoulderHeight, section.BankWidth);
            }
        }

        // A loose end on the ground rounded off past its last point, rather than cut square.
        if (!closed && frames.Count >= 2 && runs.Count > 0)
        {
            var (left, right) = (runs[0].Left, runs[^1].Right);
            foreach (var (frame, along, round) in new[] { (frames[0], -1f, roundStart), (frames[^1], 1f, roundEnd) })
            {
                if (!round || structures.Any(s => s.Covers(frame.Distance)))
                    continue;
                RoundEnd(surface, banks, sidewalks, frame, along, left, right, section.ShoulderHeight, section.BankWidth,
                    section.SidewalkRight, section.SidewalkLeft);
                // The edge lines carry on round it from one side to the other.
                var edges = section.Markings().Where(m => m.Kind == MarkingKind.EdgeLine).Select(m => m.Offset).ToList();
                if (edges.Count >= 2)
                {
                    var middle = (left + right) / 2f;
                    var inset = ((right - edges.Max()) + (edges.Min() - left)) / 2f;
                    RoundLine(white, frame, along, frame.Across(middle), (right - left) / 2f - inset);
                }
            }
        }

        var markings = section.Markings();
        for (var m = 0; m < markings.Count; m++)
        {
            var marking = markings[m];
            var offset = marking.Offset;
            switch (marking.Kind)
            {
                case MarkingKind.EdgeLine:
                    // The outer edge lines give way, where a ramp runs alongside, to the ramp's broken line.
                    var side = m == 0 ? RoadSide.Left : m == markings.Count - 1 ? RoadSide.Right : (RoadSide?)null;
                    if (side is { } s && ramps.Any(r => r.Side == s))
                    {
                        // Where a ramp is joined to the road, the road's edge line stops: the ramp's outer line
                        // carries on from it. The broken line between them starts a little way in, once
                        // there is a lane to divide.
                        bool Joined(RoadFrame f) => RampAt(f.Distance, s) is { } ramp
                            && RoadRampShaper.Profile(ramp, section, f.Distance).Gap < Apart;
                        bool Alongside(RoadFrame f) => Joined(f) && RampAt(f.Distance, s) is { } ramp
                            && RoadRampShaper.Profile(ramp, section, f.Distance).Width > 0.05f
                            && FromJoin(ramp, section, f.Distance) >= RampLineDelay;
                        LineWhere(white, frames, closed, _ => offset, f => !Joined(f), LineWidth, dashed: false);
                        // Its dashes counted from where it starts, so it starts with a dash there.
                        float DashAlong(RoadFrame f) => RampAt(f.Distance, s) is { } ramp ? FromJoin(ramp, section, f.Distance) - RampLineDelay : f.Distance;
                        LineWhere(white, frames, closed, _ => offset, Alongside, RampLineWidth, dashed: true, RampDash, RampDashGap, DashAlong);
                    }
                    else
                    {
                        Line(white, frames, closed, offset, dashed: false);
                    }

                    break;
                case MarkingKind.LaneLine:
                    Line(white, frames, closed, offset, dashed: true);
                    break;
                case MarkingKind.MedianLine:
                    Line(yellow, frames, closed, offset, dashed: false);
                    break;
                case MarkingKind.CentreLine:
                    var half = (DoubleLineGap + LineWidth) / 2f;
                    Line(yellow, frames, closed, offset - half, dashed: false);
                    Line(yellow, frames, closed, offset + half, dashed: false);
                    break;
            }
        }

        foreach (var ramp in ramps)
            Ramp(surface, white, banks, frames, section, ramp);

        // Its sidewalks, all along it (over viaducts and through tunnels too), but where a ramp runs alongside.
        // Their far sides run deep into the ground only on the ground: not hanging under a viaduct's deck.
        bool Grounded(RoadFrame f) => !structures.Any(s => s.Covers(f.Distance));
        // On a viaduct, only just into its barrier (2 cm, so no face of either lies on the other's):
        // not under all of it, where its far side would share the barrier's outer face.
        float Inset(RoadFrame f) => structures.Where(s => s.Kind == StructureKind.Viaduct && s.Covers(f.Distance)).Select(s => s.BarrierWidth - 0.02f).DefaultIfEmpty(0f).Max();
        if (runs.Count > 0)
        {
            // Open (not built) strictly inside each opening: its stretches stop at the frames either side.
            bool Open(RoadFrame f, bool left) => openings.Any(o => o.Left == left && f.Distance > o.From && f.Distance < o.To);
            // Set back round any parking along it.
            // (Its far side set back further, past the parking's ends, for it to turn in along their sides.)
            if (section.SidewalkLeft > 0f)
                Sidewalk(sidewalks, frames, closed, f => runs[0].Left - Parked(f, RoadSide.Left),
                    f => section.SidewalkLeft + Widened(f, RoadSide.Left, section.SidewalkLeft) - Parked(f, RoadSide.Left), true,
                    f => RampAt(f.Distance, RoadSide.Left) is null && !Open(f, true), Grounded, Inset);
            if (section.SidewalkRight > 0f)
                Sidewalk(sidewalks, frames, closed, f => runs[^1].Right + Parked(f, RoadSide.Right),
                    f => section.SidewalkRight + Widened(f, RoadSide.Right, section.SidewalkRight) - Parked(f, RoadSide.Right), false,
                    f => RampAt(f.Distance, RoadSide.Right) is null && !Open(f, false), Grounded, Inset);
            foreach (var opening in openings)
            {
                var (edge, width) = opening.Left ? (runs[0].Left, section.SidewalkLeft) : (runs[^1].Right, section.SidewalkRight);
                if (width > 0f)
                    Driveway(surface, white, frames, opening, edge, opening.Left ? edge - width : edge + width);
            }
        }

        // The lines between its parking bays.
        if (runs.Count > 0)
        {
            foreach (var spot in parking)
                BayLines(white, frames, spot, spot.Side == RoadSide.Right ? runs[^1].Right : runs[0].Left);
        }

        return new RoadMeshes(surface, white, yellow, banks, sidewalks);
    }

    /// <summary>
    /// The painted lines between a road's parking bays: each from the road's edge out as far as the
    /// bays reach, square to the road, or leaning along it for angled bays.
    /// </summary>
    private static void BayLines(MeshData white, IReadOnlyList<RoadFrame> frames, ParkingSpec spot, float edge)
    {
        if (frames.Count < 2)
            return;
        var sign = spot.Side == RoadSide.Right ? 1f : -1f;
        var (along, outward) = spot.LineWay;
        var length = spot.Depth / MathF.Max(outward, 0.1f);
        foreach (var at in spot.Lines())
        {
            if (at < frames[0].Distance || at > frames[^1].Distance)
                continue;
            var frame = FrameAt(frames, at);
            var forward = Vector3.Normalize(new Vector3(frame.Forward.X, 0f, frame.Forward.Z));
            var outside = Vector3.Normalize(new Vector3(frame.Right.X, 0f, frame.Right.Z)) * sign;
            var way = forward * along + outside * outward;
            var start = frame.Across(edge) + Vector3.UnitY * LineLift;
            var end = start + way * length;
            var half = Vector3.Normalize(Vector3.Cross(way, Vector3.UnitY)) * (LineWidth / 2f);
            Triangle(white, start - half, start + half, end + half, Vector3.UnitY, v => new Vector2(v.X, v.Z));
            Triangle(white, start - half, end + half, end - half, Vector3.UnitY, v => new Vector2(v.X, v.Z));
        }
    }

    /// <summary>
    /// A road's parking strips alone, as tops a curb's height above where they are (as sidewalks' are),
    /// for the ground under them to be fitted from (see RoadGround.SidewalkTargets): each from the
    /// road's edge out as far as its parking sets that side back.
    /// </summary>
    public static MeshData ParkingTops(IReadOnlyList<RoadFrame> frames, RoadCrossSection section, IReadOnlyList<ParkingSpec> parking)
    {
        var tops = new MeshData();
        var runs = PavedRuns(section).ToList();
        if (parking.Count == 0 || runs.Count == 0 || frames.Count < 2)
            return tops;
        frames = WithParkingBreaks(frames, parking);
        foreach (var spot in parking)
        {
            var along = frames.Where(f => f.Distance >= spot.Start && f.Distance <= spot.End).ToList();
            if (along.Count < 2)
                continue;
            var right = spot.Side == RoadSide.Right;
            var edge = right ? runs[^1].Right : runs[0].Left;
            Func<RoadFrame, float> from = right ? _ => edge : f => edge - spot.DepthAt(f.Distance);
            Func<RoadFrame, float> to = right ? f => edge + spot.DepthAt(f.Distance) : _ => edge;
            Strip(tops, along, false, from, to, RoadCrossSection.CurbHeight, TextureSize);
        }

        return tops;
    }

    /// <summary>
    /// The road's frames with more where its parking comes out from its edge and goes back: along
    /// each end's taper, so the kerb bends out and back smoothly, and at its ends, so it starts square.
    /// </summary>
    private static IReadOnlyList<RoadFrame> WithParkingBreaks(IReadOnlyList<RoadFrame> frames, IReadOnlyList<ParkingSpec> parking, float sidewalk = 0f)
    {
        if (parking.Count == 0 || frames.Count < 2)
            return frames;
        var (first, last) = (frames[0].Distance, frames[^1].Distance);
        var breaks = new List<float>();
        foreach (var spot in parking)
        {
            var steps = Math.Max(1, (int)MathF.Ceiling(spot.Taper / 0.5f));
            for (var k = 0; k <= steps; k++)
            {
                breaks.Add(spot.Start + spot.Taper * k / steps);
                breaks.Add(spot.End - spot.Taper * k / steps);
                // And where the sidewalk beside it widens, past its ends.
                if (sidewalk > 0f && spot.Kind != ParkingKind.LayBy)
                {
                    breaks.Add(spot.Start - sidewalk + spot.Taper * k / steps);
                    breaks.Add(spot.End + sidewalk - spot.Taper * k / steps);
                }
            }
        }

        var added = breaks.Where(d => d > first && d < last && frames.All(f => MathF.Abs(f.Distance - d) > BreakGap / 2f))
            .Distinct().Select(d => RoadCurve.At(frames, d));
        return frames.Concat(added).OrderBy(f => f.Distance).ToList();
    }

    /// <summary>
    /// A sidewalk along the road from its edge (at edge metres right of the centre line) out width
    /// wide, on its left or right, where the test holds: its curb facing the road, its walkway on
    /// top, a skirt down from its far side into the ground, and its ends closed.
    /// </summary>
    private static void Sidewalk(MeshData mesh, IReadOnlyList<RoadFrame> frames, bool closed, Func<RoadFrame, float> edge, Func<RoadFrame, float> width, bool leftSide, Func<RoadFrame, bool> where,
        Func<RoadFrame, bool> grounded, Func<RoadFrame, float> inset)
    {
        float Depth(RoadFrame frame) => grounded(frame) ? SidewalkSkirtDepth : SkirtDepth;
        var sign = leftSide ? -1f : 1f;
        float Outer(RoadFrame frame) => edge(frame) + sign * (width(frame) - inset(frame));
        Func<RoadFrame, float> from = leftSide ? Outer : edge;
        Func<RoadFrame, float> to = leftSide ? edge : Outer;
        var up = Vector3.UnitY * RoadCrossSection.CurbHeight;
        var everywhere = frames.All(where);
        foreach (var run in Runs(frames, where))
        {
            var looped = closed && everywhere;
            Strip(mesh, run, looped, from, to, RoadCrossSection.CurbHeight, TextureSize);
            // The curb faces the road; the far side faces away from it.
            Skirt(mesh, run, looped, edge, !leftSide, -RoadCrossSection.CurbHeight);
            Skirt(mesh, run, looped, Outer, leftSide, -RoadCrossSection.CurbHeight, Depth);
            if (looped)
                continue;
            foreach (var (frame, along) in new[] { (run[0], -1f), (run[^1], 1f) })
            {
                var (a, b) = (frame.Across(edge(frame)) + up, frame.Across(Outer(frame)) + up);
                var facing = new Vector3(frame.Forward.X, 0f, frame.Forward.Z);
                var down = Vector3.UnitY * (Depth(frame) + RoadCrossSection.CurbHeight);
                Cap(mesh, a, b, b - down, a - down, Vector3.Normalize(facing) * along);
            }
        }
    }

    /// <summary>How wide a driveway's crosswalk stripes are, and the gaps between them.</summary>
    private const float CrosswalkStripe = 0.5f;

    /// <summary>
    /// Where a sidewalk is open: paved at the road's height from the frame before it to the frame
    /// after (where the sidewalk's stretches stop), from the road's edge to where the sidewalk's far
    /// side was; and striped across, square to the road, for people walking along the sidewalk.
    /// </summary>
    private static void Driveway(MeshData surface, MeshData white, IReadOnlyList<RoadFrame> frames, SidewalkOpening opening, float edge, float outer)
    {
        var first = frames.ToList().FindLastIndex(f => f.Distance <= opening.From);
        var last = frames.ToList().FindIndex(f => f.Distance >= opening.To);
        first = first < 0 ? 0 : first;
        last = last < 0 ? frames.Count - 1 : last;
        if (last - first < 1)
            return;
        var run = frames.Skip(first).Take(last - first + 1).ToList();
        var (from, to) = (MathF.Min(edge, outer), MathF.Max(edge, outer));
        Strip(surface, run, false, _ => from, _ => to, 0f, TextureSize);

        // The stripes, between where the sidewalk stops either side.
        var (start, end) = (run[0].Distance, run[^1].Distance);
        for (var at = start + CrosswalkStripe; at + CrosswalkStripe <= end; at += CrosswalkStripe * 2f)
        {
            var (a, b) = (FrameAt(run, at), FrameAt(run, at + CrosswalkStripe));
            var lift = Vector3.UnitY * LineLift;
            var (p, q, r, s) = (a.Across(from) + lift, a.Across(to) + lift, b.Across(to) + lift, b.Across(from) + lift);
            Triangle(white, p, q, r, Vector3.UnitY, v => new Vector2(v.X, v.Z));
            Triangle(white, p, r, s, Vector3.UnitY, v => new Vector2(v.X, v.Z));
        }
    }

    /// <summary>Where a road is at a distance along it, between the frames either side.</summary>
    private static RoadFrame FrameAt(IReadOnlyList<RoadFrame> frames, float distance)
    {
        for (var i = 1; i < frames.Count; i++)
        {
            if (frames[i].Distance < distance)
                continue;
            var (a, b) = (frames[i - 1], frames[i]);
            var s = b.Distance - a.Distance < 1e-6f ? 0f : (distance - a.Distance) / (b.Distance - a.Distance);
            return new RoadFrame(Vector3.Lerp(a.Position, b.Position, s), Vector3.Normalize(Vector3.Lerp(a.Forward, b.Forward, s)),
                Vector3.Normalize(Vector3.Lerp(a.Right, b.Right, s)), distance);
        }

        return frames[^1];
    }

    /// <summary>The runs of two or more frames in a row where the test holds.</summary>
    private static IEnumerable<IReadOnlyList<RoadFrame>> Runs(IReadOnlyList<RoadFrame> frames, Func<RoadFrame, bool> where)
    {
        var run = new List<RoadFrame>();
        foreach (var frame in frames)
        {
            if (where(frame))
            {
                run.Add(frame);
                continue;
            }

            if (run.Count > 1)
                yield return run;
            run = new List<RoadFrame>();
        }

        if (run.Count > 1)
            yield return run;
    }

    /// <summary>How far along a ramp a distance along the road is, from the end where its lane joins the road.</summary>
    private static float FromJoin(RampSpec ramp, RoadCrossSection section, float distance)
    {
        var along = Math.Clamp(distance - ramp.Start, 0f, ramp.Length);
        return RoadRampShaper.SeparatedAtStart(ramp, section) ? ramp.Length - along : along;
    }

    /// <summary>How far before a ramp's start (or past its end) the road has a frame of its own, so its edge steps in cleanly there.</summary>
    private const float BreakGap = 0.001f;

    /// <summary>
    /// The road's frames with more where each ramp starts and ends: one there and one just outside,
    /// so the road's edge steps in to the ramp's straight across, where the ramp's surface begins,
    /// instead of slanting in over the frame spacing and leaving a notch.
    /// </summary>
    private static IReadOnlyList<RoadFrame> WithBreaks(IReadOnlyList<RoadFrame> frames, IReadOnlyList<RampSpec> ramps)
    {
        if (ramps.Count == 0)
            return frames;
        var first = frames[0].Distance;
        var last = frames[^1].Distance;
        var breaks = ramps.SelectMany(r => new[] { r.Start - BreakGap, r.Start, r.End, r.End + BreakGap, r.Start + RampLineDelay, r.End - RampLineDelay })
            .Where(d => d > first && d < last && frames.All(f => MathF.Abs(f.Distance - d) > BreakGap / 2f))
            .Distinct()
            .Select(d => RoadCurve.At(frames, d));
        return frames.Concat(breaks).OrderBy(f => f.Distance).ToList();
    }

    /// <summary>A ramp's own surface over its stretch, with its skirts, banks and lines.</summary>
    private static void Ramp(MeshData surface, MeshData white, MeshData banks, IReadOnlyList<RoadFrame> frames, RoadCrossSection section, RampSpec ramp)
    {
        // Its frames turned to run along its lanes where they bend away, so it keeps its width there.
        var stretch = RoadCurve.Trim(frames, ramp.Start, ramp.End).Select(f => RoadRampShaper.Turned(f, ramp, section)).ToList();
        if (stretch.Count < 2)
            return;
        var right = ramp.Side == RoadSide.Right;
        (float LaneIn, float LaneOut, float EdgeIn, float EdgeOut) At(RoadFrame f) => RoadRampShaper.Across(ramp, section, f.Distance);

        // Left to right across it, whichever side it is on.
        Func<RoadFrame, float> leftEdge = right ? f => At(f).EdgeIn : f => At(f).EdgeOut;
        Func<RoadFrame, float> rightEdge = right ? f => At(f).EdgeOut : f => At(f).EdgeIn;
        Strip(surface, stretch, false, leftEdge, rightEdge, 0f, TextureSize);
        Skirt(surface, stretch, false, leftEdge, true);
        Skirt(surface, stretch, false, rightEdge, false);
        var bankWidth = ramp.Section.BankWidth;
        Bank(banks, stretch, false, leftEdge, true, ramp.ShoulderHeight, bankWidth);
        Bank(banks, stretch, false, rightEdge, false, ramp.ShoulderHeight, bankWidth);

        bool Wide(RoadFrame f) => RoadRampShaper.Profile(ramp, section, f.Distance).Width > ramp.Width * 0.5f;
        bool Away(RoadFrame f) => RoadRampShaper.Profile(ramp, section, f.Distance).Gap >= Apart;
        // Its outer edge line all along, centred on its lane's edge as the road's edge line is: where
        // its lane starts from nothing, it carries straight on from the road's.
        LineWhere(white, stretch, false, f => At(f).LaneOut, _ => true, LineWidth, dashed: false);
        // Its inner edge line, where it has come away from the road: centred on its lane's edge, as the road's own is, so it carries on straight into its road's.
        LineWhere(white, stretch, false, f => At(f).LaneIn, Away, LineWidth, dashed: false);

        // Dashed lines between its own lanes, once they are wide enough to tell apart.
        for (var lane = 1; lane < ramp.Lanes; lane++)
        {
            var share = lane / (float)ramp.Lanes;
            LineWhere(white, stretch, false, f => At(f).LaneIn + (At(f).LaneOut - At(f).LaneIn) * share, Wide, LineWidth, dashed: true);
        }
    }

    /// <summary>The spans across the road with surface on them, left to right: all of it, or either side of a grass median.</summary>
    public static IEnumerable<(float Left, float Right)> PavedRuns(RoadCrossSection section)
    {
        float? start = null;
        float end = 0f;
        foreach (var band in section.Bands())
        {
            // Sidewalks are past the paved road, not part of it.
            if (band.Kind == BandKind.Sidewalk)
                continue;
            var paved = !(band.Kind == BandKind.Median && section.Median == MedianKind.Grass);
            if (paved)
            {
                start ??= band.Left;
                end = band.Right;
            }
            else if (start is { } s)
            {
                yield return (s, end);
                start = null;
            }
        }

        if (start is { } last)
            yield return (last, end);
    }

    /// <summary>A flat strip from left to right across the road, lift above its surface, all along it.</summary>
    private static void Strip(MeshData mesh, IReadOnlyList<RoadFrame> frames, bool closed, Func<RoadFrame, float> left, Func<RoadFrame, float> right, float lift, float textureSize)
    {
        var first = mesh.Vertices.Count;
        foreach (var frame in frames)
        {
            var v = frame.Distance / textureSize;
            var (l, r) = (left(frame), right(frame));
            mesh.Add(frame.Across(l) + Vector3.UnitY * lift, Vector3.UnitY, new Vector2(l / textureSize, v));
            mesh.Add(frame.Across(r) + Vector3.UnitY * lift, Vector3.UnitY, new Vector2(r / textureSize, v));
        }

        Stitch(mesh, first, frames.Count, closed);
    }

    /// <summary>A strip from the road's edge (or drop below it) straight down into the ground, facing out.</summary>
    private static void Skirt(MeshData mesh, IReadOnlyList<RoadFrame> frames, bool closed, Func<RoadFrame, float> offset, bool leftEdge, float drop = 0f,
        Func<RoadFrame, float>? depthAt = null)
    {
        var first = mesh.Vertices.Count;
        foreach (var frame in frames)
        {
            var outward = leftEdge ? -frame.Right : frame.Right;
            var v = frame.Distance / TextureSize;
            var top = frame.Across(offset(frame)) - Vector3.UnitY * drop;
            var depth = depthAt?.Invoke(frame) ?? SkirtDepth;
            // Pairs in the order the surface's are, so that stitching them the same way winds them
            // to face out: bottom then top on the left edge, top then bottom on the right.
            if (leftEdge)
            {
                mesh.Add(top - Vector3.UnitY * depth, outward, new Vector2(depth / TextureSize, v));
                mesh.Add(top, outward, new Vector2(0f, v));
            }
            else
            {
                mesh.Add(top, outward, new Vector2(0f, v));
                mesh.Add(top - Vector3.UnitY * depth, outward, new Vector2(depth / TextureSize, v));
            }
        }

        Stitch(mesh, first, frames.Count, closed);
    }

    /// <summary>
    /// A bank from the road's edge sloping away outwards, width wide and dropping height (rising, if
    /// negative), facing up and out; with a skirt down from its far edge into the ground.
    /// </summary>
    private static void Bank(MeshData mesh, IReadOnlyList<RoadFrame> frames, bool closed, Func<RoadFrame, float> edge, bool leftEdge, float height, float width)
    {
        if (width <= 0.01f)
            return;
        var sign = leftEdge ? -1f : 1f;
        var slope = MathF.Sqrt(width * width + height * height);
        var first = mesh.Vertices.Count;
        foreach (var frame in frames)
        {
            var outward = sign * frame.Right;
            var normal = Vector3.Normalize(Vector3.UnitY * width + outward * height);
            var v = frame.Distance / TextureSize;
            var top = frame.Across(edge(frame));
            var bottom = frame.Across(edge(frame) + sign * width) - Vector3.UnitY * height;
            var topVertex = (top, new Vector2(0f, v));
            var bottomVertex = (bottom, new Vector2(slope / TextureSize, v));
            // Left then right across the road, as the surface's pairs go, so they wind the same way.
            foreach (var (at, uv) in leftEdge ? new[] { bottomVertex, topVertex } : new[] { topVertex, bottomVertex })
                mesh.Add(at, normal, uv);
        }

        Stitch(mesh, first, frames.Count, closed);
        Skirt(mesh, frames, closed, f => edge(f) + sign * width, leftEdge, height);

        // Its ends closed: from the slope straight down below its foot, facing along the road.
        if (closed || frames.Count < 2)
            return;
        foreach (var (frame, along) in new[] { (frames[0], -1f), (frames[^1], 1f) })
        {
            var top = frame.Across(edge(frame));
            var bottom = frame.Across(edge(frame) + sign * width) - Vector3.UnitY * height;
            var low = MathF.Min(top.Y, bottom.Y) - SkirtDepth;
            var facing = new Vector3(frame.Forward.X, 0f, frame.Forward.Z);
            Cap(mesh, top, bottom, bottom with { Y = low }, top with { Y = low }, Vector3.Normalize(facing) * along);
        }
    }

    /// <summary>How many pieces a rounded road end's half circle is made of.</summary>
    private const int RoundSteps = 16;

    /// <summary>
    /// A rounded end: a half circle of road across its whole width, out past the end frame the
    /// way along says (-1 back past its start, 1 on past its end), with a skirt down round it into
    /// the ground, and its banks carried on round it.
    /// </summary>
    private static void RoundEnd(MeshData surface, MeshData banks, MeshData sidewalks, RoadFrame frame, float along, float left, float right, float bankHeight, float bankWidth,
        float walkRight = 0f, float walkLeft = 0f)
    {
        // With sidewalks, they carry on round it (from the right's width to the left's), and there is no bank past them.
        var walked = walkRight > 0f || walkLeft > 0f;
        if (walked)
            bankWidth = 0f;
        var radius = (right - left) / 2f;
        if (radius <= 0.01f)
            return;
        var centre = frame.Across((left + right) / 2f);
        var forward = Vector3.Normalize(new Vector3(frame.Forward.X, 0f, frame.Forward.Z)) * along;
        var ways = RoundEndWays(frame, along);
        Vector2 Uv(Vector3 p) => new Vector2(Vector3.Dot(p - frame.Position, frame.Right), frame.Distance + Vector3.Dot(p - frame.Position, forward) * along) / TextureSize;

        var down = Vector3.UnitY * SkirtDepth;
        for (var i = 0; i < RoundSteps; i++)
        {
            var (a, b) = (centre + ways[i] * radius, centre + ways[i + 1] * radius);
            Triangle(surface, centre, a, b, Vector3.UnitY, Uv);
            var outward = Vector3.Normalize(ways[i] + ways[i + 1]);
            Cap(surface, a, b, b - down, a - down, outward);
            if (walked)
            {
                // Its width round the end: the right side's at the start, the left's at the finish.
                float Width(int k) => MathF.Max(walkRight + (walkLeft - walkRight) * k / (float)RoundSteps, 0.01f);
                var up = Vector3.UnitY * RoadCrossSection.CurbHeight;
                var (oa, ob) = (centre + ways[i] * (radius + Width(i)), centre + ways[i + 1] * (radius + Width(i + 1)));
                Triangle(sidewalks, a + up, b + up, ob + up, Vector3.UnitY, Uv);
                Triangle(sidewalks, a + up, ob + up, oa + up, Vector3.UnitY, Uv);
                Cap(sidewalks, a + up, b + up, b - down, a - down, -outward);
                var deep = Vector3.UnitY * SidewalkSkirtDepth;
                Cap(sidewalks, oa + up, ob + up, ob - deep, oa - deep, outward);
            }

            if (bankWidth <= 0.01f)
                continue;
            var (c, d) = (centre + ways[i] * (radius + bankWidth) - Vector3.UnitY * bankHeight, centre + ways[i + 1] * (radius + bankWidth) - Vector3.UnitY * bankHeight);
            var slope = Vector3.Normalize(Vector3.UnitY * bankWidth + outward * bankHeight);
            Triangle(banks, a, b, d, slope, Uv);
            Triangle(banks, a, d, c, slope, Uv);
            Cap(banks, c, d, d - down, c - down, outward);
        }
    }

    /// <summary>A line round a rounded end, this far out from its middle, from the road's right round to its left, over the surface.</summary>
    private static void RoundLine(MeshData mesh, RoadFrame frame, float along, Vector3 centre, float radius)
    {
        if (radius <= LineWidth)
            return;
        var ways = RoundEndWays(frame, along);
        var lift = Vector3.UnitY * LineLift;
        Vector2 Uv(Vector3 p) => new Vector2(p.X, p.Z) / TextureSize;
        for (var i = 0; i < RoundSteps; i++)
        {
            var (a, b) = (ways[i], ways[i + 1]);
            var (innerA, outerA) = (centre + a * (radius - LineWidth / 2f) + lift, centre + a * (radius + LineWidth / 2f) + lift);
            var (innerB, outerB) = (centre + b * (radius - LineWidth / 2f) + lift, centre + b * (radius + LineWidth / 2f) + lift);
            Triangle(mesh, innerA, outerA, outerB, Vector3.UnitY, Uv);
            Triangle(mesh, innerA, outerB, innerB, Vector3.UnitY, Uv);
        }
    }

    /// <summary>The ways out from a rounded end's middle, level: from the road's right, round out past the end, to its left.</summary>
    private static List<Vector3> RoundEndWays(RoadFrame frame, float along)
    {
        var forward = Vector3.Normalize(new Vector3(frame.Forward.X, 0f, frame.Forward.Z)) * along;
        var across = new Vector3(frame.Right.X, 0f, frame.Right.Z);
        return Enumerable.Range(0, RoundSteps + 1)
            .Select(i => i * MathF.PI / RoundSteps)
            .Select(a => Vector3.Normalize(across * MathF.Cos(a) + forward * MathF.Sin(a)))
            .ToList();
    }

    /// <summary>
    /// A rounded end's middle and outline (its half circle, closed by the line across the road),
    /// for the ground under it; at the end frame, past it the way along says.
    /// </summary>
    public static (Vector3 Middle, IReadOnlyList<Vector3> Outline) RoundEndOutline(RoadFrame frame, float along, RoadCrossSection section)
    {
        var runs = PavedRuns(section).ToList();
        var (left, right) = runs.Count > 0 ? (runs[0].Left, runs[^1].Right) : (0f, 0f);
        var centre = frame.Across((left + right) / 2f);
        // Out round its sidewalks too, if it has them.
        var ways = RoundEndWays(frame, along);
        return (centre, ways.Select((w, k) => centre + w * ((right - left) / 2f
            + section.SidewalkRight + (section.SidewalkLeft - section.SidewalkRight) * k / (float)RoundSteps)).ToList());
    }

    /// <summary>A triangle, wound to face the way it is told, whichever way round its corners come.</summary>
    private static void Triangle(MeshData mesh, Vector3 a, Vector3 b, Vector3 c, Vector3 normal, Func<Vector3, Vector2> uv)
    {
        if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) > 0f)
            (b, c) = (c, b);
        var first = mesh.Vertices.Count;
        foreach (var p in new[] { a, b, c })
            mesh.Add(p, normal, uv(p));
        mesh.Indices.AddRange(new[] { first, first + 1, first + 2 });
    }

    /// <summary>A flat four-cornered face, wound to face the way it is told, whichever way round its corners come.</summary>
    private static void Cap(MeshData mesh, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
    {
        if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) > 0f)
            (b, d) = (d, b);
        var right = Vector3.Normalize(Vector3.Cross(normal, Vector3.UnitY));
        var first = mesh.Vertices.Count;
        foreach (var p in new[] { a, b, c, d })
            mesh.Add(p, normal, new Vector2(Vector3.Dot(p, right), p.Y) / TextureSize);
        mesh.Quad(first, first + 1, first + 2, first + 3);
    }

    /// <summary>A line centred offset metres right of the centre line: solid, or in dashes.</summary>
    private static void Line(MeshData mesh, IReadOnlyList<RoadFrame> frames, bool closed, float offset, bool dashed) =>
        LineWhere(mesh, frames, closed, _ => offset, _ => true, LineWidth, dashed);

    /// <summary>
    /// A line along the road, centred where offset says, width wide, only where the test holds:
    /// solid there, or in dashes of dash metres with gap between.
    /// </summary>
    private static void LineWhere(MeshData mesh, IReadOnlyList<RoadFrame> frames, bool closed, Func<RoadFrame, float> offset,
        Func<RoadFrame, bool> where, float width, bool dashed, float dash = DashLength, float gap = DashGap, Func<RoadFrame, float>? dashAlong = null)
    {
        dashAlong ??= f => f.Distance;
        Func<RoadFrame, float> left = f => offset(f) - width / 2f;
        Func<RoadFrame, float> right = f => offset(f) + width / 2f;
        var everywhere = frames.All(where);
        if (!dashed && everywhere)
        {
            Strip(mesh, frames, closed, left, right, LineLift, 1f);
            return;
        }

        // Runs of frames where the line is drawn: where the test holds, and within a dash.
        var run = new List<RoadFrame>();
        foreach (var frame in frames)
        {
            if (where(frame) && (!dashed || Math.Abs(dashAlong(frame)) % (dash + gap) < dash))
            {
                run.Add(frame);
                continue;
            }

            if (run.Count > 1)
                Strip(mesh, run, false, left, right, LineLift, 1f);
            run.Clear();
        }

        if (run.Count > 1)
            Strip(mesh, run, false, left, right, LineLift, 1f);
    }

    /// <summary>
    /// Joins count pairs of vertices from first into quads, each pair (left, right) seen from the
    /// side they face with the road running away: clockwise seen from that side.
    /// </summary>
    private static void Stitch(MeshData mesh, int first, int count, bool closed)
    {
        var quads = closed ? count : count - 1;
        for (var i = 0; i < quads; i++)
        {
            var a = first + 2 * i;
            var b = first + 2 * ((i + 1) % count);
            // Near left, far left, far right, near right: clockwise seen from above.
            mesh.Quad(a, b, b + 1, a + 1);
        }
    }
}
