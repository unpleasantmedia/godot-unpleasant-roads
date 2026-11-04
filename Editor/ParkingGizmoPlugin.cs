#if TOOLS
using System.Collections.Generic;
using Godot;

namespace UnpleasantMedia.UnpleasantRoads.Editor;

/// <summary>
/// A road's parking (RoadParking) in the 3D editor: a line along its strip, and a handle at each end
/// to drag along the road, moving that end (the other staying put). Each drag one step to undo.
/// </summary>
[Tool]
public partial class ParkingGizmoPlugin : EditorNode3DGizmoPlugin
{
    /// <summary>How short dragging an end can make it, in metres.</summary>
    private const float Shortest = 5f;

    public ParkingGizmoPlugin()
    {
        CreateMaterial("line", new Color(1f, 0.8f, 0.2f));
        CreateHandleMaterial("handles");
    }

    public override string _GetGizmoName() => "Road Parking";

    public override bool _HasGizmo(Node3D forNode3D) => forNode3D is RoadParking;

    public override void _Redraw(EditorNode3DGizmo gizmo)
    {
        gizmo.Clear();
        if (gizmo.GetNode3D() is not RoadParking parking || parking.Road is not { } road || road.FullFrames() is not { Count: >= 2 } frames)
            return;
        var offset = parking.MiddleOffset();
        Vector3 At(float distance) => parking.ToLocal(RoadCurve.At(frames, distance).Across(offset).ToGodot() + Vector3.Up * 0.3f);

        // Along its strip, a point each metre or so.
        var line = new List<Vector3>();
        var steps = Mathf.Max(1, Mathf.CeilToInt(parking.Length));
        for (var k = 0; k < steps; k++)
        {
            line.Add(At(parking.Start + parking.Length * k / steps));
            line.Add(At(parking.Start + parking.Length * (k + 1) / steps));
        }

        gizmo.AddLines([.. line], GetMaterial("line", gizmo));
        gizmo.AddHandles([At(parking.Start), At(parking.Start + parking.Length)], GetMaterial("handles", gizmo), []);
    }

    public override string _GetHandleName(EditorNode3DGizmo gizmo, int handleId, bool secondary) => handleId == 0 ? "Start" : "End";

    // Where it was before the drag, to undo to: its start and length.
    public override Variant _GetHandleValue(EditorNode3DGizmo gizmo, int handleId, bool secondary) =>
        gizmo.GetNode3D() is RoadParking parking ? new Vector2(parking.Start, parking.Length) : default;

    public override void _SetHandle(EditorNode3DGizmo gizmo, int handleId, bool secondary, Camera3D camera, Vector2 screenPos)
    {
        if (gizmo.GetNode3D() is not RoadParking parking || parking.Road is not { } road || road.FullFrames() is not { Count: >= 2 } frames)
            return;
        // Where the mouse points, on the level of the road there; the nearest place along the road to it.
        var from = camera.ProjectRayOrigin(screenPos);
        var way = camera.ProjectRayNormal(screenPos);
        var level = RoadCurve.At(frames, handleId == 0 ? parking.Start : parking.Start + parking.Length).Position.Y;
        if (new Plane(Vector3.Up, level).IntersectsRay(from, way) is not { } point)
            return;
        var along = Nearest(frames, point);
        var end = parking.Start + parking.Length;
        if (handleId == 0)
        {
            var start = Mathf.Clamp(along, 0f, end - Shortest);
            (parking.Start, parking.Length) = (start, end - start);
        }
        else
        {
            parking.Length = Mathf.Max(along - parking.Start, Shortest);
        }

        parking.UpdateGizmos();
    }

    public override void _CommitHandle(EditorNode3DGizmo gizmo, int handleId, bool secondary, Variant restore, bool cancel)
    {
        if (gizmo.GetNode3D() is not RoadParking parking)
            return;
        var was = restore.AsVector2();
        if (cancel)
        {
            (parking.Start, parking.Length) = (was.X, was.Y);
            return;
        }

        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        undo.CreateAction(handleId == 0 ? "Move parking's start" : "Move parking's end");
        undo.AddDoProperty(parking, RoadParking.PropertyName.Start, parking.Start);
        undo.AddDoProperty(parking, RoadParking.PropertyName.Length, parking.Length);
        undo.AddUndoProperty(parking, RoadParking.PropertyName.Start, was.X);
        undo.AddUndoProperty(parking, RoadParking.PropertyName.Length, was.Y);
        undo.CommitAction();
    }

    /// <summary>How far along the road the place on it nearest a point is (seen from above).</summary>
    private static float Nearest(IReadOnlyList<RoadFrame> frames, Vector3 point)
    {
        var (best, distance) = (float.MaxValue, 0f);
        var at = new Vector2(point.X, point.Z);
        for (var i = 0; i + 1 < frames.Count; i++)
        {
            var (a, b) = (new Vector2(frames[i].Position.X, frames[i].Position.Z), new Vector2(frames[i + 1].Position.X, frames[i + 1].Position.Z));
            var run = b - a;
            var t = run.LengthSquared() < 1e-6f ? 0f : Mathf.Clamp((at - a).Dot(run) / run.LengthSquared(), 0f, 1f);
            var off = (a + run * t).DistanceTo(at);
            if (off < best)
                (best, distance) = (off, Mathf.Lerp(frames[i].Distance, frames[i + 1].Distance, t));
        }

        return distance;
    }
}
#endif
