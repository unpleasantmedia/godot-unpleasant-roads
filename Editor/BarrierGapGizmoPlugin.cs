#if TOOLS
using System.Collections.Generic;
using Godot;

namespace UnpleasantMedia.UnpleasantRoads.Editor;

/// <summary>
/// A gap in a road's barrier (RoadBarrierGap) in the 3D editor: a line along it where the barrier is left
/// open, and a handle at each end to drag along the road, moving that end (the other staying put). Each
/// drag one step to undo.
/// </summary>
[Tool]
public partial class BarrierGapGizmoPlugin : EditorNode3DGizmoPlugin
{
    /// <summary>How short dragging an end can make it, in metres.</summary>
    private const float Shortest = 1f;

    public BarrierGapGizmoPlugin()
    {
        CreateMaterial("line", new Color(1f, 0.35f, 0.25f));
        CreateHandleMaterial("handles");
    }

    public override string _GetGizmoName() => "Barrier Gap";

    public override bool _HasGizmo(Node3D forNode3D) => forNode3D is RoadBarrierGap;

    public override void _Redraw(EditorNode3DGizmo gizmo)
    {
        gizmo.Clear();
        if (gizmo.GetNode3D() is not RoadBarrierGap gap || gap.Road is not { } road || road.FullFrames() is not { Count: >= 2 } frames)
            return;
        var offset = road.BarrierOffset(gap.Side);
        Vector3 At(float distance) => gap.ToLocal(RoadCurve.At(frames, distance).Across(offset).ToGodot() + Vector3.Up * 1f);

        var line = new List<Vector3>();
        var steps = Mathf.Max(1, Mathf.CeilToInt(gap.Length));
        for (var k = 0; k < steps; k++)
        {
            line.Add(At(gap.Start + gap.Length * k / steps));
            line.Add(At(gap.Start + gap.Length * (k + 1) / steps));
        }

        gizmo.AddLines([.. line], GetMaterial("line", gizmo));
        gizmo.AddHandles([At(gap.Start), At(gap.End)], GetMaterial("handles", gizmo), []);
    }

    public override string _GetHandleName(EditorNode3DGizmo gizmo, int handleId, bool secondary) => handleId == 0 ? "Start" : "End";

    // Where it was before the drag, to undo to: its start and length.
    public override Variant _GetHandleValue(EditorNode3DGizmo gizmo, int handleId, bool secondary) =>
        gizmo.GetNode3D() is RoadBarrierGap gap ? new Vector2(gap.Start, gap.Length) : default;

    public override void _SetHandle(EditorNode3DGizmo gizmo, int handleId, bool secondary, Camera3D camera, Vector2 screenPos)
    {
        if (gizmo.GetNode3D() is not RoadBarrierGap gap || gap.Road is not { } road || road.FullFrames() is not { Count: >= 2 } frames)
            return;
        // Where the mouse points, on the level of the road there; the nearest place along the road to it.
        var from = camera.ProjectRayOrigin(screenPos);
        var way = camera.ProjectRayNormal(screenPos);
        var level = RoadCurve.At(frames, handleId == 0 ? gap.Start : gap.End).Position.Y;
        if (new Plane(Vector3.Up, level).IntersectsRay(from, way) is not { } point)
            return;
        var along = Nearest(frames, point);
        var end = gap.End;
        if (handleId == 0)
        {
            var start = Mathf.Clamp(along, 0f, end - Shortest);
            (gap.Start, gap.Length) = (start, end - start);
        }
        else
        {
            gap.Length = Mathf.Max(along - gap.Start, Shortest);
        }
    }

    public override void _CommitHandle(EditorNode3DGizmo gizmo, int handleId, bool secondary, Variant restore, bool cancel)
    {
        if (gizmo.GetNode3D() is not RoadBarrierGap gap)
            return;
        var was = restore.AsVector2();
        if (cancel)
        {
            (gap.Start, gap.Length) = (was.X, was.Y);
            return;
        }

        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        undo.CreateAction(handleId == 0 ? "Move barrier gap's start" : "Move barrier gap's end");
        undo.AddDoProperty(gap, RoadBarrierGap.PropertyName.Start, gap.Start);
        undo.AddDoProperty(gap, RoadBarrierGap.PropertyName.Length, gap.Length);
        undo.AddUndoProperty(gap, RoadBarrierGap.PropertyName.Start, was.X);
        undo.AddUndoProperty(gap, RoadBarrierGap.PropertyName.Length, was.Y);
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
