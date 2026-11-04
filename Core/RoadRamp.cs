using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// A ramp built into the road it is a child of: over Length metres from Start along the road, on
/// one side, a lane tapers in beside its outer lane, runs alongside, and peels away. An exit takes
/// traffic off in the way that side's traffic goes; an entrance brings it on. The ramp's own road
/// joins it by naming it as its start junction (an exit) or end junction (an entrance): the ramp
/// keeps that road's end on its far end, facing the way its traffic goes.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_roads/Icons/Road-Ramp.svg")]
public partial class RoadRamp : Node3D
{
    private RampKind _kind = RampKind.Exit;
    private RoadSide _side = RoadSide.Right;
    private float _start = 50f;
    private float _length = 120f;
    private float _taper = 40f;
    private float _separation = 12f;
    private float _alongside;
    private float _endAngle;

    /// <summary>Which way it last faced its road's end: turned from there by hand, its end angle follows.</summary>
    private Vector3? _placedFacing;

    /// <summary>Where it last put its road's end: moved from there by hand, the ramp's end follows.</summary>
    private Vector3? _placedEnd;
    private int _lanes = 1;
    private float _laneWidth = 4f;
    private float _shoulderWidth = 1.5f;
    private float _shoulderHeight = 1f;
    private float _shoulderAngle = RoadCrossSection.WalkableAngle;

    [Export]
    public RampKind Kind { get => _kind; set => Change(ref _kind, value); }

    /// <summary>Which side of the road, looking along it from its first point.</summary>
    [Export]
    public RoadSide Side { get => _side; set => Change(ref _side, value); }

    /// <summary>Whether a guard rail runs along its outer edge (away from the road): as its road's (Auto), always, or never.</summary>
    [Export]
    public BarrierMode GuardRails { get => _guardRails; set => Change(ref _guardRails, value); }

    private BarrierMode _guardRails;

    /// <summary>How far along the road the ramp's stretch begins.</summary>
    [Export(PropertyHint.Range, "0,10000,0.5,or_greater,suffix:m")]
    public float Start { get => _start; set => Change(ref _start, value); }

    [Export(PropertyHint.Range, "20,500,1,suffix:m")]
    public float Length { get => _length; set => Change(ref _length, value); }

    /// <summary>How long its lane takes to widen in from nothing, at the end where it joins the road.</summary>
    [Export(PropertyHint.Range, "5,200,1,suffix:m")]
    public float Taper { get => _taper; set => Change(ref _taper, value); }

    /// <summary>
    /// How long its lane runs at full width beside the road, between its taper and where it splits
    /// off. Changing it on a ramp in the scene lengthens or shortens the ramp to match, so the
    /// split keeps its shape (not while the scene loads, when Length is already saved to match).
    /// </summary>
    [Export(PropertyHint.Range, "0,1000,1,or_greater,suffix:m")]
    public float ExitLaneLength
    {
        get => _alongside;
        set
        {
            // Not as its saved settings are put back: as the scene loads, or as the scripts are
            // reloaded (in the tree, its Length already put back to match).
            if (IsInsideTree() && _settled)
                _length = Mathf.Max(_length + value - _alongside, 20f);
            Change(ref _alongside, value);
        }
    }

    /// <summary>
    /// Whether its saved settings have all been put back, for a change to Exit Lane Length from
    /// then on to be one by hand. Made anew (false) when the scripts are reloaded, and true again
    /// at the end of that frame, once they have been.
    /// </summary>
    private bool _settled;

    public RoadRamp() => new Callable(this, MethodName.Settle).CallDeferred();

    private void Settle() => _settled = true;

    /// <summary>How far out from the road's lane edge the middle of the ramp's lanes ends up.</summary>
    [Export(PropertyHint.Range, "3,60,0.5,suffix:m")]
    public float Separation { get => _separation; set => Change(ref _separation, value); }

    /// <summary>
    /// How far its lanes head away from the road where they end, in degrees: none runs alongside
    /// it again. Turning the ramp, or its road's end, sets it. It bends no more sharply than its
    /// separation and swing allow.
    /// </summary>
    [Export(PropertyHint.Range, "0,80,0.5,degrees")]
    public float EndAngle { get => _endAngle; set => Change(ref _endAngle, value); }

