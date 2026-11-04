using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// Where the ground should be for a road on the terrain's grid: at each grid point near the road,
/// the height it wants (the road's surface there, less <see cref="RoadGround.Sink"/>) and how much
/// (1 under the road, its banks and a margin past them, easing to nothing over the falloff beyond),
/// and how far out past the paved surface it is.
/// </summary>
/// <remarks>
/// Off a road stretch's end, Outside is pushed far out for ranking (so it gives way); Past is then how
/// far out past the paving it really is.
/// </remarks>
public readonly record struct GroundTarget(float Height, float Weight, float Outside = 0f, float? Past = null)
{
    /// <summary>How far out past the paved surface it really is, whatever its ranking.</summary>
    public float Beyond => Past ?? Outside;

    /// <summary>
    /// Whether this wants a grid point more than that does: the one wanting it more, or where both
    /// want it fully, the one nearer its paving, so a ramp's lane is not dug into by the road's bank
    /// beside it, nor a junction's by its roads'. Where both are as near (both under their surfaces:
    /// a parking lot's drive across a road's sidewalk), the lower, so the ground is under both.
    /// </summary>
    public bool Beats(GroundTarget other) =>
        Weight > other.Weight || (Weight == other.Weight && (Outside < other.Outside || (Outside == other.Outside && Height < other.Height)));
}

public static class RoadGround
{
    /// <summary>How far under a road's surface the ground is put, so it never shows through.</summary>
    public const float Sink = 0.2f;

    /// <summary>How far above the road the ground under and beside a sidewalk is put: under its top, a curb's height up, by a little.</summary>
    public const float WalkGround = RoadCrossSection.CurbHeight - 0.1f;

    /// <summary>What a grid point off the end of the stretch it is measured against counts as being further out, so it gives way.</summary>
    private const float OffEnd = 1000f;

    /// <summary>
    /// The ground wanted under a road, on a grid spacing metres apart, keyed by (x, z) grid index:
    /// flat under the road, down across its banks (see <see cref="RoadCrossSection.ShoulderHeight"/>) as far as they
    /// are left uncovered by bankCover (0: all the way down them; 1: level, burying them),
    /// flat for margin past them, then easing back over falloff.
    /// Where it passes a grid point more than once (a tight bend), the fullest weight wins.
    /// </summary>
    public static Dictionary<(int X, int Z), GroundTarget> Targets(
        IReadOnlyList<RoadFrame> frames, RoadCrossSection section, float spacing, float margin = 1f, float falloff = 12f,
        float bankCover = 0f)
    {
        var targets = new Dictionary<(int X, int Z), GroundTarget>();
        if (frames.Count == 0)
            return targets;

        var bands = section.Bands();
        // Past a sidewalk, flat a grid step further before its bank: the ground between grid points
        // would otherwise rise (or fall) into the sidewalk from the next one out, up a cutting.
        var left = bands[0].Left - (section.SidewalkLeft > 0f ? spacing : 0f);
        var right = bands[^1].Right + (section.SidewalkRight > 0f ? spacing : 0f);
        var bank = section.BankWidth;
        var reach = bank + margin + falloff + spacing;

        // Every grid point near each stretch between two frames, measured against that stretch.
        // Past a stretch's ends it counts too (measured from the frame there), but for the road's
        // own two ends: on the outside of a bend the stretches fan apart, and the ground in the
        // wedges between them wants the road's height as much as the rest.
        var stretches = Math.Max(frames.Count - 1, 1);
        for (var i = 0; i < stretches; i++)
        {
            var a = frames[i];
            var b = frames[Math.Min(i + 1, frames.Count - 1)];
            var (openBefore, openAfter) = (i > 0, i < stretches - 1);
            var min = Vector3.Min(Vector3.Min(a.Across(left - reach), a.Across(right + reach)), Vector3.Min(b.Across(left - reach), b.Across(right + reach)));
            var max = Vector3.Max(Vector3.Max(a.Across(left - reach), a.Across(right + reach)), Vector3.Max(b.Across(left - reach), b.Across(right + reach)));
            for (var gx = (int)MathF.Floor(min.X / spacing); gx <= (int)MathF.Ceiling(max.X / spacing); gx++)
            for (var gz = (int)MathF.Floor(min.Z / spacing); gz <= (int)MathF.Ceiling(max.Z / spacing); gz++)
            {
                var point = new Vector3(gx * spacing, 0f, gz * spacing);
                if (!Across(a, b, point, openBefore, openAfter, out var offset, out var height, out var offEnd))
                    continue;

                // How far out past the road's edge, and past its bank.
                var outside = MathF.Max(left - offset, offset - right);
                // Down its bank, but only so far: the ground covers that share of it. A side with a
                // sidewalk has none: the ground meets the sidewalk.
                var walked = offset < left ? section.SidewalkLeft > 0f : offset > right && section.SidewalkRight > 0f;
                var sideBank = walked ? 0f : bank;
                var down = Down(section.ShoulderHeight, sideBank, Climb(section.ShoulderHeight, outside, spacing), bankCover);
                var beyond = outside - sideBank - margin;
                var weight = beyond <= 0f ? 1f : 1f - SmoothStep(beyond / falloff);
                if (weight <= 0f)
                    continue;

                // Off a stretch's end, it gives way to the stretch the point lies beside, wanting it just as much.
                // Just past a sidewalk, the ground comes up to just under its top, to meet it. Not under it: a
                // grid cell there reaches the asphalt, and the ground across it would rise through that.
                var pastWalk = outside <= 0f && (offset < bands[0].Left && section.SidewalkLeft > 0f || offset > bands[^1].Right && section.SidewalkRight > 0f);
                var sink = pastWalk ? -WalkGround : Sink;
                // Off a stretch's end, the nearer that end the more it counts: inside a tight bend a point
                // lies off the ends of many stretches, all under the road, and the first measured (far round
                // the bend, at another height) won, raising the ground through the road.
                var wanted = new GroundTarget(height - sink - down, weight, MathF.Max(outside, 0f) + (offEnd ? OffEnd + MathF.Abs(offset) : 0f),
                    MathF.Max(outside, 0f));
                if (!targets.TryGetValue((gx, gz), out var had) || wanted.Beats(had))
                    targets[(gx, gz)] = wanted;
            }
        }

        return targets;
    }

