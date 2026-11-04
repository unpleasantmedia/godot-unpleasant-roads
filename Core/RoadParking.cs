using System;
using System.Collections.Generic;
using Godot;
using V3 = System.Numerics.Vector3;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>How a road's parking bays are laid out along its side.</summary>
public enum ParkingKind
{
    /// <summary>A parking lane along the kerb, square at its ends: cars end to end, parallel to the road.</summary>
    Parallel,

    /// <summary>The same, set into the sidewalk, which bends out round it and back: tapered at its ends.</summary>
    LayBy,

    /// <summary>Bays at an angle to the road (see <see cref="RoadParking.Angle"/>), the kerb set back as deep as they reach.</summary>
    Angled,

    /// <summary>Bays square to the road, the kerb set back a car's length.</summary>
    Perpendicular,
}

/// <summary>
/// A road's parking, as the road maths needs it: its kind, side, where along the road it runs, how
/// big its bays are and (angled) at what angle; how far out its side of the road is set at each place.
/// </summary>
public readonly record struct ParkingSpec(ParkingKind Kind, RoadSide Side, float Start, float Length, float BayWidth, float BayLength, float Angle, float Offset = 0f)
{
    public float End => Start + Length;

    /// <summary>How far out from the road's edge its bays reach (and the kerb is set back).</summary>
    public float Depth => Kind switch
    {
        ParkingKind.Angled => BayLength * MathF.Sin(Radians) + BayWidth * MathF.Cos(Radians),
        ParkingKind.Perpendicular => BayLength,
        _ => BayWidth,
    };

    private float Radians => Math.Clamp(Angle, 10f, 90f) * MathF.PI / 180f;

    /// <summary>How long its ends take to come out from the road's edge to its full depth: a lay-by's taper, others nearly square.</summary>
    public float Taper => Kind == ParkingKind.LayBy ? MathF.Min(Depth * 2.5f, Length / 3f) : MathF.Min(0.3f, Length / 3f);

    /// <summary>How far out its side of the road is set at a distance along the road: none outside it, easing in and out at its ends.</summary>
    public float DepthAt(float distance)
    {
        if (distance <= Start || distance >= End)
            return 0f;
        var t = Math.Clamp(MathF.Min(distance - Start, End - distance) / Taper, 0f, 1f);
        return Depth * t * t * (3f - 2f * t);
    }

    /// <summary>
    /// How far out the far side of the sidewalk beside it is set at a distance along the road, for a
    /// sidewalk so wide. A lay-by's sidewalk bends round it as its kerb does. Any other's runs on past
    /// each end by the sidewalk's width at the full depth, so the sidewalk there is wide enough to
    /// turn in along the end bay's side and meet the sidewalk behind the bays.
    /// </summary>
    public float SidewalkDepthAt(float distance, float sidewalk)
    {
        if (Kind == ParkingKind.LayBy)
            return DepthAt(distance);
        var (start, end) = (Start - sidewalk, End + sidewalk);
        if (distance <= start || distance >= end)
            return 0f;
        var t = Math.Clamp(MathF.Min(distance - start, end - distance) / Taper, 0f, 1f);
        return Depth * t * t * (3f - 2f * t);
    }

    /// <summary>How far apart its bays are along the road.</summary>
    public float Pitch => Kind switch
    {
        ParkingKind.Angled => BayWidth / MathF.Sin(Radians),
        ParkingKind.Perpendicular => BayWidth,
        _ => BayLength,
    };

    /// <summary>Where its painted lines between bays are along the road, from where it is at full depth to where it leaves it.</summary>
    public IEnumerable<float> Lines()
    {
        var (from, to) = (Start + Taper, End - Taper);
        // Angled bays lean along the road: their lines start back by as much, so the last still fits.
        var lean = Kind == ParkingKind.Angled ? Depth / MathF.Tan(Radians) : 0f;
        var count = (int)MathF.Floor((to - from - lean) / Pitch);
        if (count < 1)
            yield break;
        // Middled along it, then shifted by its offset (a bay's pitch round, so any shift fits): as many as then fit.
        var middled = from + (to - from - lean - count * Pitch) / 2f;
        var first = from + PosMod(middled - from + Offset, Pitch);
        for (var at = first; at + lean <= to + 1e-3f; at += Pitch)
            yield return at;
    }

    private static float PosMod(float value, float step) => value - step * MathF.Floor(value / step);

    /// <summary>The way a line between bays runs out from the road's edge, as a share along the road and a share out from it.</summary>
    public (float Along, float Out) LineWay => Kind == ParkingKind.Angled ? (MathF.Cos(Radians), MathF.Sin(Radians)) : (0f, 1f);
}

/// <summary>
/// Parking along one side of the road it is a child of, over Length metres from Start along it: a
/// parking lane, a lay-by set into the sidewalk, or angled or square bays, the sidewalk (or bank) set
/// back round them and the bays painted. The road builds it in (see RoadMeshBuilder). Its bays are
/// known to the game as parking spots (see <see cref="Bays"/>).
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_roads/Icons/Road-Parking.svg")]
public partial class RoadParking : Node3D
{
    private ParkingKind _kind = ParkingKind.Parallel;
    private RoadSide _side = RoadSide.Right;
    private float _start;
    private float _length = 30f;
    private float _bayWidth = 2.5f;
    private float _bayLength = 6f;
    private float _angle = 60f;

