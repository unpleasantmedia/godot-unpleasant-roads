using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>Which of a road's barriers: the guard rail along its left or right edge, or the barrier down its middle.</summary>
public enum BarrierSide
{
    Left,
    Right,
    Median,
}

/// <summary>Whether a road has a barrier: on a divided road of its own accord (Auto), or always, or never.</summary>
public enum BarrierMode
{
    Auto,
    On,
    Off,
}

/// <summary>
/// A gap in one of the barriers of the road it is a child of (see Road.GuardRails, Road.MedianBarrier):
/// over Length metres from Start along it, that barrier left open. Its ends can be dragged along the road.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_roads/Icons/Road-Barrier-Gap.svg")]
public partial class RoadBarrierGap : Node3D
{
    private BarrierSide _side = BarrierSide.Median;
    private float _start;
    private float _length = 20f;

    /// <summary>Which barrier it opens.</summary>
    [Export]
    public BarrierSide Side { get => _side; set => Change(ref _side, value); }

    /// <summary>How far along the road it begins.</summary>
    [Export(PropertyHint.Range, "0,10000,0.5,or_greater,suffix:m")]
    public float Start { get => _start; set => Change(ref _start, Mathf.Max(value, 0f)); }

    [Export(PropertyHint.Range, "1,2000,0.5,or_greater,suffix:m")]
    public float Length { get => _length; set => Change(ref _length, Mathf.Max(value, 1f)); }

    public Road? Road => GetParent() as Road;

    public float End => _start + _length;

    public override void _EnterTree() => Road?.QueueRebuild();

    public override void _ExitTree() => Road?.QueueRebuild();

    private void Change<T>(ref T field, T value)
    {
        field = value;
        Road?.QueueRebuild();
        UpdateGizmos();
    }
}
