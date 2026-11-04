using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>What carries a stretch of road: a viaduct over the ground, or a tunnel through it.</summary>
public enum StructureKind
{
    Viaduct,
    Tunnel,
}

/// <summary>
/// A viaduct or tunnel over Length metres from Start along a road. A viaduct is a deck DeckDepth
/// thick under the road, with barriers BarrierHeight high along its edges and pillars every
/// PillarSpacing metres down to the ground. A tunnel is a tube round the road, Clearance high
/// inside, its walls and roof WallThickness thick, open at both ends.
/// </summary>
public readonly record struct StructureSpec(
    StructureKind Kind,
    float Start,
    float Length,
    float DeckDepth = 1.2f,
    float BarrierHeight = 1f,
    float BarrierWidth = 0.4f,
    float PillarSpacing = 30f,
    float PillarWidth = 1.5f,
    float Clearance = 6f,
    float WallThickness = 1.5f,
    float LightSpacing = 8f,
    float LightLength = 1.5f,
    float LightRed = 1f,
    float LightGreen = 0.62f,
    float LightBlue = 0.25f,
    float LightEnergy = 2f,
    float LightRange = 14f)
{
    public float End => Start + Length;

    public bool Covers(float distance) => distance >= Start && distance <= End;
}

/// <summary>
/// Builds viaducts and tunnels along a road's frames, as one mesh of concrete. Offsets across are
/// right of the road's centre line; heights are from its surface.
/// </summary>
public static class RoadStructureBuilder
{
    /// <summary>How far pillars reach into the ground, so they never stand on air where it dips.</summary>
    public const float PillarSink = 1f;

    /// <summary>The widest a road is carried on one pillar; wider ones stand on a row of them.</summary>
    public const float PillarReach = 12f;

    /// <summary>The concrete's texture repeats every this many metres.</summary>
    public const float TextureSize = 4f;

    /// <summary>
    /// The structures along a road. The ground says how high the ground is under a point (none
    /// where it does not know), for how far a viaduct's pillars reach down; blocked, whether a
    /// pillar this many metres wide each way from a point under the deck would stand on a road.
    /// </summary>
    public static MeshData Build(IReadOnlyList<RoadFrame> frames, RoadCrossSection section, IReadOnlyList<StructureSpec> structures, Func<Vector3, float?> ground,
        Func<Vector3, float, bool>? blocked = null, MeshData? inside = null)
    {
        var mesh = new MeshData();
        if (frames.Count < 2)
            return mesh;
        var bands = section.Bands();
        var (left, right) = (bands[0].Left, bands[^1].Right);
        foreach (var structure in structures)
        {
            var stretch = RoadCurve.Trim(frames, Math.Max(structure.Start, frames[0].Distance), Math.Min(structure.End, frames[^1].Distance));
            if (stretch.Count < 2)
                continue;
            if (structure.Kind == StructureKind.Viaduct)
                Viaduct(mesh, stretch, left, right, structure, ground, blocked);
            else
                Tunnel(mesh, inside ?? mesh, stretch, left, right, structure);
        }

        return mesh;
    }

    /// <summary>The runs of a road's frames not carried by any of these structures, where it lies on the ground.</summary>
    public static IReadOnlyList<IReadOnlyList<RoadFrame>> OnGround(IReadOnlyList<RoadFrame> frames, IReadOnlyList<StructureSpec> structures)
    {
        if (structures.Count == 0 || frames.Count < 2)
            return new[] { frames };
        var runs = new List<IReadOnlyList<RoadFrame>>();
        var from = frames[0].Distance;
        foreach (var structure in structures.OrderBy(s => s.Start))
        {
            if (structure.Start > from)
                runs.Add(RoadCurve.Trim(frames, from, Math.Min(structure.Start, frames[^1].Distance)));
            from = Math.Max(from, structure.End);
        }

        if (from < frames[^1].Distance)
            runs.Add(RoadCurve.Trim(frames, from, frames[^1].Distance));
        return runs.Where(r => r.Count >= 2).ToList();
    }