    /// <summary>
    /// For finding where the ground shows through: every stretch of a road that reaches one grid point,
    /// and what each makes of it (as <see cref="Targets"/> measures it), in words.
    /// </summary>
    public static List<string> Explain(IReadOnlyList<RoadFrame> frames, RoadCrossSection section, float spacing, int gx, int gz, float margin = 1f, float falloff = 12f)
    {
        var lines = new List<string>();
        if (frames.Count < 2)
            return lines;
        var bands = section.Bands();
        var left = bands[0].Left - (section.SidewalkLeft > 0f ? spacing : 0f);
        var right = bands[^1].Right + (section.SidewalkRight > 0f ? spacing : 0f);
        var reach = section.BankWidth + margin + falloff + spacing;
        var point = new Vector3(gx * spacing, 0f, gz * spacing);
        var stretches = frames.Count - 1;
        for (var i = 0; i < stretches; i++)
        {
            var (a, b) = (frames[i], frames[i + 1]);
            var min = Vector3.Min(Vector3.Min(a.Across(left - reach), a.Across(right + reach)), Vector3.Min(b.Across(left - reach), b.Across(right + reach)));
            var max = Vector3.Max(Vector3.Max(a.Across(left - reach), a.Across(right + reach)), Vector3.Max(b.Across(left - reach), b.Across(right + reach)));
            if (point.X < min.X - spacing || point.X > max.X + spacing || point.Z < min.Z - spacing || point.Z > max.Z + spacing)
                continue;
            if (!Across(a, b, point, i > 0, i < stretches - 1, out var offset, out var height, out var offEnd))
                continue;
            var outside = MathF.Max(left - offset, offset - right);
            lines.Add(System.FormattableString.Invariant(
                $"stretch {i} ({a.Distance:0.0}-{b.Distance:0.0} m, {Vector3.Distance(a.Position, b.Position):0.0} m long, heights {a.Position.Y:0.00}-{b.Position.Y:0.00}): {(offEnd ? "OFF END" : "on it")}, across {offset:0.0}, outside {outside:0.0}, height {height:0.00}"));
        }

        return lines;
    }

