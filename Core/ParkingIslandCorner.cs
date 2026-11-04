using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// A corner of a <see cref="ParkingIsland"/>'s outline, under its Shape node: the island runs round
/// its corners in the order they are there.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_roads/Icons/Parking-Island-Corner.svg")]
public partial class ParkingIslandCorner : Node3D
{
    private Transform3D? _notifiedAt;

    private ParkingIsland? Island => GetParent()?.GetParent() as ParkingIsland;

    public override void _EnterTree()
    {
        SetNotifyTransform(true);
        Island?.Changed();
    }

    public override void _ExitTree() => Island?.Changed();

    public override void _Notification(int what)
    {
        if (what == NotificationTransformChanged && this.Moved(ref _notifiedAt))
            Island?.Changed();
    }
}