    /// <summary>
    /// The shapes a tunnel's ground is masked by (see IRoadGround.SetMask), in world X and Z: its footprint,
    /// out to the middle of its walls, piece by piece, each with the height of the middle of its roof
    /// there, so the ground's cut edge is inside them, out of sight. Ground lower would show inside the
    /// tube; ground higher meets the walls and roof, or covers them, and stays.
    /// </summary>
    public static (Godot.Vector2[][] Shapes, float[] Tops) TunnelMask(IReadOnlyList<RoadFrame> frames, RoadCrossSection section, StructureSpec tunnel)
    {
        var stretch = RoadCurve.Trim(frames, Math.Max(tunnel.Start, frames[0].Distance), Math.Min(tunnel.End, frames[^1].Distance));
        if (stretch.Count < 2)
            return ([], []);
        var bands = section.Bands();
        var (left, right) = (bands[0].Left - tunnel.WallThickness / 2f, bands[^1].Right + tunnel.WallThickness / 2f);
        static Godot.Vector2 Flat(Vector3 at) => new(at.X, at.Z);
        var shapes = new List<Godot.Vector2[]>();
        var tops = new List<float>();
        for (var i = 1; i < stretch.Count; i++)
        {
            var (a, b) = (stretch[i - 1], stretch[i]);
            shapes.Add([Flat(a.Across(left)), Flat(a.Across(right)), Flat(b.Across(right)), Flat(b.Across(left))]);
            tops.Add(Math.Max(a.Position.Y, b.Position.Y) + tunnel.Clearance + tunnel.WallThickness / 2f);
        }

        return ([.. shapes], [.. tops]);
    }

    /// <summary>
    /// A tunnel's light bars, in the corners of its roof, along it, by turns on either side, each tilted
    /// to face the middle of the road: their mesh, and where each is with the way it faces.
    /// </summary>
    public static (MeshData Mesh, List<(Vector3 At, Vector3 Facing)> Places) TunnelBars(IReadOnlyList<RoadFrame> frames, RoadCrossSection section, IReadOnlyList<StructureSpec> structures)
    {
        const float inset = 0.3f;
        const float halfWidth = 0.14f;
        const float halfDepth = 0.05f;
        var mesh = new MeshData();
        var places = new List<(Vector3, Vector3)>();
        if (frames.Count < 2)
            return (mesh, places);
        var bands = section.Bands();
        var (left, right) = (bands[0].Left, bands[^1].Right);
        foreach (var tunnel in structures.Where(s => s.Kind == StructureKind.Tunnel && s.LightSpacing > 0f && s.LightLength > 0f))
        {
            var (from, to) = (Math.Max(tunnel.Start, frames[0].Distance), Math.Min(tunnel.End, frames[^1].Distance));
            var onLeft = true;
            for (var along = from + tunnel.LightSpacing / 2f; along < to - tunnel.LightLength; along += tunnel.LightSpacing / 2f)
            {
                var frame = RoadCurve.At(frames, along);
                var at = frame.Across(onLeft ? left + inset : right - inset) + Vector3.UnitY * (tunnel.Clearance - inset);
                var facing = Vector3.Normalize(frame.Across((left + right) / 2f) - at);
                places.Add((at, facing));
                var length = Vector3.Normalize(frame.Forward) * tunnel.LightLength / 2f;
                var width = Vector3.Normalize(Vector3.Cross(facing, length)) * halfWidth;
                var depth = facing * halfDepth;
                Vector3 Corner(int l, int w, int d) => at + length * l + width * w + depth * d;
                Face(mesh, Corner(-1, -1, 1), Corner(1, -1, 1), Corner(1, 1, 1), Corner(-1, 1, 1), facing);
                Face(mesh, Corner(-1, -1, -1), Corner(1, -1, -1), Corner(1, 1, -1), Corner(-1, 1, -1), -facing);
                Face(mesh, Corner(-1, -1, -1), Corner(1, -1, -1), Corner(1, -1, 1), Corner(-1, -1, 1), -Vector3.Normalize(width));
                Face(mesh, Corner(-1, 1, -1), Corner(1, 1, -1), Corner(1, 1, 1), Corner(-1, 1, 1), Vector3.Normalize(width));
                Face(mesh, Corner(-1, -1, -1), Corner(-1, 1, -1), Corner(-1, 1, 1), Corner(-1, -1, 1), -Vector3.Normalize(length));
                Face(mesh, Corner(1, -1, -1), Corner(1, 1, -1), Corner(1, 1, 1), Corner(1, -1, 1), Vector3.Normalize(length));
                onLeft = !onLeft;
            }
        }

        return (mesh, places);
    }

