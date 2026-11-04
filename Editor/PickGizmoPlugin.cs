#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantRoads.Editor;

/// <summary>
/// Lets junctions, parking lots and their bay areas, fences, plots and building type and exclusion areas be clicked in
/// the 3D editor to select them. Kept cheap, as every one of them makes its shape as a level opens:
/// an area's (a junction's, a parking lot's, a plot's) is the ground inside its outline, a fence's
/// its line; and what it draws, unseen (the editor only tries a gizmo's shape where the click's ray
/// passes through something the gizmo draws), only that outline or line.
/// </summary>
[Tool]
public partial class PickGizmoPlugin : EditorNode3DGizmoPlugin
{
    public override string _GetGizmoName() => "Pick";

    public override bool _HasGizmo(Node3D forNode3D) =>
        forNode3D is RoadJunction or ParkingLot or ParkingBayArea or ParkingIsland or RoadFork || RoadLinks.PickShape?.Invoke(forNode3D) is not null;

    private StandardMaterial3D Unseen => _unseen ??= new StandardMaterial3D
    {
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        AlbedoColor = new Color(0f, 0f, 0f, 0f),
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        NoDepthTest = true,
    };

    private StandardMaterial3D? _unseen;

    public override void _Redraw(EditorNode3DGizmo gizmo)
    {
        gizmo.Clear();
        var node = gizmo.GetNode3D();
        var local = node.GlobalTransform.AffineInverse();
        var picked = RoadLinks.PickShape?.Invoke(node);
        if (picked?.Lines is { } lines)
        {
            gizmo.AddCollisionSegments([.. lines]);
            gizmo.AddLines([.. lines], Unseen);
            return;
        }

        IReadOnlyList<Vector3> outline = node switch
        {
            RoadJunction { Shape: { } shape } => [.. shape.Reach.Select(p => p.ToGodot())],
            ParkingLot lot => [.. lot.OuterEdge.Select(p => p.ToGodot())],
            ParkingIsland island => island.Outline,
            RoadFork fork => [.. fork.Outline.Select(p => p.ToGodot())],
            // A bay area's on its lot's surface, above the lot's own, so a click there finds the area first.
            ParkingBayArea bays => [.. bays.Corners.Select(c => c.GlobalPosition with
            {
                Y = (bays.GetParent() is ParkingLot onLot ? onLot.HeightAt(c.GlobalPosition.X, c.GlobalPosition.Z) : c.GlobalPosition.Y) + 0.2f,
            })],
            _ => picked?.Outline ?? [],
        };
        if (outline.Count < 3)
            return;
        // Where they overlap, a click finds a parking lot's bay area first, then the lot, then a plot
        // round them: a lot's (and its areas') flat over its highest point, a plot's under its lowest.
        // A square the other way round: plots go on it, so a click finds them first, under them.
        if (node is Square)
        {
            var bottom = outline.Min(p => p.Y) - 0.6f;
            outline = [.. outline.Select(p => p with { Y = bottom })];
        }
        else if (node is ParkingLot or ParkingBayArea or ParkingIsland)
        {
            var top = node is ParkingBayArea or ParkingIsland && node.GetParent() is ParkingLot { OuterEdge.Count: > 0 } parent
                ? parent.OuterEdge.Max(p => p.Y) + (node is ParkingIsland ? 0.4f : 0.3f)
                : outline.Max(p => p.Y) + (node is ParkingBayArea ? 0.3f : 0.2f);
            outline = [.. outline.Select(p => p with { Y = top })];
        }
        else if (picked is { Lift: < 0f })
        {
            var bottom = outline.Min(p => p.Y) + picked.Lift;
            outline = [.. outline.Select(p => p with { Y = bottom })];
        }


        // The ground inside it, a little above its outline, to be clicked: its outline without points
        // on top of one another or in line between their neighbours (which leave the triangulation
        // with nothing), and, should it still find nothing, triangles over its points kept where inside it.
        var points = Clean(outline.Select(p => local * (p + Vector3.Up * 0.1f)).ToList());
        if (points.Count < 3)
            return;
        var flat = points.Select(p => new Vector2(p.X, p.Z)).ToArray();
        var indices = Geometry2D.TriangulatePolygon(flat);
        if (indices.Length < 3)
        {
            var all = Geometry2D.TriangulateDelaunay(flat);
            indices = [.. Enumerable.Range(0, all.Length / 3).Where(t => Geometry2D.IsPointInPolygon((flat[all[3 * t]] + flat[all[3 * t + 1]] + flat[all[3 * t + 2]]) / 3f, flat))
                .SelectMany(t => new[] { all[3 * t], all[3 * t + 1], all[3 * t + 2] })];
        }

        var faces = indices.Select(i => points[i]).ToArray();
        if (faces.Length >= 3 && new TriangleMesh() is var mesh && mesh.CreateFromFaces(faces))
            gizmo.AddCollisionTriangles(mesh);
        // Round its outline, unseen.
        var edges = new List<Vector3>();
        for (var i = 0; i < points.Count; i++)
        {
            edges.Add(points[i]);
            edges.Add(points[(i + 1) % points.Count]);
        }

        gizmo.AddLines([.. edges], Unseen);
    }

    /// <summary>An outline without points within a few centimetres of the one before, or in line between their neighbours.</summary>
    private static List<Vector3> Clean(List<Vector3> outline)
    {
        var kept = new List<Vector3>();
        foreach (var p in outline)
        {
            if (kept.Count == 0 || new Vector2(p.X - kept[^1].X, p.Z - kept[^1].Z).Length() > 0.05f)
                kept.Add(p);
        }

        while (kept.Count > 1 && new Vector2(kept[0].X - kept[^1].X, kept[0].Z - kept[^1].Z).Length() <= 0.05f)
            kept.RemoveAt(kept.Count - 1);
        for (var changed = true; changed && kept.Count > 3;)
        {
            changed = false;
            for (var i = 0; i < kept.Count && kept.Count > 3; i++)
            {
                var (a, b, c) = (kept[(i + kept.Count - 1) % kept.Count], kept[i], kept[(i + 1) % kept.Count]);
                var (ab, bc) = (new Vector2(b.X - a.X, b.Z - a.Z), new Vector2(c.X - b.X, c.Z - b.Z));
                if (Mathf.Abs(ab.Cross(bc)) < 1e-3f * ab.Length() * bc.Length())
                {
                    kept.RemoveAt(i);
                    changed = true;
                    i--;
                }
            }
        }

        return kept;
    }
}
#endif
