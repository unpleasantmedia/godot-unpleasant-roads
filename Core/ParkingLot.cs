using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using V3 = System.Numerics.Vector3;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>What is painted on a parking lot.</summary>
public enum ParkingMarkings
{
    /// <summary>Rows of parking bays, laid out to fill it, with aisles between.</summary>
    Bays,

    /// <summary>Plain asphalt.</summary>
    Plain,
}

/// <summary>
/// A parking lot (ParkingLot.tscn): asphalt inside the outline of its <see cref="ParkingCorner"/>s
/// (each rounded as much as it says), at each corner's height and blending smoothly between them, with a sidewalk along each side
/// as wide as that side's corner says. It belongs under a <see cref="RoadNetwork"/>. A road whose
/// end is put on its edge joins it there: the sidewalk opens for the road, and traffic drives in
/// from it and out along any road joined to it. Its bays, when it has them, run along its own x
/// axis: turning it turns its rows.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_roads/Icons/ParkingLot.svg")]
public partial class ParkingLot : Node3D, IRoadOutlined
{
    /// <summary>
    /// Whether its saved settings have all been put back since it was made: false from when the
    /// scripts are reloaded (it is made anew, already in the tree and built) to the end of that
    /// frame. Settings put back then are not changes, and rebuild nothing: rebuilding everything
    /// after each reload froze the editor for a minute.
    /// </summary>
    private bool _reloadSettled;

    public ParkingLot() => new Callable(this, MethodName.ReloadSettle).CallDeferred();

    private void ReloadSettle() => _reloadSettled = true;

    /// <summary>Whether a setting is being put back as the scripts are reloaded, rather than changed.</summary>
    private bool Reloading => IsInsideTree() && !_reloadSettled;

    /// <summary>The group every parking lot is in.</summary>
    public const string Group = "parking_lots";

    private ParkingMarkings _markings = ParkingMarkings.Bays;
    private float _bayWidth = 2.5f;
    private float _bayDepth = 5f;
    private float _aisleWidth = 6.5f;
    private bool _rebuildQueued;
    private Transform3D? _notifiedAt;
    private MeshInstance3D? _mesh;
    private StaticBody3D? _body;

    /// <summary>Whether it has parking bays painted on it, or is plain.</summary>
    [Export]
    public ParkingMarkings Markings { get => _markings; set => Change(ref _markings, value); }

    /// <summary>How wide each bay is, along its row.</summary>
    [Export(PropertyHint.Range, "1.5,5,0.05,suffix:m")]
    public float BayWidth { get => _bayWidth; set => Change(ref _bayWidth, Mathf.Max(value, 1f)); }

    /// <summary>How deep each bay is, from the aisle in.</summary>
    [Export(PropertyHint.Range, "3,10,0.1,suffix:m")]
    public float BayDepth { get => _bayDepth; set => Change(ref _bayDepth, Mathf.Max(value, 1f)); }

    /// <summary>
    /// How far its bay lines are shifted from where they would start: x along the rows (a bay's
    /// width round, so any shift fits), y across them (the rows moved in from the edge they start at).
    /// </summary>
    [Export(PropertyHint.None, "suffix:m")]
    public Vector2 BayOffset { get => _bayOffset; set => Change(ref _bayOffset, value); }

    private Vector2 _bayOffset;

    /// <summary>How far its bay lines are turned from its own x axis, in degrees (seen from above).</summary>
    [Export(PropertyHint.Range, "-180,180,0.5,suffix:°")]
    public float BayAngle { get => _bayAngle; set => Change(ref _bayAngle, value); }

    private float _bayAngle;

    /// <summary>How wide the aisles between rows of bays are, for driving along.</summary>
    [Export(PropertyHint.Range, "3,15,0.1,suffix:m")]
    public float AisleWidth { get => _aisleWidth; set => Change(ref _aisleWidth, Mathf.Max(value, 1f)); }

    /// <summary>
    /// Its corners (entrances among them), in the order its outline runs round them: by their
    /// <see cref="ParkingCorner.Order"/>, lowest first; any with none yet, or the same, as they come
    /// among its children.
    /// </summary>
    public IReadOnlyList<ParkingCorner> Corners
    {
        get
        {
            var children = CornerChildren().ToList();
            // A lot saved with its order as a list of names, not yet numbered: in that order.
            if (_cornerOrder.Count > 0)
            {
                var names = _cornerOrder.Select(n => n.ToString()).ToList();
                return children.OrderBy(c => names.IndexOf(c.Name) is var at and >= 0 ? at : int.MaxValue).ToList();
            }

            return children.OrderBy(c => c.Order < 0 ? int.MaxValue : c.Order).ThenBy(c => c == _placed == _placedLater ? 1 : 0).ToList();
        }
    }

    /// <summary>
    /// The corner last given a number by hand: where that number is, among any other with it until
    /// they are numbered again; before it, or after it if it was moved on round (so it lands on it).
    /// </summary>
    private ParkingCorner? _placed;
    private bool _placedLater;
    private bool _numbering;

    /// <summary>A corner given a number (by hand, not by <see cref="NumberCorners"/>): the lot built again, it first among any sharing it.</summary>
    public void Placed(ParkingCorner corner, bool later)
    {
        if (!_numbering)
            (_placed, _placedLater) = (corner, later);
        QueueRebuild();
    }

    private Godot.Collections.Array<StringName> _cornerOrder = new();

    /// <summary>
    /// Its corners' names in order, as lots kept it before each corner had its own
    /// <see cref="ParkingCorner.Order"/>: read from scenes saved so, turned into their corners'
    /// numbers when it is ready, and then emptied (and so no longer saved). Not shown.
    /// </summary>
    [Export]
    public Godot.Collections.Array<StringName> CornerOrder
    {
        get => _cornerOrder;
        set
        {
            _cornerOrder = value ?? new();
            QueueRebuild();
        }
    }

    public override void _ValidateProperty(Godot.Collections.Dictionary property)
    {
        if (property["name"].AsStringName() == PropertyName.CornerOrder)
            property["usage"] = (int)PropertyUsageFlags.Storage;
        // Worked out, never set: shown, not saved.
        else if (property["name"].AsStringName() == PropertyName.StreetName)
            property["usage"] = (int)(PropertyUsageFlags.Editor | PropertyUsageFlags.ReadOnly);
    }

    /// <summary>
    /// The street it is on: that of the road at its first entrance (in its corners' order), a road joined
    /// to it or one whose sidewalk an entrance corner opens. An unnamed road there is followed on through
    /// its junctions to the nearest named road. Empty with none.
    /// </summary>
    [Export]
    public string StreetName
    {
        get
        {
            // Worked out at most once a second: the Inspector asks often, and looking round the roads is slow.
            var now = Godot.Time.GetTicksMsec();
            if (_streetNameAt is not { } at || now - at > 1000)
                (_streetName, _streetNameAt) = (WorkOutStreetName(), now);
            return _streetName;
        }
        set { }
    }

    private string _streetName = "";
    private ulong? _streetNameAt;

    private string WorkOutStreetName()
    {
        // Each entrance corner in turn round it: the road it opens onto, or the road joined nearest it.
        var joined = Entrances();
        foreach (var gate in Corners.Where(c => c.Entrance).Select(c => c.GlobalPosition))
        {
            if (Network?.Roads.FirstOrDefault(r => r.OpensAt(gate.ToNumerics())) is { } opened)
                return NamedFrom(opened);
            if (joined.Count > 0)
            {
                var near = joined.MinBy(e => e.Road.Points is { Count: > 0 } points
                    ? gate.DistanceSquaredTo(e.Start ? points[0].GlobalPosition : points[^1].GlobalPosition) : float.MaxValue);
                return NamedFrom(near.Road);
            }
        }

        return joined.Count > 0 ? NamedFrom(joined[0].Road) : "";
    }

    /// <summary>A road's street name; for an unnamed one, the nearest named road's on through its junctions (fewest roads between).</summary>
    private static string NamedFrom(Road road)
    {
        var seen = new HashSet<Road> { road };
        var queue = new Queue<Road>();
        queue.Enqueue(road);
        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            if (!string.IsNullOrWhiteSpace(at.StreetName))
                return at.StreetName.Trim();
            foreach (var end in new[] { at.StartJunction, at.EndJunction })
            {
                var next = end switch
                {
                    RoadJunction junction => junction.Arms().Select(a => a.Road),
                    ParkingLot lot => lot.Entrances().Select(e => e.Road),
                    RoadRamp { Road: { } from } => new[] { from },
                    _ => Enumerable.Empty<Road>(),
                };
                foreach (var other in next)
                {
                    if (seen.Add(other))
                        queue.Enqueue(other);
                }
            }
        }

        return "";
    }

    /// <summary>
    /// Its corners numbered 0, 1, 2 and on round its outline, as they are in order now: one with no
    /// number yet (just added) after the corner before it among its children, one sharing a number
    /// (just duplicated) after the other. A lot saved with its order as a list of names is
    /// numbered from that list, and the list emptied.
    /// </summary>
    public void NumberCorners()
    {
        // Not as it closes, its corners going one by one; nor before it is ready.
        if (_closing || _readyAt == 0)
            return;
        var children = CornerChildren().ToList();
        var order = Corners.Where(c => c.Order >= 0 || _cornerOrder.Count > 0).ToList();
        foreach (var corner in children.Where(c => !order.Contains(c)))
        {
            var before = children.IndexOf(corner) - 1;
            order.Insert(before < 0 ? 0 : order.IndexOf(children[before]) + 1, corner);
        }

        var changed = _cornerOrder.Count > 0;
        _cornerOrder = new();
        _placed = null;
        _numbering = true;
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i].Order == i)
                continue;
            order[i].Order = i;
            changed = true;
        }

        _numbering = false;
#if TOOLS
        if (changed && Engine.IsEditorHint())
            EditorInterface.Singleton.MarkSceneAsUnsaved();