    [Export(PropertyHint.Range, "1,3,1")]
    public int Lanes { get => _lanes; set => Change(ref _lanes, value); }

    [Export(PropertyHint.Range, "2,6,0.05,suffix:m")]
    public float LaneWidth { get => _laneWidth; set => Change(ref _laneWidth, value); }


    /// <summary>How wide its paved shoulders are, before the banks.</summary>
    [ExportGroup("Banks")]
    [Export(PropertyHint.Range, "0,5,0.05,suffix:m")]
    public float ShoulderWidth { get => _shoulderWidth; set => Change(ref _shoulderWidth, value); }

    /// <summary>How far the banks past its shoulders drop from it (negative: rise), as they slope away at the shoulder angle.</summary>
    [Export(PropertyHint.Range, "-30,30,0.1,or_greater,or_less,suffix:m")]
    public float ShoulderHeight { get => _shoulderHeight; set => Change(ref _shoulderHeight, value); }

    /// <summary>How steep its banks are. The default is the steepest the player can walk up.</summary>
    [Export(PropertyHint.Range, "5,85,0.5,degrees")]
    public float ShoulderAngle { get => _shoulderAngle; set => Change(ref _shoulderAngle, value); }

    public Road? Road => GetParent() as Road;

    public RampSpec Spec => new(_start, _length, _side, _kind, _lanes, _laneWidth, _shoulderWidth, _taper, _separation, _shoulderHeight, _shoulderAngle, _alongside, _endAngle);

    /// <summary>Where its own road meets it, facing the way that road's traffic goes; none without a road to be on.</summary>
    /// <remarks>Its road's whole centre line may be given, when already worked out, rather than worked out again.</remarks>
    public RoadFrame? BranchEnd(IReadOnlyList<RoadFrame>? roadFrames = null)
    {
        if (Road is not { } road)
            return null;
        var frames = roadFrames ?? road.FullFrames();
        if (frames.Count < 2)
            return null;
        return RoadRampShaper.BranchEnd(frames, Spec, road.CrossSection);
    }

    /// <summary>
    /// Puts the end of the ramp's own road on its far end, facing along it; called when the road
    /// it is built into has been built again, with that road's whole centre line and the network's
    /// roads, if already found.
    /// </summary>
    public void Refresh(IReadOnlyList<RoadFrame>? roadFrames = null, IReadOnlyList<Road>? roads = null)
    {
        if (BranchEnd(roadFrames) is not { } end || Road?.Network is not { } network)
            return;
        // It sits where its road meets it, so selecting and framing it shows the ramp.
        _placedEnd = end.Position.ToGodot();
        var facing = end.Forward.ToGodot();
        _placedFacing = facing;
        var turned = Basis.LookingAt(facing, Vector3.Up);
        if (!GlobalPosition.Near(end.Position.ToGodot()) || !GlobalBasis.IsEqualApprox(turned))
            GlobalTransform = new Transform3D(turned, end.Position.ToGodot());
        foreach (var branch in roads ?? network.Roads)
        {
            var points = branch.Points;
            if (points.Count == 0)
                continue;
            var point = branch.StartJunction == this ? points[0] : branch.EndJunction == this ? points[^1] : null;
            if (point is null)
                continue;
            var at = end.Position.ToGodot();
            _placedEnd = at;
            var basis = Basis.LookingAt(facing, Vector3.Up);
            if (!point.GlobalPosition.Near(at) || !point.GlobalBasis.IsEqualApprox(basis))
                point.GlobalTransform = new Transform3D(basis, at);
            point.FollowRotation = true;

            // Its road is as wide as it is, lane for lane, shoulders and banks alike, so they meet flush.
            if (branch.ForwardLanes != _lanes)
                branch.ForwardLanes = _lanes;
            if (branch.BackwardLanes != 0)
                branch.BackwardLanes = 0;
            if (!Mathf.IsEqualApprox(branch.LaneWidth, _laneWidth))
                branch.LaneWidth = _laneWidth;
            if (!Mathf.IsEqualApprox(branch.ShoulderWidth, _shoulderWidth))
                branch.ShoulderWidth = _shoulderWidth;
            if (!Mathf.IsEqualApprox(branch.ShoulderHeight, _shoulderHeight))
                branch.ShoulderHeight = _shoulderHeight;
            if (!Mathf.IsEqualApprox(branch.ShoulderAngle, _shoulderAngle))
                branch.ShoulderAngle = _shoulderAngle;
        }
    }