    /// <summary>The grid points round a tunnel's footprint and a little past: where tunnels used to open the ground, for filling it in again.</summary>
    public static IReadOnlyCollection<(int X, int Z)> TunnelCells(IReadOnlyList<RoadFrame> frames, RoadCrossSection section, StructureSpec tunnel, float spacing)
    {
        var cells = new HashSet<(int X, int Z)>();
        var stretch = RoadCurve.Trim(frames, Math.Max(tunnel.Start, frames[0].Distance), Math.Min(tunnel.End, frames[^1].Distance));
        if (stretch.Count < 2)
            return cells;
        var bands = section.Bands();
        var reach = tunnel.WallThickness + spacing * 2f;
        foreach (var frame in stretch)
        {
            for (var across = bands[0].Left - reach; across <= bands[^1].Right + reach; across += spacing / 2f)
            {
                var at = frame.Across(across);
                cells.Add(((int)MathF.Round(at.X / spacing), (int)MathF.Round(at.Z / spacing)));
            }
        }

        return cells;
    }

    /// <summary>
    /// A junction carried as a viaduct: a deck under its surface (a fan from its middle out to its
    /// outline), its sides and barriers along its corners (each running clockwise round it, so away
    /// from it is on the left), and a pillar under its middle down into the ground.
    /// </summary>
    public static MeshData JunctionDeck(Vector3 middle, IReadOnlyList<Vector3> outline, IReadOnlyList<IReadOnlyList<Vector3>> corners,
        StructureSpec deck, Func<Vector3, float?> ground, Func<Vector3, float, bool>? blocked = null)
    {
        var mesh = new MeshData();
        if (outline.Count < 3)
            return mesh;
        var down = Vector3.UnitY * deck.DeckDepth;

        // Its underside: the surface's fan, lowered, facing down.
        var centre = mesh.Add(middle - down, -Vector3.UnitY, new Vector2(middle.X, middle.Z) / TextureSize);
        var first = mesh.Vertices.Count;
        foreach (var p in outline)
            mesh.Add(p - down, -Vector3.UnitY, new Vector2(p.X, p.Z) / TextureSize);
        for (var i = 0; i < outline.Count; i++)
            mesh.Indices.AddRange(new[] { centre, first + (i + 1) % outline.Count, first + i });

        // Along each corner: the deck's side, and a barrier on it.
        foreach (var corner in corners.Where(c => c.Count >= 2))
        {
            var frames = new List<RoadFrame>();
            var along = 0f;
            for (var i = 0; i < corner.Count; i++)
            {
                if (i > 0)
                    along += Vector3.Distance(corner[i - 1], corner[i]);
                var way = Level(corner[Math.Min(i + 1, corner.Count - 1)] - corner[Math.Max(i - 1, 0)]);
                // Right of the way round is in, over the junction.
                frames.Add(new RoadFrame(corner[i], way, Vector3.Normalize(Vector3.Cross(way, Vector3.UnitY)), along));
            }

            var height = Vector3.UnitY * deck.BarrierHeight;
            var width = deck.BarrierWidth;
            Sheet(mesh, frames, f => (f.Position - down, f.Position), f => -f.Right);
            Sheet(mesh, frames, f => (f.Position, f.Position + height), f => -f.Right);
            Sheet(mesh, frames, f => (f.Position + height, f.Across(width) + height), _ => Vector3.UnitY);
            Sheet(mesh, frames, f => (f.Across(width) + height, f.Across(width)), f => f.Right);
            // Its barrier closed at both ends, where the roads come in.
            foreach (var (frame, outward) in new[] { (frames[0], -frames[0].Forward), (frames[^1], frames[^1].Forward) })
                Face(mesh, frame.Position, frame.Across(width), frame.Across(width) + height, frame.Position + height, outward);
        }

        // A pillar under its middle, unless a road runs there.
        if (ground(middle) is { } floor && floor < middle.Y - deck.DeckDepth - 0.1f && blocked?.Invoke(middle - down, deck.PillarWidth) != true)
            Pillar(mesh, middle - down, floor - PillarSink, deck.PillarWidth, Vector3.UnitX, Vector3.UnitZ);
        return mesh;
    }