#endif
    }

    private ulong _readyAt;
    private bool _closing;

    /// <summary>The node its corners are under (Shape, in ParkingLot.tscn), if it has one.</summary>
    public Node3D? ShapeNode => GetNodeOrNull<Node3D>("Shape");

    /// <summary>Its corners as they come under its Shape node.</summary>
    private IEnumerable<ParkingCorner> CornerChildren() => ShapeNode?.GetChildren().OfType<ParkingCorner>() ?? Enumerable.Empty<ParkingCorner>();

    /// <summary>The network it belongs to: the nearest above it.</summary>
    public RoadNetwork? Network
    {
        get
        {
            for (var node = GetParent(); node is not null; node = node.GetParent())
            {
                if (node is RoadNetwork network)
                    return network;
            }

            return null;
        }
    }

    /// <summary>The edge of its asphalt, in the world, round it in order; empty until it has built.</summary>
    public IReadOnlyList<V3> Edge { get; private set; } = Array.Empty<V3>();

    /// <summary>Its outer edge, sidewalks and all, in the world: what the ground is shaped to and buildings keep off.</summary>
    public IReadOnlyList<V3> OuterEdge { get; private set; } = Array.Empty<V3>();

    /// <summary>Its sidewalks as built, in the world, for the ground to be shaped under them.</summary>
    public MeshData? Sidewalks { get; private set; }

    /// <summary>Its asphalt as built, in the world, for the ground to be shaped under it.</summary>
    public MeshData? Asphalt { get; private set; }

    /// <summary>Its corners, seen from above, and their heights: what the height anywhere on it blends.</summary>
    private List<(float X, float Z, float Y)> _heights = new();

    /// <summary>
    /// How high it is at a point, seen from above: its corners' heights blended (see
    /// <see cref="CornerHeightAt"/>), so it runs straight from corner to corner along each side and
    /// curves smoothly between them inside; and where it runs along a road, raised or lowered to the road's height,
    /// easing back in from there.
    /// </summary>
    public float HeightAt(float x, float z) => CornerHeightAt(x, z) + RaisedAt(x, z);

    /// <summary>Points where it runs along a road, seen from above, and how far it is raised (or lowered) there to meet the road.</summary>
    private readonly List<(float X, float Z, float By)> _raised = new();

    /// <summary>Those points by the square of the ground they are in (<see cref="RaisedCell"/> a side), to look through only those near a point.</summary>
    private readonly Dictionary<(int X, int Z), List<(float X, float Z, float By)>> _raisedCells = new();

    private const float RaisedCell = 8f;

    /// <summary>How far round a point raised points are blended: past the fade, further ones count for next to nothing (their weight falls with the distance to the fourth).</summary>
    private const float RaisedSearch = 32f;

    /// <summary>The raised points sorted into their squares, after they change.</summary>
    private void IndexRaised()
    {
        _raisedCells.Clear();
        foreach (var point in _raised)
        {
            var cell = ((int)MathF.Floor(point.X / RaisedCell), (int)MathF.Floor(point.Z / RaisedCell));
            if (!_raisedCells.TryGetValue(cell, out var list))
                _raisedCells[cell] = list = new();
            list.Add(point);
        }
    }

    /// <summary>
    /// How far it is raised at a point to meet the roads it runs along: theirs blended by nearness (of
    /// those within <see cref="RaisedSearch"/>), fading out by how far the nearest is.
    /// </summary>
    private float RaisedAt(float x, float z)
    {
        if (_raised.Count == 0)
            return 0f;
        var (sum, weights, nearest) = (0f, 0f, float.MaxValue);
        var reach = (int)MathF.Ceiling(RaisedSearch / RaisedCell);
        var (cx, cz) = ((int)MathF.Floor(x / RaisedCell), (int)MathF.Floor(z / RaisedCell));
        for (var ix = cx - reach; ix <= cx + reach; ix++)
        for (var iz = cz - reach; iz <= cz + reach; iz++)
        {
            if (!_raisedCells.TryGetValue((ix, iz), out var cell))
                continue;
            foreach (var (rx, rz, by) in cell)
            {
            var squared = (rx - x) * (rx - x) + (rz - z) * (rz - z);
            if (squared < 1e-6f)
                return by;
            nearest = MathF.Min(nearest, squared);
            var weight = 1f / (squared * squared);
            sum += by * weight;
            weights += weight;
            }
        }

        if (weights <= 0f)
            return 0f;
        var fade = 1f - Mathf.SmoothStep(RaisedHold, RaisedHold + RaisedFade, MathF.Sqrt(nearest));
        return fade <= 0f ? 0f : sum / weights * fade;
    }

    /// <summary>
    /// How high its corners make it at a point: each side's height where the point is level with it
    /// along it (straight from corner to corner), blended by how near each side is (the inverse
    /// square of the distance). So it runs along each side exactly, and never goes above its highest
    /// corner or below its lowest, wherever the point is, in or out.
    /// </summary>
    private float CornerHeightAt(float x, float z)
    {
        var count = _heights.Count;
        if (count == 0)
            return GlobalPosition.Y;
        var (sum, weights) = (0f, 0f);
        for (var i = 0; i < count; i++)
        {
            var (a, b) = (_heights[i], _heights[(i + 1) % count]);
            var (abx, abz) = (b.X - a.X, b.Z - a.Z);
            var length = abx * abx + abz * abz;
            var s = length < 1e-6f ? 0f : Math.Clamp(((x - a.X) * abx + (z - a.Z) * abz) / length, 0f, 1f);
            var (px, pz) = (a.X + abx * s - x, a.Z + abz * s - z);
            var squared = px * px + pz * pz;
            var height = a.Y + (b.Y - a.Y) * s;
            if (squared < 1e-6f)
                return height;
            var weight = 1f / squared;
            sum += height * weight;
            weights += weight;
        }

        return sum / weights;
    }

    private V3 Lifted(V3 at) => at with { Y = HeightAt(at.X, at.Z) };

    /// <summary>The middle of its asphalt, in the world: where traffic through it passes.</summary>
    public V3 Middle { get; private set; }

    /// <summary>The roads joined to it, and whether each is joined by its start.</summary>
    public IReadOnlyList<(Road Road, bool Start)> Entrances()
    {
        var list = new List<(Road, bool)>();
        foreach (var road in Network?.Roads ?? Array.Empty<Road>())
        {
            if (road.Closed || road.Points.Count < 2)
                continue;
            if (road.StartJunction == this)
                list.Add((road, true));
            if (road.EndJunction == this)
                list.Add((road, false));
        }

        return list;
    }

    /// <summary>The nearest point on the edge of its asphalt to a place, seen from above, at its height, and how far away that is.</summary>
    public (V3 Point, float Distance)? NearestEdge(V3 at)
    {
        if (Edge.Count < 3)
            return null;
        (V3 Point, float Distance)? best = null;
        for (var i = 0; i < Edge.Count; i++)
        {
            var (a, b) = (Edge[i], Edge[(i + 1) % Edge.Count]);
            var ab = new V3(b.X - a.X, 0f, b.Z - a.Z);
            var ap = new V3(at.X - a.X, 0f, at.Z - a.Z);
            var t = ab.LengthSquared() < 1e-8f ? 0f : Math.Clamp(V3.Dot(ap, ab) / ab.LengthSquared(), 0f, 1f);
            var point = a + (b - a) * t;
            var distance = new V3(point.X - at.X, 0f, point.Z - at.Z).Length();
            if (best is null || distance < best.Value.Distance)
                best = (point, distance);
        }

        return best;
    }

    public override void _EnterTree()
    {
        _closing = false;
        AddToGroup(Group);
    }

    // Taken away (deleted, or its scene closed): nothing kept off it any more.
    public override void _ExitTree() => RoadLinks.FootprintRemoved?.Invoke(this);

    public override void _Ready()
    {
        _notifiedAt = GlobalTransform;
        SetNotifyTransform(true);
        _readyAt = Godot.Time.GetTicksMsec();
        ChildOrderChanged += ChildrenChanged;
        if (ShapeNode is null)
            Callable.From(FillInShape).CallDeferred();
        if (ShapeNode is { } shape)
            shape.ChildOrderChanged += ChildrenChanged;
        QueueRebuild();
    }

    /// <summary>
    /// Added in the editor bare (Add Child Node, not ParkingLot.tscn): a Shape with four corners, as
    /// ParkingLot.tscn has. Deferred, as the editor gives a new node its owner after adding it.
    /// </summary>
    private void FillInShape()
    {
#if TOOLS
        if (!Engine.IsEditorHint() || !IsInsideTree() || ShapeNode is not null || EditorInterface.Singleton.GetEditedSceneRoot() is not { } root || root == this)
            return;
        var owner = Owner ?? root;
        var shape = new Node3D { Name = "Shape" };
        AddChild(shape, true);
        shape.Owner = owner;
        var at = new[] { new Vector3(-15f, 0f, -10f), new Vector3(15f, 0f, -10f), new Vector3(15f, 0f, 10f), new Vector3(-15f, 0f, 10f) };
        for (var i = 0; i < at.Length; i++)
        {
            var corner = new ParkingCorner { Name = $"Corner{i + 1}", Position = at[i] };
            shape.AddChild(corner, true);
            corner.Owner = owner;
        }

        shape.ChildOrderChanged += ChildrenChanged;
        NumberCorners();
        QueueRebuild();
#endif
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTransformChanged && this.Moved(ref _notifiedAt))
            QueueRebuild();
        else if (what is (int)NotificationPredelete or (int)NotificationExitTree)
            _closing = true;
    }

    // A method, not a lambda, so the editor can reconnect it after rebuilding the scripts.
    private void ChildrenChanged()
    {
        NumberCorners();
        QueueRebuild();
    }

    private void Change<T>(ref T field, T value)
    {
        field = value;
        if (!Reloading)
            QueueRebuild();
    }

    /// <summary>Builds it again at the end of the frame, once however many changes come before then.</summary>
    public void QueueRebuild()
    {
        if (_rebuildQueued || !IsInsideTree())
            return;
        _rebuildQueued = true;
        Callable.From(Rebuild).CallDeferred();
    }

    /// <summary>How far a road's end may be from its edge to join it, in metres.</summary>
    public const float JoinDistance = 1.5f;

    /// <summary>How far a joined road's end has to be taken from its edge to come away, in metres.</summary>
    public const float LeaveDistance = 3f;

    /// <summary>How far down its sides run into the ground, where it has no sidewalk: as far as a sidewalk's, so it never hangs over lower ground beside it.</summary>
    private const float EdgeDepth = RoadMeshBuilder.SidewalkSkirtDepth;

    /// <summary>The longest stretch of straight side between points of its outline, so the sidewalk opens just where a road comes in.</summary>
    private const float Step = 0.5f;

    /// <summary>How far in from its edge bays keep, in metres.</summary>
    private const float BayMargin = 0.5f;

    /// <summary>How wide the painted lines between bays are.</summary>
    private const float LineWidth = 0.12f;

    /// <summary>A point round its outline: where it is, the way out from it, and how wide the sidewalk is there.</summary>
    /// <remarks>
    /// Meets: where its sidewalk stops against a road's (see MeetRoads), the back of that road's
    /// sidewalk from its inner side to its far side, which its end follows rather than going straight across.
    /// </remarks>
    private readonly record struct EdgePoint(V3 At, V3 Out, float Width, bool Mitred = false, V3[]? Meets = null);

    /// <summary>How long it has to be left alone in the editor before its bays and collision are built too, in milliseconds.</summary>
    private const ulong SettleTime = 300;

    private ulong _settleAt;
    private bool _settling;

    /// <summary>
    /// Builds it whole once it has been left alone a moment: while its corners are being dragged
    /// about in the editor, only its outline is drawn again, each step.
    /// </summary>
    private void Settle()
    {
        _settleAt = Godot.Time.GetTicksMsec() + SettleTime;
        if (_settling)
            return;
        _settling = true;
        WaitToSettle();
    }

    private void WaitToSettle()
    {
        var wait = _settleAt > Godot.Time.GetTicksMsec() ? (_settleAt - Godot.Time.GetTicksMsec()) / 1000.0 : 0.0;
        GetTree().CreateTimer(wait).Connect(SceneTreeTimer.SignalName.Timeout, Callable.From(Settled));
    }

    private void Settled()
    {
        if (!IsInstanceValid(this) || !IsInsideTree())
        {
            _settling = false;
            return;
        }

        // Not while a corner is still held (paused mid-drag): once it is let go.
        if (Godot.Time.GetTicksMsec() < _settleAt || Input.IsMouseButtonPressed(MouseButton.Left))
        {
            if (Godot.Time.GetTicksMsec() >= _settleAt)
                _settleAt = Godot.Time.GetTicksMsec() + 50;
            WaitToSettle();
            return;
        }

        _settling = false;
        _wholeDue = true;
        QueueRebuild();
    }

    /// <summary>Whether its next build is whole (bays and collision too), not just what shows its shape while it is being dragged.</summary>
    private bool _wholeDue = true;

    private void Rebuild()
    {
        _rebuildQueued = false;
        if (!IsInstanceValid(this) || !IsInsideTree())
            return;
        // Its gizmo, for clicking it in the editor, round what it builds now.
        UpdateGizmos();
        // Its corners numbered, any new ones among them.
        NumberCorners();
        // In the editor, after the first build, quick builds while it changes and a whole one once it settles.
        var whole = _wholeDue || !Engine.IsEditorHint();
        _wholeDue = false;
        if (!whole)
        {
            // While its corners are dragged about: only its outline drawn again; the lot itself
            // (asphalt, sidewalks, bays, collision) once it has been left alone a moment (see Settle).
            Settle();
            ShowCorners();
            return;
        }

        _mesh ??= AddInternal(new MeshInstance3D { Name = "Mesh" });
        _body ??= AddInternal(new StaticBody3D { Name = "Body" });
        if (whole)
        {
            foreach (var old in _body.GetChildren())
            {
                _body.RemoveChild(old);
                old.QueueFree();
            }
        }

        // The roads round it are looked up again only on a whole build: they stay put while it is dragged about.
        if (whole || _snap is null)
            _snap = RoadSnapper();
        var snap = _snap;
        if (whole)
            SnapCorners(snap);
        ShowCorners();
        _heights = Corners.Select(c => (c.GlobalPosition.X, c.GlobalPosition.Z, c.GlobalPosition.Y)).ToList();
        _raised.Clear();
        var lifted = Outline().Select(p => p with { At = Lifted(p.At) }).ToList();
        var points = AlongRoads(lifted, snap);
        FollowSidewalks(points, lifted, snap);
        Untangle(points, lifted);
        // Paved all over with sidewalk, a square has no sidewalk of its own round it: its sides' widths only
        // reached for the roads by, for its edge to go onto them as a lot's does.
        if (AllSidewalk)
            points = points.Select(p => p with { Width = 0f }).ToList();
        // Where it runs along a road, at the road's height: inside, easing back from there to its corners' heights.
        for (var i = 0; i < points.Count; i += 2)
        {
            if (points[i].At != lifted[i].At)
                _raised.Add((points[i].At.X, points[i].At.Z, points[i].At.Y - CornerHeightAt(points[i].At.X, points[i].At.Z)));
        }

        IndexRaised();
        // The rest of its outline eases to them too, so its own sidewalks meet the roads' level.
        if (_raised.Count > 0)
            points = points.Select((p, i) => p.At == lifted[i].At ? p with { At = Lifted(p.At) } : p).ToList();
        FillDips(points);
        MeetRoads(points, snap);
        if (points.Count < 3)
        {
            _mesh.Mesh = null;
            Edge = OuterEdge = Array.Empty<V3>();
            Sidewalks = Asphalt = null;
            return;
        }

        OpenForRoads(points);

        var surface = new MeshData();
        var sidewalks = new MeshData();
        var lines = new MeshData();
        var edge = Distinct(points.Select(p => p.At)).ToList();
        Edge = edge;
        Middle = Lifted(edge.Aggregate(V3.Zero, (sum, p) => sum + p) / edge.Count);
        Surface(surface, edge);
        InsideCorners(points);
        Walks(sidewalks, surface, points);
        if (AllSidewalk)
            Paved(surface, edge, snap);
        else if (_markings == ParkingMarkings.Bays && whole)
        {
            _onSurface = SurfaceHeights(surface);
            Bays(lines, edge);
        }

        Sidewalks = sidewalks;
        Asphalt = surface;
        OuterEdge = Distinct(points.Select(p => p.Width > 0f ? p.At + p.Out * p.Width : p.At)).ToList();
        // Where it lies, sidewalks and all, for whatever keeps other things off it.
        if (OuterEdge.Count >= 3)
            RoadLinks.FootprintChanged?.Invoke(this, [RoadNetwork.Outline(OuterEdge)]);

        var toLocal = GlobalTransform.AffineInverse();
        var mesh = new ArrayMesh();
        Road.AddSurface(mesh, surface, Road.DefaultMaterial(AllSidewalk ? "Sidewalk" : "Asphalt"), toLocal);
        Road.AddSurface(mesh, lines, Road.DefaultMaterial("Road-Line-White"), toLocal);
        Road.AddSurface(mesh, sidewalks, Road.DefaultMaterial("Sidewalk"), toLocal);
        _mesh.Mesh = mesh.GetSurfaceCount() > 0 ? mesh : null;

        foreach (var solid in whole ? new[] { surface, sidewalks } : Array.Empty<MeshData>())
        {
            if (solid.Indices.Count == 0)
                continue;
            var faces = solid.Indices.Select(i => toLocal * solid.Vertices[i].ToGodot()).ToArray();
            _body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = faces } });
        }

        // Its edge moved from under a road joined to it: that road goes to where its edge now is.
        foreach (var (road, start) in Entrances())
        {
            var ends = road.Points;
            var end = (start ? ends[0] : ends[^1]).GlobalPosition.ToNumerics();
            if (NearestEdge(end) is not { } near || V3.DistanceSquared(near.Point, end) > RoadPlacing.Tolerance * RoadPlacing.Tolerance)
                road.QueueRebuild();
        }

        if (whole)
            EntrancesChanged();
        Network?.TrafficChanged();
    }

    /// <summary>How far outside a road's edge a corner may be to go onto it, in metres.</summary>
    public const float SnapDistance = 1.5f;

    /// <summary>How far above or below a road a corner may be to go onto it, in metres: not onto one passing over or under it.</summary>
    private const float HeightReach = 2.5f;

    /// <summary>The backs of a road's two sidewalks, out round its parking.</summary>
    private static (SidewalkBack Left, SidewalkBack Right) Backs(Road road, IReadOnlyList<RoadBand> bands, List<ParkingSpec> parking, float sidewalk)
    {
        var full = road.FullFrames();
        var cut = road.Frames();
        var (from, to) = cut.Count >= 2 ? (cut[0].Distance, cut[^1].Distance) : (0f, full.Count >= 2 ? full[^1].Distance : 0f);
        SidewalkBack Side(RoadSide side) => new(d =>
        {
            var setBack = parking.Where(s => s.Side == side).Select(s => s.SidewalkDepthAt(d, sidewalk)).DefaultIfEmpty(0f).Max();
            return RoadCurve.At(full, d).Across(side == RoadSide.Right ? bands[^1].Right + setBack : bands[0].Left - setBack);
        }, from, to);
        return (Side(RoadSide.Left), Side(RoadSide.Right));
    }

    /// <summary>
    /// Where a place goes onto the roads and junctions near it, if it is over one or within a reach
    /// outside one, and how far it is from there: onto the back of a road's sidewalk on the place's
    /// side (or its edge, where it has none), at the road's height, a curb below the sidewalk's top;
    /// in or by a junction, onto the far edge of its corners. Not onto a road passing over or under,
    /// on a viaduct or in a tunnel, or joined to it.
    /// </summary>
    private Func<V3, float, Snap?> RoadSnapper()
    {
        if (Network is not { } network || Corners.Count < 3)
            return (_, _) => null;
        var joined = Entrances().Select(e => e.Road).ToHashSet();
        // Only those near it, by the box round its corners and a little more.
        var near = Corners.Select(c => c.GlobalPosition).ToList();
        var (minX, maxX) = (near.Min(p => p.X) - Margin, near.Max(p => p.X) + Margin);
        var (minZ, maxZ) = (near.Min(p => p.Z) - Margin, near.Max(p => p.Z) + Margin);
        bool Near(V3 p) => p.X >= minX && p.X <= maxX && p.Z >= minZ && p.Z <= maxZ;
        var roads = network.Roads.Where(r => !joined.Contains(r) && r.StructureSpecs().Count == 0)
            .Select(r => (Frames: r.Frames().Where(f => Near(f.Position)).ToList(), Bands: r.CrossSection.Bands(),
                Parking: r.Parking.Select(p => p.Spec).ToList(), Sidewalk: MathF.Max(r.CrossSection.SidewalkLeft, r.CrossSection.SidewalkRight), Road: r))
            .Select(r => (r.Frames, r.Bands, r.Parking, r.Sidewalk, Backs: Backs(r.Road, r.Bands, r.Parking, r.Sidewalk)))
            .Where(r => r.Frames.Count > 0).ToList();
        var junctions = network.Junctions.Where(j => !j.Viaduct && j.Shape is { Reach.Count: >= 3 } && j.Shape.Reach.Any(Near))
            .Select(j => Junction(j.Shape!)).ToList();

        return (at, reach) =>
        {
            Snap? best = null;
            var over = false;
            var bestAlong = 0f;
            void Consider(V3 to, float distance, bool inside, SidewalkBack? back = null, float along = 0f)
            {
                if (MathF.Abs(to.Y - at.Y) > HeightReach)
                    return;
                over |= inside;
                if (best is null || distance < best.Value.Distance)
                {
                    best = new Snap(to, distance, false, back);
                    bestAlong = along;
                }
            }


            foreach (var (frames, bands, parking, sidewalk, backs) in roads)
            {
                // Out past its parking's bays, where it has any, as its sidewalk is set back round them.
                float SetBack(float d, RoadSide side) => parking.Where(s => s.Side == side).Select(s => s.SidewalkDepthAt(d, sidewalk)).DefaultIfEmpty(0f).Max();
                var deepest = parking.Select(s => s.Depth).DefaultIfEmpty(0f).Max();
                var within = MathF.Max(-bands[0].Left, bands[^1].Right) + deepest + reach + Road.Spacing;
                foreach (var frame in frames)
                {
                    var offset = at - frame.Position;
                    if (offset.X * offset.X + offset.Z * offset.Z > within * within)
                        continue;
                    var along = V3.Dot(offset, frame.Forward);
                    // Only where this frame is the nearest along it: not off either end, where its junction is.
                    if (MathF.Abs(along) > Road.Spacing * 0.75f)
                        continue;
                    var d = frame.Distance + along;
                    var (left, right) = (bands[0].Left - SetBack(d, RoadSide.Left), bands[^1].Right + SetBack(d, RoadSide.Right));
                    var across = V3.Dot(offset, frame.Right);
                    if (across < left - reach || across > right + reach)
                        continue;
                    var edge = across >= (left + right) / 2f ? right : left;
                    var distance = MathF.Abs(across - edge);
                    Consider(frame.Position + frame.Forward * along + frame.Right * edge, distance, across > left && across < right && distance > OnEdge,
                        across >= (left + right) / 2f ? backs.Right : backs.Left, d);
                }
            }

            foreach (var (flat, box, edges) in junctions)
            {
                if (!box.Grow(reach).HasPoint(new Vector2(at.X, at.Z)))
                    continue;
                var inside = Geometry2D.IsPointInPolygon(new Vector2(at.X, at.Z), flat);
                foreach (var (a, b) in edges)
                {
                    var ab = b - a;
                    var flatAb = ab with { Y = 0f };
                    var s = flatAb.LengthSquared() < 1e-6f ? 0f : Math.Clamp(V3.Dot((at - a) with { Y = 0f }, flatAb) / flatAb.LengthSquared(), 0f, 1f);
                    var to = a + ab * s;
                    var distance = V3.Distance(to with { Y = 0f }, at with { Y = 0f });
                    if (inside || distance <= reach)
                        Consider(to, distance, inside && distance > OnEdge);
                }
            }

            return best is { } found ? found with { Over = over, Along = bestAlong } : null;
        };
    }

    /// <summary>A junction's outline seen from above, the box round it, and the edges of its corners (not across where roads come in).</summary>
    private static (Vector2[] Flat, Rect2 Box, List<(V3 A, V3 B)> Edges) Junction(JunctionShape shape)
    {
        var outline = shape.Reach;
        var flat = outline.Select(p => new Vector2(p.X, p.Z)).ToArray();
        var box = new Rect2(flat[0], Vector2.Zero);
        foreach (var point in flat)
            box = box.Expand(point);
        var edges = new List<(V3, V3)>();
        for (var k = 0; k < outline.Count; k++)
        {
            var (a, b) = (outline[k], outline[(k + 1) % outline.Count]);
            var middle = (a + b) / 2f;
            if (shape.Ends?.Any(end => MathF.Abs(V3.Dot(middle - end.Position, end.Forward)) < 0.1f) != true)
                edges.Add((a, b));
        }

        return (flat, box, edges);
    }

    private Func<V3, float, Snap?>? _snap;

    /// <summary>Where a place goes onto a road or junction, how far that is, and whether the place is over it (not just by it, or on its edge).</summary>
    private readonly record struct Snap(V3 To, float Distance, bool Over, SidewalkBack? Back = null, float Along = 0f);

    /// <summary>The back of one side of a road's sidewalk (out round its parking), as a place at each distance along it.</summary>
    private sealed record SidewalkBack(Func<float, V3> At, float From, float To);

    /// <summary>How near a road's edge a place counts as on it, not over it, in metres.</summary>
    private const float OnEdge = 0.05f;

    /// <summary>Its entrance corners as last built: where each is and how wide.</summary>
    private List<(V3 At, float Width)> _entrances = new();

    /// <summary>Its entrances moved, or one was made, taken away or widened: the roads by them (then and now) open their sidewalks again.</summary>
    private void EntrancesChanged()
    {
        var now = Corners.Where(c => c.Entrance).Select(c => (At: c.GlobalPosition.ToNumerics(), Width: c.EntranceWidth)).ToList();
        if (now.Count == _entrances.Count && now.Zip(_entrances).All(p => V3.DistanceSquared(p.First.At, p.Second.At) < 1e-6f && p.First.Width == p.Second.Width))
            return;
        var spots = now.Concat(_entrances).ToList();
        _entrances = now;
        if (Network is not { } network)
            return;
        foreach (var road in network.Roads)
        {
            var frames = road.Frames();
            if (spots.Any(s => frames.Any(f => V3.DistanceSquared(f.Position, s.At) < (s.Width + Margin) * (s.Width + Margin))))
                road.QueueRebuild();
        }
    }

    /// <summary>How far round it to look for roads to go onto, in metres.</summary>
    private const float Margin = 20f;

    /// <summary>Each corner on or near a road or junction goes onto it (see <see cref="RoadSnapper"/>).</summary>
    private void SnapCorners(Func<V3, float, Snap?> snap)
    {
        foreach (var corner in Corners)
        {
            if (snap(corner.GlobalPosition.ToNumerics(), SnapDistance) is not { } on)
                continue;
            var target = on.To.ToGodot();
            if (!corner.GlobalPosition.Near(target))
            {
                corner.GlobalPosition = target;
                // Moved by itself, not by hand: the scene has changed all the same.
#if TOOLS
                if (Engine.IsEditorHint())
                    EditorInterface.Singleton.MarkSceneAsUnsaved();
#endif
            }
        }
    }

    /// <summary>
    /// Its outline where it runs over or along a road or junction (near enough that its own sidewalk
    /// would overlap, or just off): onto the back of the road's sidewalk, following it, with no
    /// sidewalk of its own there.
    /// </summary>
    private static List<EdgePoint> AlongRoads(List<EdgePoint> points, Func<V3, float, Snap?> snap)
    {
        var count = points.Count;
        bool Over(V3 at) => snap(at, 0f) is { Over: true };
        // Only where it is over one, or its sidewalk (or just past it) would be and it faces the road:
        // a side running into a road at a slant keeps its sidewalk, cut off along the road (below).
        var onto = points.Select(p =>
        {
            if (snap(p.At, SnapDistance + p.Width) is not { } on)
                return (Snap?)null;
            if (on.Over)
                return on;
            var toward = (on.To - p.At) with { Y = 0f };
            var facing = toward.LengthSquared() < OnEdge * OnEdge || V3.Dot(V3.Normalize(toward), p.Out) > 0.7f;
            return facing && Over(p.At + p.Out * (p.Width + Touching)) ? on : null;
        }).ToArray();
        // Not for a stretch too short to be along a road (a point or two catching the corner of one): that would leave a gap in its sidewalk.
        var start = Array.FindIndex(onto, o => o is null);
        if (start >= 0)
        {
            for (var k = 1; k <= count; k++)
            {
                var i = (start + k) % count;
                if (onto[i] is null || onto[(i + count - 1) % count] is not null)
                    continue;
                var run = 0;
                var length = 0f;
                while (run < count && onto[(i + run) % count] is not null)
                {
                    if (run > 0)
                        length += V3.Distance(points[(i + run) % count].At, points[(i + run - 1) % count].At);
                    run++;
                }

                if (length < MinimumAlong)
                {
                    for (var j = 0; j < run; j++)
                        onto[(i + j) % count] = null;
                }
            }
        }

        // A few left alone between others gone onto them (at a corner on a junction's curve, facing across it, not out
        // onto it): onto them too, or its edge would dip in to the corner there.
        for (var i = 0; i < count; i++)
        {
            if (onto[i] is not null || onto[(i + count - 1) % count] is null)
                continue;
            var run = 1;
            while (run <= 3 && onto[(i + run) % count] is null)
                run++;
            if (run > 3 || onto[(i + run) % count] is null)
                continue;
            for (var k = 0; k < run; k++)
            {
                var p = points[(i + k) % count];
                if (snap(p.At, SnapDistance + p.Width) is { } alone)
                    onto[(i + k) % count] = alone;
            }
        }

        return points.Select((p, i) => onto[i] is { } on ? p with { At = on.To, Width = 0f } : CutOff(p)).ToList();

        // Its sidewalk only as far out as the road's starts, where it would run onto one.
        EdgePoint CutOff(EdgePoint p)
        {
            if (p.Width <= 0f || !Over(p.At + p.Out * p.Width))
                return p;
            var (inside, outside) = (0f, p.Width);
            for (var k = 0; k < 10; k++)
            {
                var middle = (inside + outside) / 2f;
                if (Over(p.At + p.Out * middle))
                    outside = middle;
                else
                    inside = middle;
            }

            // Its far edge there at the road's height, so its top meets the road's sidewalk's.
            var outer = p.At + p.Out * inside;
            if (inside < 0.1f || snap(outer, Touching) is not { } edge)
                return p with { Width = inside };
            return p with { Out = (outer with { Y = edge.To.Y } - p.At) / inside, Width = inside };
        }
    }

    /// <summary>
    /// Between two points of its outline gone onto the back of the same sidewalk but far apart (either side of where it
    /// steps out round parking, say), points along that sidewalk, each a tenth of a metre on: its edge follows the
    /// sidewalk's corner, rather than cutting across it. Both lists (its outline, and as it was) kept point for point.
    /// </summary>
    private static void FollowSidewalks(List<EdgePoint> points, List<EdgePoint> lifted, Func<V3, float, Snap?> snap)
    {
        // How near a road's end a point on its sidewalk has to be for the edge to run on to the end, where it goes onto
        // the junction's corner, in metres: far enough for where the points between went onto the junction's corner
        // instead (nearer to them), leaving the sidewalk's corner cut.
        const float NearEnd = 10f;
        static float? EndNear(Snap on) => on.Back is { } back
            ? (MathF.Abs(on.Along - back.From) < MathF.Abs(on.Along - back.To) ? back.From : back.To) is var end && MathF.Abs(end - on.Along) < NearEnd ? end : null
            : null;
        for (var i = 0; i < points.Count; i++)
        {
            var j = (i + 1) % points.Count;
            var (a, b) = (points[i], points[j]);
            if (a.Width > 0f || b.Width > 0f || V3.Distance(a.At, b.At) < 0.05f || snap(a.At, OnEdge) is not { } from || snap(b.At, OnEdge) is not { } to)
                continue;
            // Each a run of distances along a sidewalk, from one point to the next: along the one sidewalk both are on;
            // or, from one onto another (or a junction's corner), along each to its road's end, where they meet.
            var runs = new List<(SidewalkBack Back, float A, float B)>();
            if (from.Back is { } same && same == to.Back)
            {
                // (Two next to each other on it, as along any straight stretch: nothing between.)
                if (V3.Distance(a.At, b.At) >= Step * 1.5f && MathF.Abs(to.Along - from.Along) < Margin)
                    runs.Add((same, from.Along, to.Along));
            }
            else
            {
                if (EndNear(from) is { } end)
                    runs.Add((from.Back!, from.Along, end));
                if (EndNear(to) is { } start)
                    runs.Add((to.Back!, start, to.Along));
            }

            var added = new List<V3>();
            foreach (var (back, start, end) in runs)
            {
                var count = (int)MathF.Ceiling(MathF.Abs(end - start) / 0.1f);
                var first = added.Count == 0 && back == from.Back ? 1 : 0;
                var last = back == to.Back && (runs.Count == 1 || back != from.Back) ? count - 1 : count;
                for (var k = first; k <= last; k++)
                    added.Add(back.At(start + (end - start) * k / Math.Max(count, 1)));
            }

            // Only where it bends: a run of points in a line between (dozens along a straight stretch) leaves the surface's
            // triangulation slivers that it drops, holes in it.
            added = Bends(a.At, added, b.At);
            if (added.Count == 0)
                continue;
            var source = lifted[i].At;
            var next = lifted[j].At;
            points.InsertRange(i + 1, added.Select(p => a with { At = p }));
            lifted.InsertRange(i + 1, added.Select((_, k) => lifted[i] with { At = V3.Lerp(source, next, (k + 1f) / (added.Count + 1f)) + V3.UnitY * 1e-3f }));
            i += added.Count;
        }
    }

    /// <summary>
    /// The points of a run between two others where it bends (leaving any out would move its line by more than a couple
    /// of centimetres), the rest left out: those along straight stretches between.
    /// </summary>
    private static List<V3> Bends(V3 from, List<V3> run, V3 to)
    {
        const float Off = 0.02f;
        var all = new List<V3> { from };
        all.AddRange(run);
        all.Add(to);
        var keep = new bool[all.Count];
        keep[0] = keep[^1] = true;
        // Each stretch: the point furthest off the straight line across it kept, and the halves either side looked at in turn.
        var stretches = new Stack<(int A, int B)>();
        stretches.Push((0, all.Count - 1));
        while (stretches.Count > 0)
        {
            var (s, e) = stretches.Pop();
            var along = (all[e] - all[s]) with { Y = 0f };
            var length = along.Length();
            var (most, at) = (0f, -1);
            for (var k = s + 1; k < e; k++)
            {
                var off = (all[k] - all[s]) with { Y = 0f };
                var away = length < 1e-4f ? off.Length() : MathF.Abs(along.X * off.Z - along.Z * off.X) / length;
                if (away > most)
                    (most, at) = (away, k);
            }

            if (at < 0 || most <= Off)
                continue;
            keep[at] = true;
            stretches.Push((s, at));
            stretches.Push((at, e));
        }

        return [.. run.Where((_, k) => keep[k + 1])];
    }

    /// <summary>
    /// Its outline with any loop in it cut out (where, gone onto roads with no sidewalk of its own, it doubles back
    /// across itself, near a corner between two of them): from where it first crosses itself to where it crosses back, the crossing in their place.
    /// Both lists kept point for point.
    /// </summary>
    private static void Untangle(List<EdgePoint> points, List<EdgePoint> lifted)
    {
        const int Window = 60;
        static Vector2 Flat(V3 p) => new(p.X, p.Z);
        for (var i = 0; i + 3 < points.Count; i++)
        {
            for (var j = Math.Min(i + Window, points.Count - 2); j >= i + 2; j--)
            {
                // Only where it has gone onto roads (no sidewalk of its own): its own sidewalks' corners are no loops.
                if (Enumerable.Range(i, j - i + 2).Any(k => points[k].Width > 0f))
                    continue;
                var hit = Geometry2D.SegmentIntersectsSegment(Flat(points[i].At), Flat(points[i + 1].At), Flat(points[j].At), Flat(points[j + 1].At));
                if (hit.VariantType != Variant.Type.Vector2)
                    continue;
                var cross = hit.AsVector2();
                // The crossing, at the height between, in place of all from i + 1 to j.
                var y = (points[i].At.Y + points[j + 1].At.Y) / 2f;
                points[i + 1] = points[i + 1] with { At = new V3(cross.X, y, cross.Y) };
                points.RemoveRange(i + 2, j - i - 1);
                lifted.RemoveRange(i + 2, j - i - 1);
                break;
            }
        }
    }

    /// <summary>
    /// Where one of its sidewalks stops at a road it runs along: its end goes onto the road's edge,
    /// at the road's height, and across along the back of the road's sidewalk, so the two meet
    /// flush instead of square across.
    /// </summary>
    private static void MeetRoads(List<EdgePoint> points, Func<V3, float, Snap?> snap)
    {
        var count = points.Count;
        bool OnRoad(EdgePoint p) => p.Width <= 0f && snap(p.At, OnEdge * 2f) is { Distance: < OnEdge * 2f };
        for (var i = 0; i < count; i++)
        {
            var p = points[i];
            if (p.Width <= 0f || (!OnRoad(points[(i + count - 1) % count]) && !OnRoad(points[(i + 1) % count])))
                continue;
            var at = snap(p.At, SnapDistance) is { } on ? on.To : p.At;
            if (snap(p.At + p.Out * p.Width, p.Width + SnapDistance) is not { } outer)
            {
                points[i] = p with { At = at };
                continue;
            }

            var across = outer.To - at;
            var width = new Vector2(across.X, across.Z).Length();
            if (width < 0.1f)
            {
                points[i] = p with { At = at };
                continue;
            }

            // The road's edge between, each half metre: it curves, and a straight end would leave a sliver
            // of ground between them.
            var steps = Math.Max(2, (int)MathF.Ceiling(width / 0.5f));
            var meets = new V3[steps + 1];
            meets[0] = at;
            meets[steps] = outer.To;
            for (var k = 1; k < steps; k++)
            {
                var along = at + across * (k / (float)steps);
                meets[k] = snap(along, 1f) is { } edge ? edge.To : along;
            }

            points[i] = p with { At = at, Out = across / width, Width = width, Meets = meets };
        }
    }

    /// <summary>
    /// Its sidewalk kept whole where it is cut back to nothing (or less) for a short way only, between
    /// stretches of it: as at a corner put onto a road's edge, which the cutting back by the road
    /// would leave a gap at. Each point in such a dip as wide as its neighbours either side, blended.
    /// Longer stretches (along a road, or open for one) are left as they are.
    /// </summary>
    private static void FillDips(List<EdgePoint> points)
    {
        var count = points.Count;
        if (count < 3)
            return;
        for (var i = 0; i < count; i++)
        {
            var (before, here) = (points[(i + count - 1) % count], points[i]);
            // A dip's start: narrower than the point before, which has sidewalk.
            if (before.Width <= 0f || here.Width >= before.Width * 0.9f)
                continue;
            // How far it goes: until it is as wide as the point before again (or near), and how long that is.
            var (run, length) = (0, 0f);
            while (run < count && points[(i + run) % count].Width < before.Width * 0.9f)
            {
                if (run > 0)
                    length += V3.Distance(points[(i + run) % count].At, points[(i + run - 1) % count].At);
                run++;
            }

            var after = points[(i + run) % count];
            if (run == 0 || run >= count || after.Width <= 0f || length >= MinimumAlong)
                continue;
            for (var k = 0; k < run; k++)
            {
                var share = (k + 1f) / (run + 1f);
                var index = (i + k) % count;
                points[index] = points[index] with { Width = before.Width + (after.Width - before.Width) * share };
            }

            i += run - 1;
        }
    }

    /// <summary>How far past its sidewalk a road may start for it to run along the road, in metres.</summary>
    private const float Touching = 0.25f;

    /// <summary>How long a stretch of its outline has to be along a road to go onto it, in metres.</summary>
    private const float MinimumAlong = 3f;

    /// <summary>
    /// A line from corner to corner, in their order and back to the first, on its Edge node: shown
    /// only in the editor, over everything, to see its corners' order by.
    /// </summary>
    private void ShowCorners()
    {
        if ((GetNodeOrNull<MeshInstance3D>("Edge") ?? AddEdge()) is not { } edge)
            return;
        // Only while it is being worked on (see Outlines).
        AddToGroup(RoadOutlines.Group);
        edge.Visible = RoadOutlines.Shown(this, "ParkingLots");
        var corners = Corners;
        if (corners.Count < 2)
        {
            edge.Mesh = null;
            return;
        }

        var toLocal = edge.GlobalTransform.AffineInverse();
        var mesh = new ImmediateMesh();
        mesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
        foreach (var corner in corners.Append(corners[0]))
            mesh.SurfaceAddVertex(toLocal * (corner.GlobalPosition + Vector3.Up * 0.2f));
        mesh.SurfaceEnd();
        edge.Mesh = mesh;
    }

    /// <summary>For a lot made without its scene's Edge (added as a bare node): the Edge from ParkingLot.tscn, as its own.</summary>
    private MeshInstance3D? AddEdge()
    {
        const string sceneFile = "res://addons/unpleasant_roads/Scenes/ParkingLot.tscn";
        if (!ResourceLoader.Exists(sceneFile) || ResourceLoader.Load<PackedScene>(sceneFile).Instantiate() is not { } lot)
            return null;
        var edge = lot.GetNodeOrNull<MeshInstance3D>("Edge");
        if (edge is not null)
        {
            lot.RemoveChild(edge);
            edge.Owner = null;
            AddChild(edge, false, InternalMode.Front);
        }

        lot.Free();
        return edge;
    }

    /// <summary>
    /// Its outline, round its corners in order, each rounded by its radius (as far as its sides
    /// allow); its straight sides in short steps. A sharp corner has its point twice, once for each
    /// side, as the two sides' sidewalks meet there.
    /// </summary>
    private List<EdgePoint> Outline()
    {
        var corners = Corners;
        var count = corners.Count;
        if (count < 3)
            return new List<EdgePoint>();
        var height = GlobalPosition.Y;
        var at = corners.Select(c => c.GlobalPosition.ToNumerics() with { Y = height }).ToList();

        // Which way round it goes, for which side of each side is out.
        var area = 0f;
        for (var i = 0; i < count; i++)
            area += at[i].X * at[(i + 1) % count].Z - at[(i + 1) % count].X * at[i].Z;
        V3 OutOf(V3 along) => area > 0f ? V3.Normalize(new V3(along.Z, 0f, -along.X)) : V3.Normalize(new V3(-along.Z, 0f, along.X));

        // Each corner's curve: from where it leaves the side before to where it meets the side after.
        var curves = new List<List<EdgePoint>>();
        for (var i = 0; i < count; i++)
        {
            var (before, corner, after) = (at[(i + count - 1) % count], at[i], at[(i + 1) % count]);
            var (toBefore, toAfter) = (before - corner, after - corner);
            var (outBefore, outAfter) = (OutOf(corner - before), OutOf(after - corner));
            var (widthBefore, widthAfter) = (corners[(i + count - 1) % count].SidewalkWidth, corners[i].SidewalkWidth);
            var curve = new List<EdgePoint>();
            if (toBefore.LengthSquared() < 1e-6f || toAfter.LengthSquared() < 1e-6f)
            {
                curves.Add(curve);
                continue;
            }

            var (d1, d2) = (V3.Normalize(toBefore), V3.Normalize(toAfter));
            var angle = MathF.Acos(Math.Clamp(V3.Dot(d1, d2), -1f, 1f));
            var radius = corners[i].Radius;
            var tangent = angle < 1e-3f || angle > MathF.PI - 1e-3f ? 0f : radius / MathF.Tan(angle / 2f);
            var room = 0.45f * MathF.Min(toBefore.Length(), toAfter.Length());
            if (tangent > room)
            {
                tangent = room;
                radius = tangent * MathF.Tan(angle / 2f);
            }

            if (radius < 0.01f || tangent < 0.01f)
            {
                // Sharp: the corner twice, the side before's end and the side after's start.
                curve.Add(new EdgePoint(corner, outBefore, widthBefore));
                curve.Add(new EdgePoint(corner, outAfter, widthAfter));
                curves.Add(curve);
                continue;
            }

            var middle = corner + V3.Normalize(d1 + d2) * (radius / MathF.Sin(angle / 2f));
            var (start, end) = (corner + d1 * tangent, corner + d2 * tangent);
            var (from, to) = (start - middle, end - middle);
            var sweep = MathF.Acos(Math.Clamp(V3.Dot(V3.Normalize(from), V3.Normalize(to)), -1f, 1f));
            var steps = Math.Max(2, (int)MathF.Ceiling(sweep / (MathF.PI / 16f)));
            for (var k = 0; k <= steps; k++)
            {
                var s = k / (float)steps;
                // Round the arc: the turn from its start to its end, a share at a time.
                var way = Slerp(V3.Normalize(from), V3.Normalize(to), s);
                var point = middle + way * radius;
                // Out from the curve's middle where it bulges out, into it where it curls in.
                var outward = V3.Dot(way, outBefore + outAfter) >= 0f ? way : -way;
                curve.Add(new EdgePoint(point, outward, widthBefore + (widthAfter - widthBefore) * s));
            }

            curves.Add(curve);
        }

        var points = new List<EdgePoint>();
        for (var i = 0; i < count; i++)
        {
            points.AddRange(curves[i]);
            if (curves[i].Count == 0 || curves[(i + 1) % count].Count == 0)
                continue;
            // The side on to the next corner's curve, in steps.
            var (a, b) = (curves[i][^1].At, curves[(i + 1) % count][0].At);
            var length = V3.Distance(a, b);
            var outward = OutOf(b - a);
            var steps = (int)MathF.Ceiling(length / Step);
            for (var k = 1; k < steps; k++)
                points.Add(new EdgePoint(a + (b - a) * (k / (float)steps), outward, corners[i].SidewalkWidth));
        }

        return points;
    }

    private static V3 Slerp(V3 a, V3 b, float s)
    {
        var angle = MathF.Acos(Math.Clamp(V3.Dot(a, b), -1f, 1f));
        if (angle < 1e-4f)
            return a;
        return (a * MathF.Sin((1f - s) * angle) + b * MathF.Sin(s * angle)) / MathF.Sin(angle);
    }

    /// <summary>No sidewalk where a road joined to it comes in: across its carriageway, at its end.</summary>
    private void OpenForRoads(List<EdgePoint> points)
    {
        foreach (var (road, start) in Entrances())
        {
            var frames = road.Frames();
            if (frames.Count < 2)
                continue;
            var frame = start ? frames[0] : frames[^1];
            var carriageway = road.CrossSection.Bands().Where(b => b.Kind != BandKind.Sidewalk).ToList();
            if (carriageway.Count == 0)
                continue;
            var (left, right) = (carriageway.Min(b => b.Left), carriageway.Max(b => b.Right));
            var across = new V3(frame.Right.X, 0f, frame.Right.Z);
            var along = new V3(frame.Forward.X, 0f, frame.Forward.Z);
            if (across.LengthSquared() < 1e-6f || along.LengthSquared() < 1e-6f)
                continue;
            (across, along) = (V3.Normalize(across), V3.Normalize(along));
            for (var i = 0; i < points.Count; i++)
            {
                var offset = points[i].At - frame.Position;
                var side = V3.Dot(offset, across);
                if (side >= left - 0.05f && side <= right + 0.05f && MathF.Abs(V3.Dot(offset, along)) < LeaveDistance)
                    points[i] = points[i] with { Width = 0f };
            }
        }
    }

    /// <summary>The points of a loop with any one the same as the one before (a sharp corner's two) left out.</summary>
    private static IEnumerable<V3> Distinct(IEnumerable<V3> points)
    {
        V3? last = null;
        foreach (var point in points)
        {
            if (last is { } was && V3.DistanceSquared(was, point) < 1e-6f)
                continue;
            last = point;
            yield return point;
        }
    }

    /// <summary>How far from where it runs along a road it stays at the road's height (so its own sidewalks meet the road's level), in metres.</summary>
    private const float RaisedHold = 2.5f;

    /// <summary>How far in from there its height eases back to its corners', in metres.</summary>
    private const float RaisedFade = 8f;

    /// <summary>How far apart the points inside its asphalt are, so it follows its heights closely.</summary>
    private const float Fineness = 2f;

    /// <summary>
    /// Its asphalt over its outline, following its heights, with its sides running down into the
    /// ground: in triangles between the points of its outline and a grid of points inside it, each
    /// at its height there.
    /// </summary>
    /// <summary>Whether a point is inside an outline, or on it (a side's middle is, along a straight side).</summary>
    private static bool Inside(Vector2 at, Vector2[] outline)
    {
        if (Geometry2D.IsPointInPolygon(at, outline))
            return true;
        for (var i = 0; i < outline.Length; i++)
        {
            if (Geometry2D.GetClosestPointToSegment(at, outline[i], outline[(i + 1) % outline.Length]).DistanceTo(at) < 0.01f)
                return true;
        }

        return false;
    }

    /// <summary>Whether it is paved all over as a sidewalk (a square: see Square), not with asphalt and sidewalks round it.</summary>
    protected virtual bool AllSidewalk => false;

    /// <summary>
    /// Its surface made a sidewalk all over: lifted a kerb's height (meeting the tops of the roads' sidewalks it
    /// runs along), and a kerb's side all round it, down past the ground.
    /// </summary>
    private static void Paved(MeshData surface, List<V3> edge, Func<V3, float, Snap?> snap)
    {
        var up = V3.UnitY * RoadCrossSection.CurbHeight;
        for (var i = 0; i < surface.Vertices.Count; i++)
            surface.Vertices[i] += up;
        var down = V3.UnitY * EdgeDepth;
        var turning = 0f;
        for (var i = 0; i < edge.Count; i++)
            turning += edge[i].X * edge[(i + 1) % edge.Count].Z - edge[(i + 1) % edge.Count].X * edge[i].Z;
        for (var i = 0; i < edge.Count; i++)
        {
            var (a, b) = (edge[i], edge[(i + 1) % edge.Count]);
            var along = (b - a) with { Y = 0f };
            if (along.LengthSquared() < 1e-6f)
                continue;
            // Out: to the side away from its inside, by which way round its outline runs.
            var outward = V3.Normalize(turning > 0f ? new V3(along.Z, 0f, -along.X) : new V3(-along.Z, 0f, along.X));
            Face(surface, new[] { a + up, b + up, b - down, a - down }, outward);
            // Along a road's or junction's sidewalk: a lip out under its top, so no sliver shows between them where its
            // edge cuts a little inside theirs (their curves are straight pieces).
            if (new[] { a, (a + b) / 2f, b }.Any(p => snap(p, 0.3f) is not null))
            {
                var lip = up - V3.UnitY * LipDrop;
                Face(surface, new[] { a + lip, a + lip + outward * LipWidth, b + lip + outward * LipWidth, b + lip }, V3.UnitY);
            }
        }
    }

    /// <summary>How far out a square's lip reaches under the sidewalks it meets, and how far below their top, in metres.</summary>
    private const float LipWidth = 0.1f;

    private const float LipDrop = 0.01f;

    private void Surface(MeshData mesh, List<V3> edge)
    {
        var outline = edge.Select(p => new Vector2(p.X, p.Z)).ToArray();
        // Its sides with points along them as close as the grid inside, so no triangle reaches
        // across a bay in its outline (a long straight side has only its ends otherwise).
        var points = new List<Vector2>();
        for (var i = 0; i < outline.Length; i++)
        {
            var (a, b) = (outline[i], outline[(i + 1) % outline.Length]);
            var steps = Mathf.Max(1, Mathf.CeilToInt(a.DistanceTo(b) / Fineness));
            for (var k = 0; k < steps; k++)
            {
                // Not a point on top of the last (the outline can have the same point twice, as at
                // an entrance's end): the triangulation goes wrong round a doubled point.
                var at = a.Lerp(b, (float)k / steps);
                if (points.Count == 0 || points[^1].DistanceTo(at) > 0.05f)
                    points.Add(at);
            }
        }

        if (points.Count > 1 && points[^1].DistanceTo(points[0]) <= 0.05f)
            points.RemoveAt(points.Count - 1);

        var onOutline = points.Count;
        var (minX, maxX) = (outline.Min(p => p.X), outline.Max(p => p.X));
        var (minZ, maxZ) = (outline.Min(p => p.Y), outline.Max(p => p.Y));
        // Inside, and not so near the edge that it makes slivers against it.
        var inner = Geometry2D.OffsetPolygon(outline, -Fineness / 2f).OrderByDescending(p => Mathf.Abs(Area(p))).FirstOrDefault();
        for (var x = Mathf.Floor(minX / Fineness) * Fineness; x <= maxX; x += Fineness)
        {
            for (var z = Mathf.Floor(minZ / Fineness) * Fineness; z <= maxZ; z += Fineness)
            {
                var at = new Vector2(x, z);
                if (inner is not null && Geometry2D.IsPointInPolygon(at, inner))
                    points.Add(at);
            }
        }

        var all = points.ToArray();
        var triangles = Geometry2D.TriangulateDelaunay(all);
        // Each point's height worked out once, not once for each triangle it is a corner of.
        var heights = new Dictionary<int, V3>();
        V3 LiftedAt(int i) => heights.TryGetValue(i, out var known) ? known : heights[i] = Lifted(new V3(all[i].X, 0f, all[i].Y));
        for (var t = 0; t + 2 < triangles.Length; t += 3)
        {
            var (a, b, c) = (all[triangles[t]], all[triangles[t + 1]], all[triangles[t + 2]]);
            // Only those inside it: the triangulation fills its hull, across any bay in its outline too.
            // One between points well inside it is inside it.
            // One with a corner on the outline only if its middle and the middles of its sides are inside.
            var onEdge = triangles[t] < onOutline || triangles[t + 1] < onOutline || triangles[t + 2] < onOutline;
            if (onEdge && new[] { (a + b + c) / 3f, (a + b) / 2f, (b + c) / 2f, (c + a) / 2f }.Any(p => !Inside(p, outline)))
                continue;
            var (la, lb, lc) = (LiftedAt(triangles[t]), LiftedAt(triangles[t + 1]), LiftedAt(triangles[t + 2]));
            var normal = V3.Cross(lb - la, lc - la);
            Face(mesh, new[] { la, lb, lc }, normal.Y < 0f ? -normal : normal);
        }

        var down = V3.UnitY * EdgeDepth;
        var middle = edge.Aggregate(V3.Zero, (sum, p) => sum + p) / edge.Count;
        for (var i = 0; i < edge.Count; i++)
        {
            var (a, b) = (edge[i], edge[(i + 1) % edge.Count]);
            var away = (a + b) / 2f - middle;
            Face(mesh, new[] { a, b, b - down, a - down }, new V3(away.X, 0f, away.Z));
        }
    }

    /// <summary>
    /// Its sidewalks: along each stretch of its outline with one, raised a curb above its asphalt,
    /// running far down into the ground on their outer side (so ground lowered beside them never
    /// shows under them), and closed where they stop. Sharp corners between two filled in.
    /// </summary>
    private static void Walks(MeshData mesh, MeshData surface, List<EdgePoint> points)
    {
        var up = V3.UnitY * RoadCrossSection.CurbHeight;
        var deep = V3.UnitY * RoadMeshBuilder.SidewalkSkirtDepth;
        var down = V3.UnitY * EdgeDepth;
        var count = points.Count;
        V3 Outer(EdgePoint p) => p.At + p.Out * p.Width;

        for (var i = 0; i < count; i++)
        {
            var (p, q) = (points[i], points[(i + 1) % count]);
            var same = SamePlace(p.At, q.At);
            if (same)
            {
                // A sharp corner: the two sides' sidewalks meet out where their outer edges would.
                if (p.Width > 0f && q.Width > 0f && Meet(Outer(p), p.Out, Outer(q), q.Out) is { } corner
                    && V3.Dot(corner - p.At, p.Out + q.Out) > 0f && V3.Distance(corner, p.At) < 3f * MathF.Max(p.Width, q.Width))
                {
                    Face(mesh, new[] { p.At + up, Outer(p) + up, corner + up }, V3.UnitY);
                    Face(mesh, new[] { p.At + up, corner + up, Outer(q) + up }, V3.UnitY);
                    Face(mesh, new[] { Outer(p) + up, corner + up, corner - deep, Outer(p) - deep }, p.Out);
                    Face(mesh, new[] { corner + up, Outer(q) + up, Outer(q) - deep, corner - deep }, q.Out);
                }

                continue;
            }

            if (p.Width <= 0f || q.Width <= 0f)
                continue;
            var side = V3.Normalize((p.Out + q.Out) / 2f);
            Face(mesh, new[] { p.At + up, q.At + up, Outer(q) + up, Outer(p) + up }, V3.UnitY);
            // The curb, facing the asphalt; the far side, facing away.
            Face(mesh, new[] { p.At + up, q.At + up, q.At - down, p.At - down }, -side);
            // Not where two sidewalks meet on the slant at an inside corner: there is sidewalk past it.
            if (!(p.Mitred && q.Mitred))
                Face(mesh, new[] { Outer(p) + up, Outer(q) + up, Outer(q) - deep, Outer(p) - deep }, side);
        }

        // Closed across where each stretch of sidewalk stops (where a road comes in, or a side has none).
        for (var i = 0; i < count; i++)
        {
            var p = points[i];
            if (p.Width <= 0f)
                continue;
            foreach (var step in new[] { -1, 1 })
            {
                var next = points[(i + step + count) % count];
                if (next.Width > 0f || SamePlace(next.At, p.At) || (p.Mitred && next.Mitred))
                    continue;
                if (p.Meets is { Length: > 2 } meets)
                {
                    // Along the road's edge where it meets a road's sidewalk: filled out to it from straight across.
                    for (var k = 0; k + 1 < meets.Length; k++)
                    {
                        Face(mesh, new[] { p.At + up, meets[k] + up, meets[k + 1] + up }, V3.UnitY);
                        Face(mesh, new[] { meets[k] + up, meets[k + 1] + up, meets[k + 1] - deep, meets[k] - deep }, next.At - p.At);
                    }

                    continue;
                }

                Face(mesh, new[] { p.At + up, Outer(p) + up, Outer(p) - deep, p.At - deep }, next.At - p.At);
            }
        }
    }

    /// <summary>
    /// At each sharp corner turning in (where its outline bends back on itself), the two sides'
    /// sidewalks would overlap: each cut off on the slant from the corner to where their far edges
    /// cross, so they meet there instead, at one height.
    /// </summary>
    private static void InsideCorners(List<EdgePoint> points)
    {
        var count = points.Count;
        for (var i = 0; i < count; i++)
        {
            var (p, q) = (points[i], points[(i + 1) % count]);
            if (p.Width <= 0f || q.Width <= 0f || !SamePlace(p.At, q.At))
                continue;
            var before = points[(i + count - 1) % count].At;
            if (Meet(p.At + p.Out * p.Width, p.Out, q.At + q.Out * q.Width, q.Out) is not { } meet)
                continue;
            // Behind the end of the side before: they overlap.
            var across = (meet with { Y = p.At.Y }) - p.At;
            if (V3.Dot(across with { Y = 0f }, (p.At - before) with { Y = 0f }) >= 0f)
                continue;
            var width = new Vector2(across.X, across.Z).Length();
            if (width < 0.01f || width > 3f * MathF.Max(p.Width, q.Width))
                continue;
            var way = across / width;
            // The corner itself the slant's start, so each side's sidewalk runs to it from the side's points cut back along the slant.
            points[i] = p with { Out = way, Width = SlantStart, Mitred = true };
            points[(i + 1) % count] = q with { Out = way, Width = SlantStart, Mitred = true };

            // Each side's points before it (and after) reaching across that slant: cut back to it.
            var (from, to) = (new Vector2(p.At.X, p.At.Z), new Vector2(meet.X, meet.Z));
            foreach (var step in new[] { -1, 1 })
            {
                var cut = false;
                for (var k = 1; k < count; k++)
                {
                    var index = ((step < 0 ? i : i + 1) + step * k + count * k) % count;
                    var point = points[index];
                    if (point.Width <= 0f)
                        break;
                    var start = new Vector2(point.At.X, point.At.Z);
                    var end = start + new Vector2(point.Out.X, point.Out.Z) * point.Width;
                    var hit = Geometry2D.SegmentIntersectsSegment(start, end, from, to);
                    if (hit.VariantType != Variant.Type.Vector2)
                        break;
                    cut = true;
                    var at = hit.AsVector2();
                    // Its far edge on the slant at the corner's height, as the other side's is, so the two meet level.
                    var edge = new V3(at.X, p.At.Y, at.Y);
                    var reach = (at - start).Length();
                    if (reach < 0.01f)
                    {
                        points[index] = point with { Width = 0f, Mitred = true };
                        continue;
                    }

                    points[index] = point with { Out = (edge - point.At) / reach, Width = reach, Mitred = true };
                }

                // None on this side reaching across the slant (a bend in only a little): its sidewalk not
                // cut back to the corner, so the corner's on this side runs the whole slant out to meet it.
                if (!cut)
                {
                    var own = step < 0 ? i : (i + 1) % count;
                    points[own] = points[own] with { Out = way, Width = width };
                }
            }
        }
    }

    /// <summary>How wide a sidewalk is at an inside corner, where its slant starts: next to nothing, but some, so it is still built up to there.</summary>
    private const float SlantStart = 0.001f;

    /// <summary>Where two lines, each through a point square to a way out, meet, seen from above.</summary>
    /// <summary>
    /// Whether two points of its outline are one place, seen from above: a sharp corner's two (one for
    /// each side), which can be a hair apart in height once its edge is eased to meet a road.
    /// </summary>
    private static bool SamePlace(V3 a, V3 b) => (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z) < 1e-4f;

    private static V3? Meet(V3 a, V3 outA, V3 b, V3 outB)
    {
        // Each line runs square to its way out.
        var (dirA, dirB) = (new V3(-outA.Z, 0f, outA.X), new V3(-outB.Z, 0f, outB.X));
        var cross = dirA.X * dirB.Z - dirA.Z * dirB.X;
        if (MathF.Abs(cross) < 1e-5f)
            return null;
        var gap = b - a;
        var s = (gap.X * dirB.Z - gap.Z * dirB.X) / cross;
        return a + dirA * s;
    }

    /// <summary>
    /// Rows of bays filling it along its own x axis: a row against one edge, then aisles with a row
    /// either side, each bay only where it lies wholly inside (a little in from its edge) and clear
    /// of where roads come in. Each bay's two sides are painted.
    /// </summary>
    /// <summary>How high its asphalt is as built, where it is; for its lines to lie on it.</summary>
    private Func<float, float, float?>? _onSurface;

    /// <summary>
    /// How high a mesh's upward faces are at a point seen from above, if it has one there: the
    /// triangles found by the 2 m square they touch, the height from the one the point is in.
    /// </summary>
    private static Func<float, float, float?> SurfaceHeights(MeshData mesh)
    {
        const float cell = 2f;
        var cells = new Dictionary<(int, int), List<(V3 A, V3 B, V3 C)>>();
        for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
        {
            var (a, b, c) = (mesh.Vertices[mesh.Indices[i]], mesh.Vertices[mesh.Indices[i + 1]], mesh.Vertices[mesh.Indices[i + 2]]);
            var normal = V3.Cross(b - a, c - a);
            if (normal.LengthSquared() < 1e-10f || MathF.Abs(V3.Normalize(normal).Y) < 0.5f)
                continue;
            var (x0, x1) = ((int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / cell), (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / cell));
            var (z0, z1) = ((int)MathF.Floor(MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) / cell), (int)MathF.Floor(MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) / cell));
            for (var x = x0; x <= x1; x++)
            for (var z = z0; z <= z1; z++)
            {
                if (!cells.TryGetValue((x, z), out var list))
                    cells[(x, z)] = list = new();
                list.Add((a, b, c));
            }
        }

        return (x, z) =>
        {
            if (!cells.TryGetValue(((int)MathF.Floor(x / cell), (int)MathF.Floor(z / cell)), out var list))
                return null;
            foreach (var (a, b, c) in list)
            {
                // Barycentric, seen from above.
                var (v0x, v0z, v1x, v1z) = (b.X - a.X, b.Z - a.Z, c.X - a.X, c.Z - a.Z);
                var (px, pz) = (x - a.X, z - a.Z);
                var den = v0x * v1z - v1x * v0z;
                if (MathF.Abs(den) < 1e-9f)
                    continue;
                var s = (px * v1z - v1x * pz) / den;
                var u = (v0x * pz - px * v0z) / den;
                if (s >= -1e-4f && u >= -1e-4f && s + u <= 1f + 1e-4f)
                    return a.Y + (b.Y - a.Y) * s + (c.Y - a.Y) * u;
            }

            return null;
        };
    }

    private void Bays(MeshData lines, List<V3> edge)
    {
        // Drawn by hand, in its areas: only theirs. Otherwise filling all of it.
        var areas = GetChildren().OfType<ParkingBayArea>().Where(a => a.Corners.Count >= 3).ToList();
        if (areas.Count == 0)
        {
            Bays(lines, edge, GlobalTransform, null, _bayWidth, _bayDepth, _aisleWidth, _bayOffset, _bayAngle);
            return;
        }

        // Just bays in them, row after row: their aisles are wherever they are not.
        foreach (var area in areas)
        {
            var corners = area.Corners.Select(c => c.GlobalPosition.ToNumerics()).ToList();
            switch (area.Layout)
            {
                // End to end along the row: a bay's depth along it, its width across.
                case BayLayout.Parallel:
                    Bays(lines, edge, area.GlobalTransform, corners, area.BayDepth, area.BayWidth, 0f, area.BayOffset, area.BayAngle);
                    break;
                // Leaning: as far apart along the row as a bay is wide across its lean, the row as deep as the bay reaches.
                case BayLayout.Angled:
                    var slant = Mathf.DegToRad(area.Angle);
                    var reach = area.BayDepth * MathF.Sin(slant);
                    Bays(lines, edge, area.GlobalTransform, corners, area.BayWidth / MathF.Sin(slant), reach, 0f, area.BayOffset, area.BayAngle, area.BayDepth * MathF.Cos(slant));
                    break;
                default:
                    Bays(lines, edge, area.GlobalTransform, corners, area.BayWidth, area.BayDepth, 0f, area.BayOffset, area.BayAngle);
                    break;
            }
        }
    }

    /// <summary>
    /// Rows of bays along a place's x axis, from its place: in an area of it drawn by hand, wholly
    /// inside that and inside it (a little in from its edge); or with no area, filling it.
    /// </summary>
    /// <remarks>With a lean, each bay's lines run that far along the row from its aisle side to its back (angled bays).</remarks>
    private void Bays(MeshData lines, List<V3> edge, Transform3D place, IReadOnlyList<V3>? area, float bayWidth, float bayDepth, float aisleWidth, Vector2 offset, float angle = 0f,
        float lean = 0f)
    {
        var origin = place.Origin.ToNumerics();
        // Its x axis, turned as far as asked about the vertical.
        var turned = place.Basis.X.Rotated(Vector3.Up, Mathf.DegToRad(angle));
        var xAxis = new V3(turned.X, 0f, turned.Z);
        if (xAxis.LengthSquared() < 1e-6f)
            return;
        xAxis = V3.Normalize(xAxis);
        var zAxis = new V3(-xAxis.Z, 0f, xAxis.X);
        Vector2 Local(V3 p) => new(V3.Dot(p - origin, xAxis), V3.Dot(p - origin, zAxis));
        V3 World(float x, float z)
        {
            var at = origin + xAxis * x + zAxis * z;
            return at with { Y = (_onSurface?.Invoke(at.X, at.Z) ?? HeightAt(at.X, at.Z)) + 0.012f };
        }

        var shape = edge.Select(Local).ToArray();
        // Filling all of it, kept a little in from its edge; in a bay area put by hand, right up to it.
        var inset = area is not null ? shape : Geometry2D.OffsetPolygon(shape, -BayMargin).OrderByDescending(p => Mathf.Abs(Area(p))).FirstOrDefault();
        if (inset is null || inset.Length < 3)
            return;
        var drawn = area?.Select(Local).ToArray();
        // Bays right along its sides count as inside: a whisker out, so a corner on a side is not lost to rounding.
        var reach = drawn is null ? null : Geometry2D.OffsetPolygon(drawn, 0.05f).OrderByDescending(p => Mathf.Abs(Area(p))).FirstOrDefault();
        // The rows fill the area drawn, or all of it.
        var bounds = drawn ?? inset;
        var (minX, maxX) = (bounds.Min(p => p.X), bounds.Max(p => p.X));
        var (minZ, maxZ) = (bounds.Min(p => p.Y), bounds.Max(p => p.Y));

        // Kept clear in front of where roads come in: as wide as the road and a metre either side,
        // in as far as a bay and an aisle, for traffic to turn in.
        var clear = new List<(Vector2 At, Vector2 Across, Vector2 In, float Left, float Right)>();
        foreach (var (road, start) in Entrances())
        {
            var frames = road.Frames();
            if (frames.Count < 2)
                continue;
            var frame = start ? frames[0] : frames[^1];
            var bands = road.CrossSection.Bands().Where(b => b.Kind != BandKind.Sidewalk).ToList();
            var across = new Vector2(V3.Dot(frame.Right, xAxis), V3.Dot(frame.Right, zAxis)).Normalized();
            var inward = new Vector2(V3.Dot(frame.Forward, xAxis), V3.Dot(frame.Forward, zAxis)).Normalized() * (start ? -1f : 1f);
            clear.Add((Local(frame.Position), across, inward, bands.Min(b => b.Left) - 1f, bands.Max(b => b.Right) + 1f));
        }

        // Its planted islands, which bays keep off.
        var islands = GetChildren().OfType<ParkingIsland>().Where(i => i.Outline.Count >= 3)
            .Select(i => i.Outline.Select(p => Local(p.ToNumerics())).ToArray()).ToList();
        bool InTheWay(Vector2 at) => islands.Any(i => Geometry2D.IsPointInPolygon(at, i)) || clear.Any(c =>
        {
            var offset = at - c.At;
            var side = offset.Dot(c.Across);
            var depth = offset.Dot(c.In);
            return side > c.Left && side < c.Right && depth > -1f && depth < bayDepth + aisleWidth;
        });

        // The rows across it: one against its edge, then pairs back to back with an aisle before each.
        var rows = new List<(float From, float To)>();
        var z = minZ + offset.Y;
        rows.Add((z, z + bayDepth));
        z += bayDepth + aisleWidth;
        while (z + bayDepth <= maxZ)
        {
            rows.Add((z, z + bayDepth));
            if (z + 2f * bayDepth > maxZ)
                break;
            rows.Add((z + bayDepth, z + 2f * bayDepth));
            z += 2f * bayDepth + aisleWidth;
        }

        bool Inside(float x, float zz) => Geometry2D.IsPointInPolygon(new Vector2(x, zz), inset)
            && (reach is null || Geometry2D.IsPointInPolygon(new Vector2(x, zz), reach));
        var painted = new HashSet<(int X, int Z)>();
        // A line between bays, from (x, from) to (x + lean, to); its width square to it.
        void Line(float x, float from, float to)
        {
            if (!painted.Add((Mathf.RoundToInt(x * 100f), Mathf.RoundToInt(from * 100f))))
                return;
            var length = MathF.Sqrt(lean * lean + (to - from) * (to - from));
            var w = LineWidth / 2f * length / MathF.Max(to - from, 1e-3f);
            // In short pieces, each on the surface where it lies.
            var pieces = Math.Max(1, (int)MathF.Ceiling(length / 1f));
            for (var k = 0; k < pieces; k++)
            {
                var (s, t) = ((float)k / pieces, (float)(k + 1) / pieces);
                var (a, b) = (from + (to - from) * s, from + (to - from) * t);
                var (xa, xb) = (x + lean * s, x + lean * t);
                Face(lines, new[] { World(xa - w, a), World(xa + w, a), World(xb + w, b), World(xb - w, b) }, V3.UnitY);
            }
        }

        var bays = new List<(float X, float From, float To)>();
        foreach (var (from, to) in rows)
        {
            if (to > maxZ + 1e-3f)
                continue;
            // From a bay's width before the edge, shifted along: any bay that fits is drawn.
            for (var x = minX - bayWidth + Mathf.PosMod(offset.X, bayWidth); x + bayWidth <= maxX; x += bayWidth)
            {
                if (!Inside(x, from) || !Inside(x + bayWidth, from) || !Inside(x + lean, to) || !Inside(x + bayWidth + lean, to))
                    continue;
                if (InTheWay(new Vector2(x, from)) || InTheWay(new Vector2(x + bayWidth, from)) || InTheWay(new Vector2(x + lean, to)) || InTheWay(new Vector2(x + bayWidth + lean, to)))
                    continue;
                Line(x, from, to);
                Line(x + bayWidth, from, to);
                bays.Add((x, from, to));
            }
        }

        // Where two rows meet back to back, a line between their bays (square bays only: leaning ones' rows do not line up).
        if (lean != 0f)
            return;
        var backs = bays.Select(b => (Mathf.RoundToInt(b.X * 100f), Mathf.RoundToInt(b.From * 100f))).ToHashSet();
        foreach (var (x, _, to) in bays)
        {
            if (!backs.Contains((Mathf.RoundToInt(x * 100f), Mathf.RoundToInt(to * 100f))))
                continue;
            var w = LineWidth / 2f;
            var pieces = Math.Max(1, (int)MathF.Ceiling(bayWidth / 1f));
            for (var k = 0; k < pieces; k++)
            {
                var (a, b) = (x + bayWidth * k / pieces, x + bayWidth * (k + 1) / pieces);
                Face(lines, new[] { World(a, to - w), World(b, to - w), World(b, to + w), World(a, to + w) }, V3.UnitY);
            }
        }
    }

    private static float Area(Vector2[] polygon)
    {
        var area = 0f;
        for (var i = 0; i < polygon.Length; i++)
            area += polygon[i].X * polygon[(i + 1) % polygon.Length].Y - polygon[(i + 1) % polygon.Length].X * polygon[i].Y;
        return area / 2f;
    }

    private T AddInternal<T>(T node) where T : Node
    {
        AddChild(node, false, InternalMode.Back);
        return node;
    }

    /// <summary>A flat face through its corners, wound to face the way it is told, its texture laid out in the world from above or along it.</summary>
    private static void Face(MeshData mesh, V3[] corners, V3 normal)
    {
        if (normal.LengthSquared() < 1e-8f)
            return;
        normal = V3.Normalize(normal);
        var cross = V3.Cross(corners[1] - corners[0], corners[2] - corners[0]);
        var flipped = V3.Dot(cross, normal) > 0f;
        var order = flipped ? corners.Reverse().ToArray() : corners;
        var level = MathF.Abs(normal.Y) > 0.7f;
        var first = mesh.Vertices.Count;
        foreach (var p in order)
        {
            var uv = level ? new System.Numerics.Vector2(p.X, p.Z) : new System.Numerics.Vector2(p.X + p.Z, p.Y);
            mesh.Add(p, normal, uv / RoadMeshBuilder.TextureSize);
        }

        for (var k = 1; k + 1 < order.Length; k++)
            mesh.Indices.AddRange(new[] { first, first + k, first + k + 1 });
    }

    /// <summary>Its outline shown in the editor only while it, something under it, or its ParkingLots node is selected (see Outlines).</summary>
    public void ShowOutline()
    {
        if (GetNodeOrNull<MeshInstance3D>("Edge") is { } edge)
            edge.Visible = RoadOutlines.Shown(this, "ParkingLots");
    }
}
