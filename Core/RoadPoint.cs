using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// A point a road runs through, placed as a child of its <see cref="Road"/> in the order the road
/// runs. The road curves smoothly on through it towards its neighbours, or, with
/// <see cref="FollowRotation"/>, runs the way the point faces (its -z, as Godot's nodes face).
/// Moving, turning or adding one rebuilds the road.
/// <para>
/// In the editor a smooth point is kept turned the way the road runs through it, so turning it
/// with the rotate tool starts from there; turning it any other way switches FollowRotation on,
/// so the road bends to follow it.
/// </para>
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_roads/Icons/Road-Point.svg")]
public partial class RoadPoint : Node3D
{
    private bool _followRotation;
    private float _handle = 1f / 3f;

    /// <summary>How its road last turned it, while it runs on smoothly through it: none until it has.</summary>
    private Basis? _smoothBasis;

    /// <summary>Whether the road runs the way this point faces, rather than on smoothly through it.</summary>
    [Export]
    public bool FollowRotation
    {
        get => _followRotation;
        set
        {
            _followRotation = value;
            Changed();
        }
    }

    /// <summary>How far the road holds its way either side of the point, as a share of the distance to each neighbour: a third makes an even curve.</summary>
    [Export(PropertyHint.Range, "0.05,1,0.01")]
    public float Handle
    {
        get => _handle;
        set
        {
            _handle = value;
            Changed();
        }
    }

    public override void _Ready()
    {
        _notifiedAt = GlobalTransform;
        SetNotifyTransform(true);
    }

    private Transform3D? _notifiedAt;

    public override void _Notification(int what)
    {
        if (what != NotificationTransformChanged || !this.Moved(ref _notifiedAt))
            return;
        // Turned by hand away from the way its road turned it: the road follows it from now on.
        if (!_followRotation && Engine.IsEditorHint() && _smoothBasis is { } smooth && Turned(GlobalBasis, smooth))
            _followRotation = true;
        Changed();
    }

    /// <summary>Whether one way of facing is turned from another by more than a sliver: which way it faces, or which way is up.</summary>
    private static bool Turned(Basis a, Basis b) =>
        a.Z.Normalized().Dot(b.Z.Normalized()) < 0.99995f || a.Y.Normalized().Dot(b.Y.Normalized()) < 0.99995f;

    /// <summary>
    /// Turns it to face the way its road runs on smoothly through it, if it is smooth, so turning
    /// it by hand starts from the road's own way; called by the road after building.
    /// </summary>
    public void FaceAlong(Vector3 tangent)
    {
        if (_followRotation || tangent.LengthSquared() < 1e-8f)
            return;
        var up = Mathf.Abs(tangent.Normalized().Y) > 0.99f ? Vector3.Forward : Vector3.Up;
        var basis = Basis.LookingAt(tangent, up).Scaled(GlobalBasis.Scale);
        _smoothBasis = basis;
        if (Turned(GlobalBasis, basis))
            GlobalBasis = basis;
    }

    /// <summary>Holds the road through it this way from now on, whatever its neighbours do: FollowRotation, turned to face it.</summary>
    public void Pin(Vector3 tangent)
    {
        if (tangent.LengthSquared() < 1e-8f)
            return;
        _followRotation = true;
        var up = Mathf.Abs(tangent.Normalized().Y) > 0.99f ? Vector3.Forward : Vector3.Up;
        GlobalBasis = Basis.LookingAt(tangent, up).Scaled(GlobalBasis.Scale);
        Changed();
    }

    private void Changed() => (GetParent() as Road)?.QueueRebuild();

    /// <summary>This point as the road's maths takes it, in world space.</summary>
    public CurvePoint ToCurvePoint() => new(
        GlobalPosition.ToNumerics(),
        _followRotation ? (-GlobalBasis.Z).ToNumerics() : null,
        _handle);
}