    private static void Viaduct(MeshData mesh, IReadOnlyList<RoadFrame> stretch, float left, float right, StructureSpec viaduct, Func<Vector3, float?> ground,
        Func<Vector3, float, bool>? blocked)
    {
        var depth = viaduct.DeckDepth;
        var up = Vector3.UnitY;
        // The deck: its sides from the barriers' feet down, and its underside.
        Sheet(mesh, stretch, f => (f.Across(left) - up * depth, f.Across(left)), f => -f.Right);
        Sheet(mesh, stretch, f => (f.Across(right), f.Across(right) - up * depth), f => f.Right);
        Sheet(mesh, stretch, f => (f.Across(right) - up * depth, f.Across(left) - up * depth), _ => -up);

        // A barrier along each edge: its outer face on up from the deck's side, its top, its inner face (none, with no height).
        foreach (var (edge, inward) in viaduct.BarrierHeight > 0f ? new[] { (left, 1f), (right, -1f) } : [])
        {
            var inner = edge + inward * viaduct.BarrierWidth;
            var height = up * viaduct.BarrierHeight;
            Sheet(mesh, stretch, f => (f.Across(edge), f.Across(edge) + height), f => -inward * f.Right);
            Sheet(mesh, stretch, f => (f.Across(edge) + height, f.Across(inner) + height), _ => up);
            Sheet(mesh, stretch, f => (f.Across(inner) + height, f.Across(inner)), f => inward * f.Right);
            // Closed at both ends.
            foreach (var (frame, outward) in new[] { (stretch[0], -stretch[0].Forward), (stretch[^1], stretch[^1].Forward) })
                Face(mesh, frame.Across(edge), frame.Across(inner), frame.Across(inner) + height, frame.Across(edge) + height, Level(outward));
        }

        // Its ends, where it meets the road on the ground: the deck's face across.
        foreach (var (frame, outward) in new[] { (stretch[0], -stretch[0].Forward), (stretch[^1], stretch[^1].Forward) })
            Face(mesh, frame.Across(left) - up * depth, frame.Across(right) - up * depth, frame.Across(right), frame.Across(left), Level(outward));

        // Pillars in a row across under it every so often, from the deck down into the ground.
        var width = right - left;
        var count = Math.Max(1, (int)MathF.Ceiling(width / PillarReach));
        var length = stretch[^1].Distance - stretch[0].Distance;
        var spans = Math.Max(1, (int)MathF.Round(length / Math.Max(viaduct.PillarSpacing, 1f)));
        var spanLength = length / spans;
        for (var p = 0; p < spans; p++)
        {
            var middle = stretch[0].Distance + spanLength * (p + 0.5f);
            for (var k = 0; k < count; k++)
            {
                // Where a road runs under it, a little along either way, nearer first, to stand beside
                // it; none in this span if it cannot.
                for (var shift = 0f; shift <= spanLength * 0.45f; shift += 1f)
                {
                    var placed = false;
                    foreach (var at in shift == 0f ? new[] { middle } : new[] { middle - shift, middle + shift })
                    {
                        var frame = RoadCurve.At(stretch, at);
                        var centre = frame.Across(left + width * (k + 0.5f) / count);
                        var top = centre.Y - depth;
                        if (ground(centre) is not { } floor || floor >= top - 0.1f || blocked?.Invoke(centre with { Y = top }, viaduct.PillarWidth / 2f) == true)
                            continue;
                        Pillar(mesh, centre with { Y = top }, floor - PillarSink, viaduct.PillarWidth / 2f, Level(frame.Forward), Level(frame.Right));
                        placed = true;
                        break;
                    }

                    if (placed || blocked is null)
                        break;
                }
            }
        }
    }

    private static void Tunnel(MeshData mesh, MeshData inside, IReadOnlyList<RoadFrame> stretch, float left, float right, StructureSpec tunnel)
    {
        var up = Vector3.UnitY;
        var high = up * tunnel.Clearance;
        var thick = tunnel.WallThickness;
        var (outerLeft, outerRight) = (left - thick, right + thick);
        var roof = up * (tunnel.Clearance + thick);
        var foot = up * RoadMeshBuilder.SkirtDepth;

        // Inside: its walls facing in, and its ceiling facing down.
        Sheet(inside, stretch, f => (f.Across(left) + high, f.Across(left)), f => f.Right);
        Sheet(inside, stretch, f => (f.Across(right), f.Across(right) + high), f => -f.Right);
        Sheet(inside, stretch, f => (f.Across(right) + high, f.Across(left) + high), _ => -up);
        // Outside, where the ground is open round it: its walls facing out, down into the ground, and its roof.
        Sheet(mesh, stretch, f => (f.Across(outerLeft) - foot, f.Across(outerLeft) + roof), f => -f.Right);
        Sheet(mesh, stretch, f => (f.Across(outerRight) + roof, f.Across(outerRight) - foot), f => f.Right);
        Sheet(mesh, stretch, f => (f.Across(outerLeft) + roof, f.Across(outerRight) + roof), _ => up);
        // The floor between each wall's inside and outside, where the road's shoulder ends.
        Sheet(mesh, stretch, f => (f.Across(outerLeft), f.Across(left)), _ => up);
        Sheet(mesh, stretch, f => (f.Across(right), f.Across(outerRight)), _ => up);

        // Its portals: the ring of wall and roof round each opening, facing out along the road.
        foreach (var (frame, outward) in new[] { (stretch[0], -stretch[0].Forward), (stretch[^1], stretch[^1].Forward) })
        {
            var facing = Level(outward);
            Face(mesh, frame.Across(outerLeft) - foot, frame.Across(left) - foot, frame.Across(left) + high, frame.Across(outerLeft) + roof, facing);
            Face(mesh, frame.Across(right) - foot, frame.Across(outerRight) - foot, frame.Across(outerRight) + roof, frame.Across(right) + high, facing);
            Face(mesh, frame.Across(outerLeft) + roof, frame.Across(left) + high, frame.Across(right) + high, frame.Across(outerRight) + roof, facing);
        }
    }

