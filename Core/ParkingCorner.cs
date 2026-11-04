using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// A corner of a <see cref="ParkingLot"/>'s outline, under its Shape node: the lot runs round its
/// corners by their <see cref="Order"/>. Its own settings are its rounding and the sidewalk along
/// the side from it to the next corner, and whether it is an entrance.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_roads/Icons/Parking-Corner.svg")]
public partial class ParkingCorner : Node3D
{
    private float _radius;
    private int _order = -1;

    /// <summary>
    /// Where it comes round its lot's outline: the lot joins its corners from the lowest number up,
    /// and back to the first. Its lot numbers its corners 0, 1, 2 and on as they change: a new one
    /// after the corner before it, one given a number already taken just before the corner that
    /// had it, so typing a number puts it there; the others close up round it.
    /// </summary>
    [Export(PropertyHint.Range, "-1,1000,1")]
    public int Order
    {
        get => _order;
        set
        {
            var from = _order;
            _order = value;
            Lot?.Placed(this, value > from && from >= 0);
        }
    }
    private float _sidewalkWidth = RoadCrossSection.SidewalkWidth;
    private Transform3D? _notifiedAt;

    /// <summary>How round the corner is, as the radius of its curve; 0 for a sharp corner. Kept short enough to fit its sides.</summary>
    [Export(PropertyHint.Range, "0,100,0.5,or_greater,suffix:m")]
    public float Radius
    {
        get => _radius;
        set
        {
            _radius = Mathf.Max(0f, value);
            Lot?.QueueRebuild();
        }
    }

    /// <summary>How wide the sidewalk along the side from this corner to the next is; 0 for none.</summary>
    [Export(PropertyHint.Range, "0,10,0.25,suffix:m")]
    public float SidewalkWidth
    {
        get => _sidewalkWidth;
        set
        {
            _sidewalkWidth = Mathf.Max(0f, value);
            Lot?.QueueRebuild();
        }
    }

    private bool _entrance;
    private float _entranceWidth = 6f;

    /// <summary>
    /// Whether it is a way in and out: put on the back of a road's sidewalk, the sidewalk there is
    /// left open as wide as <see cref="EntranceWidth"/>, paved, with a crosswalk across.
    /// </summary>
    [Export]
    public bool Entrance
    {
        get => _entrance;
        set
        {
            _entrance = value;
            Lot?.QueueRebuild();
        }
    }

    /// <summary>How wide its way in and out is, along the road, when it is one.</summary>
    [Export(PropertyHint.Range, "2,30,0.5,suffix:m")]
    public float EntranceWidth
    {
        get => _entranceWidth;
        set
        {
            _entranceWidth = Mathf.Max(1f, value);
            Lot?.QueueRebuild();
        }
    }

    private ParkingLot? Lot => GetParent()?.GetParent() as ParkingLot;

    public override void _EnterTree()
    {
        SetNotifyTransform(true);
        Lot?.QueueRebuild();
    }

    public override void _ExitTree() => Lot?.QueueRebuild();

    public override void _Notification(int what)
    {
        if (what == NotificationTransformChanged && this.Moved(ref _notifiedAt))
            Lot?.QueueRebuild();
    }
}