    /// <summary>
    /// Its road's end, if moved by hand in the editor from where it put it, takes its own end
    /// with it: along the road makes it longer or shorter at that end, out from the road swings
    /// it further out (its separation). It then puts the road's end back exactly on its own.
    /// </summary>
    public void Follow(RoadPoint point)
    {
        MoveEndTo(point.GlobalPosition);
        TurnEndTo(-point.GlobalBasis.Z);
    }

    public override void _Ready()
    {
        _notifiedAt = GlobalTransform;
        SetNotifyTransform(true);
    }

    private Transform3D? _notifiedAt;

    /// <summary>Moved by hand in the editor (it sits at its end), it moves its end there too.</summary>
    public override void _Notification(int what)
    {
        if (what != NotificationTransformChanged || !IsInsideTree() || !this.Moved(ref _notifiedAt))
            return;
        MoveEndTo(GlobalPosition);
        TurnEndTo(-GlobalBasis.Z);
    }

    /// <summary>Moves its end (where its road meets it) towards a place, by how far that is from where it was.</summary>
    private void MoveEndTo(Vector3 to)
    {
        if (!Engine.IsEditorHint() || _placedEnd is not { } placed || Road is not { } road)
            return;
        var moved = to - placed;
        if (moved.LengthSquared() < 1e-4f)
            return;
        var frames = road.FullFrames();
        if (frames.Count < 2)
            return;

        var separatedAtStart = RoadRampShaper.SeparatedAtStart(Spec, road.CrossSection);
        var frame = RoadCurve.At(frames, separatedAtStart ? _start : _start + _length);
        var along = moved.Dot(frame.Forward.ToGodot());
        var outward = moved.Dot(frame.Right.ToGodot()) * (_side == RoadSide.Right ? 1f : -1f);
        _placedEnd = to;

        // Longer or shorter at its separated end: that end moves along, the joined end stays.
        var length = Mathf.Max(_length + (separatedAtStart ? -along : along), _taper + _alongside + 10f);
        if (separatedAtStart)
            _start += _length - length;
        _length = length;
        _separation = Mathf.Clamp(_separation + outward, Width / 2f + 1f, 60f);
        road.QueueRebuild();
    }

    /// <summary>
    /// Turned by hand in the editor from the way it faced its road's end, its lanes head away from
    /// the road at the angle it now faces: out from the road's way, on its side.
    /// </summary>
    private void TurnEndTo(Vector3 facing)
    {
        if (!Engine.IsEditorHint() || _placedFacing is not { } placed || Road is not { } road)
            return;
        var level = new Vector3(facing.X, 0f, facing.Z);
        var was = new Vector3(placed.X, 0f, placed.Z);
        if (level.LengthSquared() < 1e-6f || was.LengthSquared() < 1e-6f || level.Normalized().Dot(was.Normalized()) > 0.99995f)
            return;
        var frames = road.FullFrames();
        if (frames.Count < 2)
            return;
        _placedFacing = facing;

        var separatedAtStart = RoadRampShaper.SeparatedAtStart(Spec, road.CrossSection);
        var frame = RoadCurve.At(frames, separatedAtStart ? _start : _start + _length);
        // The way away from the ramp along the road, and out from it on its side.
        var away = (separatedAtStart ? -frame.Forward : frame.Forward).ToGodot() with { Y = 0f };
        var outward = frame.Right.ToGodot() * (_side == RoadSide.Right ? 1f : -1f);
        // Its road runs away from it for an exit, in towards it for an entrance.
        var heading = (_kind == RampKind.Exit ? level : -level).Normalized();
        var angle = Mathf.RadToDeg(Mathf.Atan2(heading.Dot(outward), heading.Dot(away.Normalized())));
        _endAngle = Mathf.Clamp(angle, 0f, 80f);
        road.QueueRebuild();
    }

    private float Width => _lanes * _laneWidth;

    private void Change<T>(ref T field, T value)
    {
        field = value;
        Road?.QueueRebuild();
    }
}
