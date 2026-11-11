#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantRoads.Editor;

public partial class RoadsPlugin
{
    private const string GroundCheckItem = "Find Ground Through Roads";

    /// <summary>
    /// Looks for the terrain showing through roads: along every road (each metre) and across its
    /// asphalt (each half metre), where the ground stands above the road's surface, however the
    /// ground's cells are split into triangles. Prints the worst places, and how many per road.
    /// </summary>
    private void FindGroundThroughRoads()
    {
        if (EditorInterface.Singleton.GetEditedSceneRoot() is not { } root)
            return;
        // What there is to look over, read from the scene here; looked over on a thread.
        var networks = new List<(IRoadGround Surface, List<(string Name, List<RoadFrame> Frames, float Left, float Right, List<StructureSpec> Structures)> Roads,
            List<(string Name, List<Vector3> Outline)> Forks, List<(string Name, MeshData? Mesh, string What)> Lots)>();
        foreach (var network in root.FindChildren("*", nameof(RoadNetwork), true, false).OfType<RoadNetwork>())
        {
            if (network.Ground is not { } surface)
                continue;
            var roads = new List<(string, List<RoadFrame>, float, float, List<StructureSpec>)>();
            foreach (var road in network.Roads)
            {
                var frames = road.Frames();
                var runs = RoadMeshBuilder.PavedRuns(road.CrossSection).ToList();
                if (frames.Count >= 2 && runs.Count > 0)
                    roads.Add((road.Name, frames.ToList(), runs[0].Left, runs[^1].Right, road.StructureSpecs().ToList()));
            }

            var forks = network.Forks.Where(f => !f.Carried && f.Outline.Count >= 3).Select(f => ((string)f.Name, f.Outline.Select(p => p.ToGodot()).ToList())).ToList();
            var lots = network.ParkingLots.SelectMany(l => new[] { ((string)l.Name, l.Asphalt, "asphalt"), ((string)l.Name, l.Sidewalks, "sidewalk") }).ToList();
            networks.Add((surface, roads, forks, lots));
        }

        RoadJob.Start(root, GroundCheckItem, job => LookForGround(job, networks), found => ReportGround(root, found), readsTerrain: true);
    }