    [Export]
    public ParkingKind Kind
    {
        get => _kind;
        set
        {
            // A bay's length as for its kind, unless set otherwise: end to end, or nose in.
            if ((value is ParkingKind.Parallel or ParkingKind.LayBy) != (_kind is ParkingKind.Parallel or ParkingKind.LayBy))
                _bayLength = value is ParkingKind.Parallel or ParkingKind.LayBy ? 6f : 5f;
            Change(ref _kind, value);
            NotifyPropertyListChanged();
        }
    }

    /// <summary>Which side of the road it is on, looking along it.</summary>
    [Export]
    public RoadSide Side { get => _side; set => Change(ref _side, value); }

    /// <summary>How far along the road it begins.</summary>
    [Export(PropertyHint.Range, "0,10000,0.5,or_greater,suffix:m")]
    public float Start { get => _start; set => Change(ref _start, Mathf.Max(value, 0f)); }

    [Export(PropertyHint.Range, "5,2000,0.5,or_greater,suffix:m")]
    public float Length { get => _length; set => Change(ref _length, Mathf.Max(value, 5f)); }

    /// <summary>How wide a bay is (a car's width and room either side).</summary>
    [Export(PropertyHint.Range, "1.8,5,0.05,suffix:m")]
    public float BayWidth { get => _bayWidth; set => Change(ref _bayWidth, value); }

    /// <summary>How long a bay is (a car's length and room).</summary>
    [Export(PropertyHint.Range, "3,10,0.05,suffix:m")]
    public float BayLength { get => _bayLength; set => Change(ref _bayLength, value); }

    private float _offset;

    /// <summary>How far along the road its bay lines are shifted from where they would be (middled along it).</summary>
    [Export(PropertyHint.Range, "-20,20,0.05,or_less,or_greater,suffix:m")]
    public float BayOffset { get => _offset; set => Change(ref _offset, value); }

    /// <summary>For angled bays: the angle between them and the road.</summary>
    [Export(PropertyHint.Range, "15,80,1,suffix:°")]
    public float Angle { get => _angle; set => Change(ref _angle, value); }

    public override void _ValidateProperty(Godot.Collections.Dictionary property)
    {
        if (property["name"].AsStringName() == PropertyName.Angle && _kind != ParkingKind.Angled)
            property["usage"] = (int)PropertyUsageFlags.Storage;
    }

    public Road? Road => GetParent() as Road;

    public ParkingSpec Spec => new(_kind, _side, _start, _length, _bayWidth, _bayLength, _angle, _offset);

    /// <summary>
    /// Its bays, in the world: each one's middle (on the road's surface), the way into it (from the
    /// road, as a car drives in), and how wide and long it is. For the game to know where it can park.
    /// </summary>
    public IReadOnlyList<(Vector3 Middle, Vector3 Facing, float Width, float Length)> Bays()
    {
        var bays = new List<(Vector3, Vector3, float, float)>();
        if (Road is not { } road || road.FullFrames() is not { Count: >= 2 } frames)
            return bays;
        var spec = Spec;
        var lines = new List<float>(spec.Lines());
        var edge = Edge(road);
        var (along, outward) = spec.LineWay;
        for (var k = 0; k + 1 < lines.Count; k++)
        {
            var at = (lines[k] + lines[k + 1]) / 2f;
            var frame = RoadCurve.At(frames, at);
            var sign = spec.Side == RoadSide.Right ? 1f : -1f;
            var flatOut = V3.Normalize(new V3(frame.Right.X, 0f, frame.Right.Z)) * sign;
            var forward = V3.Normalize(new V3(frame.Forward.X, 0f, frame.Forward.Z));
            var way = forward * along + flatOut * outward;
            // Its middle half its depth out along its lines, from the middle of its edge on the road.
            var middle = frame.Across(edge * sign) + way * (spec.Depth / MathF.Max(outward, 0.1f) / 2f);
            var (width, length) = (spec.BayWidth, spec.BayLength);
            var facing = spec.Kind is ParkingKind.Parallel or ParkingKind.LayBy ? forward : way;
            bays.Add((middle.ToGodot(), facing.ToGodot(), width, length));
        }

        return bays;
    }

    /// <summary>How far right of the road's middle the middle of its parking strip is (left, less than nought): where its handles are.</summary>
    public float MiddleOffset() => Road is { } road ? (_side == RoadSide.Right ? 1f : -1f) * (Edge(road) + Spec.Depth / 2f) : 0f;

    /// <summary>How far from the road's middle its side's paved edge is (where its parking starts).</summary>
    private float Edge(Road road)
    {
        var bands = road.CrossSection.Bands();
        var paved = RoadMeshBuilder.PavedRuns(road.CrossSection);
        float? edge = null;
        foreach (var (left, right) in paved)
            edge = _side == RoadSide.Right ? MathF.Max(edge ?? right, right) : MathF.Min(edge ?? left, left);
        return MathF.Abs(edge ?? (bands.Count > 0 ? (_side == RoadSide.Right ? bands[^1].Right : bands[0].Left) : 0f));
    }

    /// <summary>Sits where its middle is, so selecting and framing it shows it; called when its road has been built again.</summary>
    public void Refresh(IReadOnlyList<RoadFrame>? roadFrames = null)
    {
        if (Road is not { } road)
            return;
        var frames = roadFrames ?? road.FullFrames();
        if (frames.Count < 2)
            return;
        var at = RoadCurve.At(frames, _start + _length / 2f).Position.ToGodot();
        if (!GlobalPosition.Near(at))
            GlobalPosition = at;
    }

    private void Change<T>(ref T field, T value)
    {
        field = value;
        Road?.QueueRebuild();
    }
}