    /// <summary>
    /// A sheet along the stretch, from one edge to the other at each frame, facing the way normal
    /// says: its winding is chosen to face that way, as Godot draws a triangle's front.
    /// </summary>
    private static void Sheet(MeshData mesh, IReadOnlyList<RoadFrame> frames, Func<RoadFrame, (Vector3 A, Vector3 B)> edges, Func<RoadFrame, Vector3> normal)
    {
        var pairs = frames.Select(edges).ToList();
        // Which way round each pair goes: so that the first quad with any area faces the normal.
        var swap = false;
        for (var i = 0; i + 1 < pairs.Count; i++)
        {
            var facing = Vector3.Cross(pairs[i + 1].A - pairs[i].A, pairs[i + 1].B - pairs[i].A);
            if (facing.LengthSquared() < 1e-10f)
                continue;
            swap = Vector3.Dot(facing, normal(frames[i])) > 0f;
            break;
        }

        var first = mesh.Vertices.Count;
        for (var i = 0; i < pairs.Count; i++)
        {
            var (a, b) = swap ? (pairs[i].B, pairs[i].A) : pairs[i];
            var n = Vector3.Normalize(normal(frames[i]));
            var v = frames[i].Distance / TextureSize;
            mesh.Add(a, n, new Vector2(0f, v));
            mesh.Add(b, n, new Vector2(Vector3.Distance(a, b) / TextureSize, v));
        }

        for (var i = 0; i + 1 < pairs.Count; i++)
        {
            var a = first + 2 * i;
            mesh.Quad(a, a + 2, a + 3, a + 1);
        }
    }

    /// <summary>A flat four-cornered face, wound to face the way it is told, whichever way round its corners come.</summary>
    private static void Face(MeshData mesh, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
    {
        var facing = Vector3.Cross(b - a, c - a);
        if (Vector3.Dot(facing, normal) > 0f)
            (b, d) = (d, b);
        var right = Vector3.Normalize(Vector3.Cross(normal, Vector3.UnitY).LengthSquared() > 1e-6f ? Vector3.Cross(normal, Vector3.UnitY) : Vector3.UnitX);
        Vector2 Uv(Vector3 p) => new Vector2(Vector3.Dot(p, right), p.Y) / TextureSize;
        var first = mesh.Vertices.Count;
        foreach (var p in new[] { a, b, c, d })
            mesh.Add(p, normal, Uv(p));
        mesh.Quad(first, first + 1, first + 2, first + 3);
    }

    /// <summary>A square pillar from top down to bottom, half a side wide each way, its sides along and across the road.</summary>
    private static void Pillar(MeshData mesh, Vector3 top, float bottom, float half, Vector3 along, Vector3 across)
    {
        var low = top with { Y = bottom };
        foreach (var side in new[] { along, across, -along, -across })
        {
            var sideways = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, side)) * half;
            var face = side * half;
            Face(mesh, low + face - sideways, low + face + sideways, top + face + sideways, top + face - sideways, side);
        }
    }

    private static Vector3 Level(Vector3 v)
    {
        var flat = new Vector3(v.X, 0f, v.Z);
        return flat.LengthSquared() > 1e-8f ? Vector3.Normalize(flat) : Vector3.UnitZ;
    }
}