    private static List<(float Above, string Road, float Along, float Across, Vector3 At)> LookForGround(RoadJob job,
        List<(IRoadGround Surface, List<(string Name, List<RoadFrame> Frames, float Left, float Right, List<StructureSpec> Structures)> Roads,
            List<(string Name, List<Vector3> Outline)> Forks, List<(string Name, MeshData? Mesh, string What)> Lots)> networks)
    {
        job.SetTotal(networks.Sum(n => n.Roads.Count + n.Forks.Count + n.Lots.Count));
        var found = new List<(float Above, string Road, float Along, float Across, Vector3 At)>();
        foreach (var (surface, roadList, forkList, lotList) in networks)
        {
            var spacing = surface.VertexSpacing;
            // The ground at a place as drawn: whichever way its cell is split, the higher.
            float GroundAt(Vector3 at)
            {
                var (x, z) = (at.X / spacing, at.Z / spacing);
                var (x0, z0) = (Mathf.FloorToInt(x), Mathf.FloorToInt(z));
                var (fx, fz) = (x - x0, z - z0);
                var (a, b, c, d) = ((surface.SampleHeight(x0, z0) ?? float.NaN), (surface.SampleHeight(x0 + 1, z0) ?? float.NaN), (surface.SampleHeight(x0, z0 + 1) ?? float.NaN), (surface.SampleHeight(x0 + 1, z0 + 1) ?? float.NaN));
                // Split from (0,0) to (1,1), and from (1,0) to (0,1).
                var one = fx >= fz ? a + (b - a) * fx + (d - b) * fz : a + (c - a) * fz + (d - c) * fx;
                var other = fx + fz <= 1f ? a + (b - a) * fx + (c - a) * fz : d + (c - d) * (1f - fx) + (b - d) * (1f - fz);
                return Mathf.Max(one, other);
            }

            foreach (var (roadName, frames, left, right, structures) in roadList)
            {
                job.Step();
                for (var along = frames[0].Distance; along <= frames[^1].Distance; along += 1f)
                {
                    if (structures.Any(s => s.Covers(along)))
                        continue;
                    var frame = RoadCurve.At(frames, along);
                    for (var across = left + 0.1f; across <= right - 0.1f; across += 0.5f)
                    {
                        var at = frame.Across(across).ToGodot();
                        var ground = GroundAt(at);
                        if (float.IsNaN(ground))
                            continue;
                        var above = ground - at.Y;
                        if (above > 0.005f)
                            found.Add((above, roadName, along, across, at));
                    }
                }
            }

            // Forks and merges (not viaducts): over their paving, each half metre; and where the ground is well under it (not fitted).
            foreach (var (forkName, outline) in forkList)
            {
                job.Step();
                var flat = outline.Select(p => new Vector2(p.X, p.Z)).ToArray();
                var (minX, maxX, minZ, maxZ) = (flat.Min(p => p.X), flat.Max(p => p.X), flat.Min(p => p.Y), flat.Max(p => p.Y));
                var high = outline.Average(p => p.Y);
                var below = 0;
                for (var x = minX; x <= maxX; x += 0.5f)
                for (var z = minZ; z <= maxZ; z += 0.5f)
                {
                    if (!InPolygon(flat, new Vector2(x, z)))
                        continue;
                    // The fork's surface there: its outline's nearest point's height (it is near level across).
                    var nearest = outline.MinBy(p => new Vector2(p.X - x, p.Z - z).LengthSquared());
                    var at = new Vector3(x, nearest.Y, z);
                    var ground = GroundAt(at);
                    if (float.IsNaN(ground))
                        continue;
                    if (ground - at.Y > 0.005f)
                        found.Add((ground - at.Y, $"{forkName} (fork)", 0f, 0f, at));
                    else if (at.Y - ground > 1f)
                        below++;
                }

                if (below > 0)
                    GD.Print($"  {forkName}: the ground is more than 1 m under it in {below} places (not fitted to it?). Its outline is {outline.Count} points round, at about {high:0.0} m.");
            }

            // Parking lots: over their asphalt and sidewalk tops, each half metre.
            foreach (var (lotName, mesh, what) in lotList)
            {
                job.Step();
                {
                    if (mesh is null)
                        continue;
                    for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
                    {
                        var (a, b, c) = (mesh.Vertices[mesh.Indices[i]], mesh.Vertices[mesh.Indices[i + 1]], mesh.Vertices[mesh.Indices[i + 2]]);
                        var normal = System.Numerics.Vector3.Cross(b - a, c - a);
                        if (normal.LengthSquared() < 1e-10f || Mathf.Abs(System.Numerics.Vector3.Normalize(normal).Y) < 0.5f)
                            continue;
                        var steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(System.Numerics.Vector3.Distance(a, b), System.Numerics.Vector3.Distance(a, c)) / 0.5f));
                        for (var u = 0; u <= steps; u++)
                        for (var v = 0; u + v <= steps; v++)
                        {
                            var at = (a + (b - a) * ((float)u / steps) + (c - a) * ((float)v / steps)).ToGodot();
                            var ground = GroundAt(at);
                            if (float.IsNaN(ground))
                                continue;
                            var above = ground - at.Y;
                            if (above > 0.005f)
                                found.Add((above, $"{lotName} {what}", 0f, 0f, at));
                        }
                    }
                }
            }
        }

        return found;
    }

    private static bool InPolygon(Vector2[] outline, Vector2 point)
    {
        var inside = false;
        for (int i = 0, j = outline.Length - 1; i < outline.Length; j = i++)
        {
            var (a, b) = (outline[i], outline[j]);
            if (a.Y > point.Y != b.Y > point.Y && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }

        return inside;
    }

    /// <summary>What was found, printed: back on the main thread.</summary>
    private static void ReportGround(Node root, List<(float Above, string Road, float Along, float Across, Vector3 At)> found)
    {
        if (!GodotObject.IsInstanceValid(root))
            return;
        // Once per place (a lot's triangles share their corners).
        found = found.DistinctBy(f => (f.Road, Mathf.RoundToInt(f.At.X * 10f), Mathf.RoundToInt(f.At.Z * 10f))).ToList();
        if (found.Count == 0)
        {
            GD.Print("Find Ground Through Roads: the ground is under every road.");
            return;
        }

        GD.Print($"Find Ground Through Roads: {found.Count} places the ground is above a road's surface. Worst first:");
        foreach (var (above, road, along, across, at) in found.OrderByDescending(f => f.Above).Take(25))
            GD.Print($"  {road}: {above:0.00} m above, {along:0} m along, {across:0.0} m across, at ({at.X:0.0}, {at.Y:0.0}, {at.Z:0.0})");
        // At the worst few places: what each part wants at the grid points round it, and what the ground has.
        foreach (var network in root.FindChildren("*", nameof(RoadNetwork), true, false).OfType<RoadNetwork>())
        {
            if (network.Ground is not { } surface)
                continue;
            var spacing = surface.VertexSpacing;
            var worst = found.OrderByDescending(f => f.Above).Take(6).ToList();
            var points = worst.SelectMany(w =>
            {
                var (x0, z0) = (Mathf.FloorToInt(w.At.X / spacing), Mathf.FloorToInt(w.At.Z / spacing));
                return new[] { (x0, z0), (x0 + 1, z0), (x0, z0 + 1), (x0 + 1, z0 + 1) };
            }).ToList();
            var wishes = network.WishesAt(points);
            foreach (var w in worst)
            {
                GD.Print($"  At {w.Road} {w.Along:0} m along ({w.At.X:0.0}, {w.At.Z:0.0}), road surface {w.At.Y:0.00}:");
                var (x0, z0) = (Mathf.FloorToInt(w.At.X / spacing), Mathf.FloorToInt(w.At.Z / spacing));
                foreach (var point in new[] { (x0, z0), (x0 + 1, z0), (x0, z0 + 1), (x0 + 1, z0 + 1) })
                {
                    var list = wishes.TryGetValue(point, out var l) ? l : [];
                    GD.Print($"    grid {point}: ground {(surface.SampleHeight(point.Item1, point.Item2) ?? float.NaN):0.00}; wanted: "
                             + (list.Count == 0 ? "nothing" : string.Join("; ", list.Select(x => $"{x.Part} {x.Target.Height:0.00} w{x.Target.Weight:0.00} out{x.Target.Outside:0.0}"))));
                    // Where the road's own wish there is off a stretch's end: every stretch of it that reaches the point.
                    if (list.Any(x => x.Part == w.Road && x.Target.Outside >= 1000f) && network.Roads.FirstOrDefault(r => r.Name == w.Road) is { } own)
                    {
                        foreach (var line in RoadGround.Explain(own.Frames(), own.CrossSection, spacing, point.Item1, point.Item2, network.Margin, network.Falloff))
                            GD.Print("      " + line);
                    }
                }
            }
        }

        GD.Print("  Per road: " + string.Join(", ", found.GroupBy(f => f.Road).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
    }
}
#endif
