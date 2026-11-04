#if TOOLS
using Godot;

namespace UnpleasantMedia.UnpleasantRoads.Editor;

/// <summary>
/// Lets a road be clicked in the 3D editor to select it: its surface and banks are its gizmo's
/// shape for picking. It draws nothing itself.
/// </summary>
[Tool]
public partial class RoadGizmoPlugin : EditorNode3DGizmoPlugin
{
    public override string _GetGizmoName() => "Road";

    public override bool _HasGizmo(Node3D forNode3D) => forNode3D is Road;

    public override void _Redraw(EditorNode3DGizmo gizmo)
    {
        gizmo.Clear();
        if (gizmo.GetNode3D() is not Road road || road.PickFaces() is not { Length: >= 3 } faces)
            return;
        var mesh = new TriangleMesh();
        if (mesh.CreateFromFaces(faces))
            gizmo.AddCollisionTriangles(mesh);
    }
}
#endif