    /// <summary>
    /// The ground wanted under a flat shape (a junction), keyed as <see cref="Targets"/> keys it:
    /// flat at height less the sink inside the outline, down across a bank bankHeight deep and
    /// bankWidth wide round it as far as bankCover leaves it uncovered, flat for margin past
    /// that, then easing back over falloff.
    /// </summary>
    /// <remarks>
    /// With its middle given, the shape is a fan from there out to its outline, each point at its
    /// own height (a junction meeting its roads at theirs): the ground inside follows the fan, and
    /// outside, the outline's height nearest it.
    /// </remarks>
    public static Dictionary<(int X, int Z), GroundTarget> ShapeTargets(
        IReadOnlyList<Vector3> outline, float height, float spacing, float margin = 1f, float falloff = 12f,
        float bankHeight = 0f, float bankWidth = 0f, Vector3? middle = null, float bankCover = 0f, IReadOnlyList<Vector3>? surfaceOutline = null)
    {
        var targets = new Dictionary<(int X, int Z), GroundTarget>();
        if (outline.Count < 3)
            return targets;

        var reach = bankWidth + margin + falloff + spacing;
        var minX = outline.Min(p => p.X) - reach;
        var maxX = outline.Max(p => p.X) + reach;
        var minZ = outline.Min(p => p.Z) - reach;
        var maxZ = outline.Max(p => p.Z) + reach;
        for (var gx = (int)MathF.Floor(minX / spacing); gx <= (int)MathF.Ceiling(maxX / spacing); gx++)
        for (var gz = (int)MathF.Floor(minZ / spacing); gz <= (int)MathF.Ceiling(maxZ / spacing); gz++)
        {
            var at = new Vector2(gx * spacing, gz * spacing);
            var inside = Inside(outline, at);
            // How far outside its edge (inside, negative), and the edge's height nearest.
            var (distance, nearest, nearestAt) = Nearest(outline, at);
            // Where the outline nearest is a sidewalk's far edge (well away from the surface's own), there is no bank.
            var bankHere = surfaceOutline is not null && Nearest(surfaceOutline, nearestAt).Distance > 0.5f ? 0f : bankWidth;
            // With sidewalks (an outline for the surface inside), flat a grid step past them before the bank, as a road's.
            var edgeward = (inside ? -distance : distance) - (surfaceOutline is null ? 0f : spacing);
            var outside = MathF.Max(edgeward, 0f);
            // Inside, the ground follows the surface its fan makes: over the outline given for it, if one is (a junction's asphalt, inside its sidewalks).
            // and between that and the outline (under its sidewalks), the surface's edge height nearest, so it stays under them and never rises over the surface's edge.
            // With sidewalks, the lowest the surface is a grid step round the point: the ground between grid
            // points is drawn straight between them, and a steep, folded surface would have it bulge through.
            var surface = middle is not { } m ? height
                : !inside ? nearest
                : surfaceOutline is null ? FanHeight(m, outline, at) ?? m.Y
                : LowestRound(m, surfaceOutline, at, spacing) ?? Nearest(surfaceOutline, at).Height;
            var down = Down(bankHeight, bankHere, Climb(bankHeight, edgeward, spacing), bankCover);
            var beyond = outside - bankHere - margin;
            var weight = beyond <= 0f ? 1f : 1f - SmoothStep(beyond / falloff);
            if (weight > 0f)
            {
                // Just past its sidewalks, the ground comes up to just under their tops, to meet them (where the
                // outline nearest is a sidewalk's far edge, not the surface's own). Not under them, as for a road's.
                var pastWalk = surfaceOutline is not null && !inside && outside <= 0f && Nearest(surfaceOutline, nearestAt).Distance > 0.5f;
                targets[(gx, gz)] = new GroundTarget(surface - (pastWalk ? -WalkGround : Sink) - down, weight, outside);
            }
        }

        return targets;
    }

