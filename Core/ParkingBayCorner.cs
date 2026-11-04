using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// A corner of a <see cref="ParkingBayArea"/>'s outline, under its Shape node: the area runs round
/// its corners in the order they are there.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_roads/Icons/Parking-Bay-Corner.svg")]
public partial class ParkingBayCorner : Node3D
{
    private Transform3D? _notifiedAt;

    private ParkingBayArea? Area => GetParent()?.GetParent() as ParkingBayArea;

    public override void _EnterTree()
    {
        SetNotifyTransform(true);
        Area?.Changed();
    }

    public override void _ExitTree() => Area?.Changed();

    public override void _Notification(int what)
    {
        if (what == NotificationTransformChanged && this.Moved(ref _notifiedAt))
            Area?.Changed();
    }
}