    /// <summary>
    /// The ground wanted under sidewalks, from their mesh's top faces (keyed as <see cref="Targets"/>
    /// keys it): under each top as far as under the road beside it, and for a grid step past its edges
    /// (each grid point by the height of the nearest top) just under the top, so the ground between
    /// grid points never rises through a sidewalk, nor drops away from beside one. With whether each lies under a top (0) or beside
    /// one (how far out), for giving way to what else wants the points beside.
    /// </summary>
    public static Dictionary<(int X, int Z), (GroundTarget Target, float Out)> SidewalkTargets(MeshData sidewalks, float spacing)
    {
        var found = new Dictionary<(int X, int Z), (GroundTarget Target, float Out)>();
        // The lowest top within a grid step of each point, for those under one: the ground between grid
        // points is drawn straight between them, and would rise through a sloping, curving sidewalk.
        var lowest = new Dictionary<(int X, int Z), float>();
        for (var i = 0; i + 2 < sidewalks.Indices.Count; i += 3)
        {
            var (a, b, c) = (sidewalks.Vertices[sidewalks.Indices[i]], sidewalks.Vertices[sidewalks.Indices[i + 1]], sidewalks.Vertices[sidewalks.Indices[i + 2]]);
            // Its tops only, not its curbs and skirts.
            var normal = Vector3.Cross(b - a, c - a);
            if (normal.LengthSquared() < 1e-10f || MathF.Abs(Vector3.Normalize(normal).Y) < 0.5f)
                continue;
            var (minX, maxX) = (MathF.Min(a.X, MathF.Min(b.X, c.X)) - spacing, MathF.Max(a.X, MathF.Max(b.X, c.X)) + spacing);
            var (minZ, maxZ) = (MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) - spacing, MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) + spacing);
            for (var gx = (int)MathF.Ceiling(minX / spacing); gx <= (int)MathF.Floor(maxX / spacing); gx++)
            for (var gz = (int)MathF.Ceiling(minZ / spacing); gz <= (int)MathF.Floor(maxZ / spacing); gz++)
            {
                var at = new Vector2(gx * spacing, gz * spacing);
                var (distance, height) = OnTriangle(a, b, c, at);
                if (distance > spacing)
                    continue;
                lowest[(gx, gz)] = lowest.TryGetValue((gx, gz), out var low) ? MathF.Min(low, height) : height;
                var target = new GroundTarget(height - RoadCrossSection.CurbHeight + WalkGround, 1f, distance);
                if (!found.TryGetValue((gx, gz), out var had) || distance < had.Out)
                    found[(gx, gz)] = (target, distance);
            }
        }

        // Under a top: as far under the lowest top round it as under the road.
        foreach (var (at, (_, distance)) in found.ToList())
        {
            if (distance <= 0f)
                found[at] = (new GroundTarget(lowest[at] - RoadCrossSection.CurbHeight - Sink, 1f, 0f), 0f);
        }

        return found;
    }

    /// <summary>The lowest a fan's surface is over a square a grid step each way round a point, where it is there.</summary>
    private static float? LowestRound(Vector3 middle, IReadOnlyList<Vector3> outline, Vector2 at, float spacing)
    {
        float? lowest = null;
        for (var i = -2; i <= 2; i++)
        for (var j = -2; j <= 2; j++)
        {
            if (FanHeight(middle, outline, at + new Vector2(i, j) * (spacing / 2f)) is { } h && (lowest is null || h < lowest))
                lowest = h;
        }

        return lowest;
    }

    /// <summary>How far a point is from a triangle, seen from above, and the triangle's height at its nearest there.</summary>
    private static (float Distance, float Height) OnTriangle(Vector3 a, Vector3 b, Vector3 c, Vector2 at)
    {
        Vector2 Flat(Vector3 v) => new(v.X, v.Z);
        var (pa, pb, pc) = (Flat(a), Flat(b), Flat(c));
        // Barycentric weights of the point; inside, all are positive.
        var d = (pb.Y - pc.Y) * (pa.X - pc.X) + (pc.X - pb.X) * (pa.Y - pc.Y);
        if (MathF.Abs(d) > 1e-10f)
        {
            var u = ((pb.Y - pc.Y) * (at.X - pc.X) + (pc.X - pb.X) * (at.Y - pc.Y)) / d;
            var v = ((pc.Y - pa.Y) * (at.X - pc.X) + (pa.X - pc.X) * (at.Y - pc.Y)) / d;
            if (u >= 0f && v >= 0f && u + v <= 1f)
                return (0f, a.Y * u + b.Y * v + c.Y * (1f - u - v));
        }

        // Outside it: its nearest edge.
        var best = (float.MaxValue, 0f);
        foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
        {
            var (fp, fq) = (Flat(p), Flat(q));
            var pq = fq - fp;
            var s = pq.LengthSquared() > 1e-10f ? Math.Clamp(Vector2.Dot(at - fp, pq) / pq.LengthSquared(), 0f, 1f) : 0f;
            var distance = Vector2.Distance(at, fp + pq * s);
            if (distance < best.Item1)
                best = (distance, p.Y + (q.Y - p.Y) * s);
        }

        return best;
    }

    private static bool Inside(IReadOnlyList<Vector3> outline, Vector2 at)
    {
        var inside = false;
        for (int i = 0, j = outline.Count - 1; i < outline.Count; j = i++)
        {
            var (a, b) = (outline[i], outline[j]);
            if ((a.Z > at.Y) != (b.Z > at.Y) && at.X < (b.X - a.X) * (at.Y - a.Z) / (b.Z - a.Z) + a.X)
                inside = !inside;
        }

        return inside;
    }

    /// <summary>How far a point is from the outline, and the outline's height at its nearest there, and where that is.</summary>
    private static (float Distance, float Height, Vector2 At) Nearest(IReadOnlyList<Vector3> outline, Vector2 at)
    {
        var best = float.MaxValue;
        var height = 0f;
        var nearest = at;
        for (var i = 0; i < outline.Count; i++)
        {
            var (p, q) = (outline[i], outline[(i + 1) % outline.Count]);
            var a = new Vector2(p.X, p.Z);
            var ab = new Vector2(q.X, q.Z) - a;
            var t = ab.LengthSquared() > 1e-8f ? Math.Clamp(Vector2.Dot(at - a, ab) / ab.LengthSquared(), 0f, 1f) : 0f;
            var distance = Vector2.Distance(at, a + ab * t);
            if (distance < best)
                (best, height, nearest) = (distance, p.Y + (q.Y - p.Y) * t, a + ab * t);
        }

        return (best, height, nearest);
    }

    /// <summary>
    /// Fitted heights smoothed where the ground eases back to how it was (not under the road and
    /// its margin, where it wants the road's own height): each grid point there, again and again,
    /// moves towards the average of the points round it, as much as it is wanted, so the bumps
    /// the ground had, and any ridges between stretches of road wanting slightly different heights,
    /// iron out next to the road and fade back into the ground as it was further off. Points round
    /// it that were not fitted count at the ground's height.
    /// </summary>
    public static Dictionary<(int X, int Z), float> Smoothed(Dictionary<(int X, int Z), float> heights,
        IReadOnlyDictionary<(int X, int Z), GroundTarget> targets, Func<(int X, int Z), float?> ground, int passes = 4)
    {
        var current = new Dictionary<(int X, int Z), float>(heights);
        for (var pass = 0; pass < passes; pass++)
        {
            var next = new Dictionary<(int X, int Z), float>(current);
            foreach (var (at, height) in current)
            {
                if (!targets.TryGetValue(at, out var target) || target.Weight >= 1f)
                    continue;
                var sum = 0f;
                var count = 0;
                for (var dx = -1; dx <= 1; dx++)
                for (var dz = -1; dz <= 1; dz++)
                {
                    var near = (at.X + dx, at.Z + dz);
                    if ((current.TryGetValue(near, out var h) ? h : ground(near)) is not { } value)
                        continue;
                    sum += value;
                    count++;
                }

                if (count > 0)
                    next[at] = height + (sum / count - height) * target.Weight;
            }

            current = next;
        }

        return current;
    }

    /// <summary>How high a fan from the middle out to the outline is over a point; none if the point is not over it.</summary>
    public static float? FanHeight(Vector3 middle, IReadOnlyList<Vector3> outline, Vector2 at)
    {
        var m = new Vector2(middle.X, middle.Z);
        for (var i = 0; i < outline.Count; i++)
        {
            var (p, q) = (outline[i], outline[(i + 1) % outline.Count]);
            var (a, b) = (new Vector2(p.X, p.Z), new Vector2(q.X, q.Z));
            // Barycentric weights of the point in the triangle middle, a, b.
            var d = (b.Y - m.Y) * (a.X - m.X) + (m.X - b.X) * (a.Y - m.Y);
            if (MathF.Abs(d) < 1e-8f)
                continue;
            var wa = ((b.Y - m.Y) * (at.X - m.X) + (m.X - b.X) * (at.Y - m.Y)) / d;
            var wb = ((m.Y - a.Y) * (at.X - m.X) + (a.X - m.X) * (at.Y - m.Y)) / d;
            var wm = 1f - wa - wb;
            if (wa >= -1e-4f && wb >= -1e-4f && wm >= -1e-4f)
                return wm * middle.Y + wa * p.Y + wb * q.Y;
        }

        return null;
    }

    /// <summary>
    /// Whether a point lies beside the stretch of road from a to b (not before a nor past b), and
    /// if so how far right of its centre line it is and how high the road is there.
    /// </summary>
    /// <remarks>
    /// Before a (or past b), where the stretch has a neighbour that way, a point counts too: as far
    /// out from a (or b) as it is, on the side it lies, as on the outside of a bend.
    /// </remarks>
    private static bool Across(RoadFrame a, RoadFrame b, Vector3 point, bool openBefore, bool openAfter, out float offset, out float height, out bool offEnd)
    {
        var start = new Vector2(a.Position.X, a.Position.Z);
        var end = new Vector2(b.Position.X, b.Position.Z);
        var at = new Vector2(point.X, point.Z);
        var along = end - start;
        var t = along.LengthSquared() > 1e-8f ? Vector2.Dot(at - start, along) / along.LengthSquared() : 0f;
        offset = 0f;
        height = 0f;
        offEnd = false;
        if ((t < -1e-4f && !openBefore) || (t > 1f + 1e-4f && !openAfter))
            return false;

        var beyond = t < 0f || t > 1f;
        offEnd = beyond;
        t = Math.Clamp(t, 0f, 1f);
        var right = Vector3.Normalize(Vector3.Lerp(a.Right, b.Right, t));
        var centre = Vector3.Lerp(a.Position, b.Position, t);
        var fromCentre = at - new Vector2(centre.X, centre.Z);
        offset = Vector2.Dot(fromCentre, new Vector2(right.X, right.Z));
        // Off the stretch's end: as far out as it really is from that end, on its side.
        if (beyond)
            offset = MathF.CopySign(fromCentre.Length(), offset);
        height = centre.Y;
        return true;
    }

    /// <summary>
    /// How far out along a bank a point is, for how far down (or up) it the ground is: a bank rising from
    /// the road (a cutting) starts a grid step out past its edge. The ground is drawn in triangles
    /// between grid points, and one up the bank within a step of the edge tilted the triangle under the
    /// road's edge up through the asphalt.
    /// </summary>
    private static float Climb(float bankHeight, float outside, float spacing) =>
        bankHeight < 0f ? MathF.Max(outside - spacing, 0f) : outside;

    /// <summary>
    /// How far the ground is down a bank height deep (up, if negative) and width wide, this far out
    /// past the road's edge, with cover (0 to 1) of its height buried: the ground follows it down
    /// only the rest of the way.
    /// </summary>
    private static float Down(float height, float width, float outside, float cover) =>
        height * (1f - Math.Clamp(cover, 0f, 1f)) * (width > 0f ? Math.Clamp(outside / width, 0f, 1f) : 0f);

    private static float SmoothStep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
