using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>Which sides of a road a town lines with buildings.</summary>
/// <summary>A tunnel's light bar: where its light is, and the light it gives.</summary>
public readonly record struct TunnelLight(Transform3D Place, Color Colour, float Energy, float Range);

public enum RoadBuildings
{
    /// <summary>Both on a slow road (50 km/h or less, not a ramp's), neither on a faster one.</summary>
    Auto,
    Both,
    Left,
    Right,
    None,
}

/// <summary>Which sides of a road have a sidewalk.</summary>
public enum RoadSidewalks
{
    /// <summary>The sides a town lines with buildings (see <see cref="Road.Buildings"/>); none on a road without.</summary>
    Auto,
    Both,
    Left,
    Right,
    None,
}

/// <summary>Which sides of a road lamp posts stand along, left and right as for its sidewalks.</summary>
public enum RoadLampPosts
{
    /// <summary>Staggered on a road with a sidewalk, none on one without.</summary>
    Auto,
    Off,
    Right,
    Left,
    Both,

    /// <summary>Both sides, each side's halfway between the other's.</summary>
    Staggered,
}

/// <summary>
/// A road through its <see cref="RoadPoint"/> children, in their order, with what it looks like
/// across set here: lanes each way, their width, shoulders and a median. It builds its surface,
/// its lines and its collision itself whenever a point or a setting changes; those are internal
/// nodes, made again on load, so never saved. Traffic keeps right: the lanes going forwards (from
/// the first point towards the last) are on the right. A road may start or end at a
/// <see cref="RoadJunction"/>, with its first or last point on it; it stops short where the
/// junction's surface begins. It may start or end at a <see cref="RoadRamp"/> instead, as that
/// ramp's own road. Its <see cref="RoadRamp"/> children are built into it.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_roads/Icons/Road.svg")]
public partial class Road : Node3D
{
    /// <summary>
    /// Whether its saved settings have all been put back since it was made: false from when the
    /// scripts are reloaded (it is made anew, already in the tree and built) to the end of that
    /// frame. Settings put back then are not changes, and rebuild nothing: rebuilding everything
    /// after each reload froze the editor for a minute.
    /// </summary>
    private bool _reloadSettled;

    public Road() => new Callable(this, MethodName.ReloadSettle).CallDeferred();

    private void ReloadSettle() => _reloadSettled = true;

    /// <summary>Whether a setting is being put back as the scripts are reloaded, rather than changed.</summary>
    private bool Reloading => IsInsideTree() && !_reloadSettled;

    /// <summary>How far apart along the road its shape is sampled, in metres.</summary>
    public const float Spacing = 1f;

    private int _forwardLanes = 1;
    private int _backwardLanes = 1;
    private float _laneWidth = 3.5f;
    private float _shoulderWidth = 1f;
    private float _medianWidth;
    private MedianKind _median = MedianKind.Paved;
    private float _shoulderHeight = 1f;
    private float _shoulderAngle = RoadCrossSection.WalkableAngle;
    private bool _closed;
    private Material? _surfaceMaterial;
    private Material? _whiteLineMaterial;
    private Material? _yellowLineMaterial;
    private Material? _bankMaterial;
    private Material? _structureMaterial;
    private Node3D? _startJunction;
    private Node3D? _endJunction;
    private bool _rebuildQueued;
    private StaticBody3D? _body;

    /// <summary>
    /// What the road is called, as players see it: in navigation, on signs. Empty for an unnamed
    /// road. Its node's name is only for finding it in the editor.
    /// </summary>
    [Export]
    public string StreetName
    {
        get => _streetName;
        set
        {
            if (_streetName == value)
                return;
            _streetName = value;
            // Its signs show it: its own, its junctions', and, for a ramp's road, the gantry over the road the ramp is on.
            QueueRebuild();
            foreach (var end in new[] { StartJunction, EndJunction })
            {
                if (end is RoadJunction junction)
                    junction.QueueRebuild();
                else if (end is RoadRamp { Road: { } ramped })
                    ramped.QueueRebuild();
            }
        }
    }

    private string _streetName = "";

    /// <summary>Whether a direction sign stands on it before the junction at its start: Auto puts one up where it would anyway.</summary>
    [ExportGroup("Direction Signs")]
    [Export]
    public DirectionSign DirectionSignAtStart
    {
        get => _directionSignAtStart;
        set
        {
            _directionSignAtStart = value;
            (StartJunction as RoadJunction)?.QueueRebuild();
        }
    }

    private DirectionSign _directionSignAtStart;

    /// <summary>How far back along it from the junction at its start its direction sign stands, in metres, from where it meets the junction; 0 for the usual.</summary>
    [Export(PropertyHint.Range, "0,500,1,or_greater,suffix:m")]
    public float DirectionSignDistanceAtStart
    {
        get => _directionSignDistanceAtStart;
        set
        {
            _directionSignDistanceAtStart = Mathf.Max(0f, value);
            (StartJunction as RoadJunction)?.QueueRebuild();
        }
    }

    private float _directionSignDistanceAtStart;

    /// <summary>The towns its direction sign at its start names, if it has one; none for those the network picks itself.</summary>
    [Export]
    public Godot.Collections.Array<Node3D> DirectionTownsAtStart
    {
        get => _directionTownsAtStart;
        set
        {
            _directionTownsAtStart = value ?? new Godot.Collections.Array<Node3D>();
            Network?.QueueBoards();
        }
    }

    private Godot.Collections.Array<Node3D> _directionTownsAtStart = new();

    /// <summary>Whether a direction sign stands on it before the junction at its end: Auto puts one up where it would anyway.</summary>
    [Export]
    public DirectionSign DirectionSignAtEnd
    {
        get => _directionSignAtEnd;
        set
        {
            _directionSignAtEnd = value;
            (EndJunction as RoadJunction)?.QueueRebuild();
        }
    }

    private DirectionSign _directionSignAtEnd;

    /// <summary>How far back along it from the junction at its end its direction sign stands, in metres, from where it meets the junction; 0 for the usual.</summary>
    [Export(PropertyHint.Range, "0,500,1,or_greater,suffix:m")]
    public float DirectionSignDistanceAtEnd
    {
        get => _directionSignDistanceAtEnd;
        set
        {
            _directionSignDistanceAtEnd = Mathf.Max(0f, value);
            (EndJunction as RoadJunction)?.QueueRebuild();
        }
    }

    private float _directionSignDistanceAtEnd;

    /// <summary>The towns its direction sign at its end names, if it has one; none for those the network picks itself.</summary>
    [Export]
    public Godot.Collections.Array<Node3D> DirectionTownsAtEnd
    {
        get => _directionTownsAtEnd;
        set
        {
            _directionTownsAtEnd = value ?? new Godot.Collections.Array<Node3D>();
            Network?.QueueBoards();
        }
    }

    private Godot.Collections.Array<Node3D> _directionTownsAtEnd = new();

    /// <summary>
    /// How fast traffic may go on it, in km/h; 0 for the usual: 100 on roads of two or more lanes
    /// each way (or one way), 60 on a ramp's road, 50 on the rest.
    /// </summary>
    [ExportGroup("Speed Limit")]
    [Export(PropertyHint.Range, "0,200,5,suffix:km/h")]
    public float SpeedLimit { get => _speedLimit; set => Change(ref _speedLimit, value); }

    private float _speedLimit;

    /// <summary>Its speed limit in km/h: its own, or the usual for a road like it.</summary>
    public float EffectiveSpeedLimit
    {
        get
        {
            if (_speedLimit > 0f)
                return _speedLimit;
            if (StartJunction is RoadRamp || EndJunction is RoadRamp)
                return 60f;
            var busiest = _forwardLanes == 0 || _backwardLanes == 0 ? _forwardLanes + _backwardLanes : Mathf.Min(_forwardLanes, _backwardLanes);
            return busiest >= 2 ? 100f : 50f;
        }
    }

    /// <summary>The fastest a road may be for towns to line it with buildings of their own accord (Auto), in km/h.</summary>
    public const float TownSpeed = 50f;

    /// <summary>
    /// Which sides of it a town lines with buildings, left and right as seen going from its first
    /// point to its last (the road tool labels them when it is selected). Auto: both, on a road of
    /// 50 km/h or less that is not a ramp's; neither on faster roads. Stretches on viaducts or in
    /// tunnels never are.
    /// </summary>
    [ExportGroup("Town")]
    [Export]
    public RoadBuildings Buildings { get => _buildings; set => Change(ref _buildings, value); }

    private RoadBuildings _buildings = RoadBuildings.Auto;

    /// <summary>
    /// Which sides of it have a sidewalk, left and right as for <see cref="Buildings"/>. Auto: the
    /// sides a town lines with buildings, so none on a road without them.
    /// </summary>
    [Export]
    public RoadSidewalks Sidewalks { get => _sidewalks; set => Change(ref _sidewalks, value); }

    private RoadSidewalks _sidewalks = RoadSidewalks.Auto;

    /// <summary>How wide its sidewalks are.</summary>
    [Export(PropertyHint.Range, "0.5,10,0.1,suffix:m")]
    public float SidewalkWidth { get => _sidewalkWidth; set => Change(ref _sidewalkWidth, value); }

    private float _sidewalkWidth = RoadCrossSection.SidewalkWidth;

    /// <summary>
    /// Which sides of it lamp posts stand along, left and right as for <see cref="Buildings"/>: on
    /// the sidewalk by the curb, or out past the shoulder where there is none. Auto: staggered on
    /// a road with a sidewalk, none on one without. None on viaducts or in tunnels, near its ends,
    /// or in the way of a parking lot's entrance. Their light: see the network's Lamp Posts.
    /// </summary>
    [ExportGroup("Lamp Posts")]
    [Export]
    public RoadLampPosts LampPosts { get => _lampPosts; set => Change(ref _lampPosts, value); }

    private RoadLampPosts _lampPosts = RoadLampPosts.Auto;

    /// <summary>How far apart lamp posts stand along each side.</summary>
    [Export(PropertyHint.Range, "10,100,1,suffix:m")]
    public float LampSpacing { get => _lampSpacing; set => Change(ref _lampSpacing, Mathf.Max(value, 5f)); }

    private float _lampSpacing = 30f;

    /// <summary>Its lamp posts as <see cref="LampPosts"/> has them (Auto worked out): whether each side has them, and whether they are staggered.</summary>
    private (bool Left, bool Right, bool Staggered) LampSides => _lampPosts switch
    {
        RoadLampPosts.Off => (false, false, false),
        RoadLampPosts.Right => (false, true, false),
        RoadLampPosts.Left => (true, false, false),
        RoadLampPosts.Both => (true, true, false),
        RoadLampPosts.Staggered => (true, true, true),
        _ => CrossSection.SidewalkLeft > 0f || CrossSection.SidewalkRight > 0f ? (true, true, true) : (false, false, false),
    };

    /// <summary>Whether each side of it has a sidewalk, as <see cref="Sidewalks"/> has it.</summary>
    public (bool Left, bool Right) SidewalkSides => _sidewalks switch
    {
        RoadSidewalks.Both => (true, true),
        RoadSidewalks.Left => (true, false),
        RoadSidewalks.Right => (false, true),
        RoadSidewalks.None => (false, false),
        _ => BuildingSides,
    };

    /// <summary>Whether a town lines each side of it with buildings, as <see cref="Buildings"/> has it.</summary>
    public (bool Left, bool Right) BuildingSides => _buildings switch
    {
        RoadBuildings.Both => (true, true),
        RoadBuildings.Left => (true, false),
        RoadBuildings.Right => (false, true),
        RoadBuildings.None => (false, false),
        _ => EffectiveSpeedLimit <= TownSpeed && StartJunction is not RoadRamp && EndJunction is not RoadRamp ? (true, true) : (false, false),
    };

    /// <summary>Lanes going from the first point towards the last, on the right.</summary>
    [ExportGroup("Lanes")]
    [Export(PropertyHint.Range, "0,8,1")]
    public int ForwardLanes { get => _forwardLanes; set => Change(ref _forwardLanes, value); }

    /// <summary>Lanes going from the last point back towards the first, on the left.</summary>
    [Export(PropertyHint.Range, "0,8,1")]
    public int BackwardLanes { get => _backwardLanes; set => Change(ref _backwardLanes, value); }

    [Export(PropertyHint.Range, "2,6,0.05,suffix:m")]
    public float LaneWidth { get => _laneWidth; set => Change(ref _laneWidth, value); }

    /// <summary>Between the two directions of traffic; none for a road one way only.</summary>
    [Export(PropertyHint.Range, "0,30,0.1,suffix:m")]
    public float MedianWidth { get => _medianWidth; set => Change(ref _medianWidth, value); }

    /// <summary>Road surface, or open to the ground as on a divided highway.</summary>
    [Export]
    public MedianKind Median { get => _median; set => Change(ref _median, value); }

    /// <summary>How wide the paved shoulder is past the outer lanes on each side, before the bank.</summary>
    [ExportGroup("Banks")]
    [Export(PropertyHint.Range, "0,5,0.05,suffix:m")]
    public float ShoulderWidth { get => _shoulderWidth; set => Change(ref _shoulderWidth, value); }

    /// <summary>How far the bank past each shoulder drops from the road (negative: rises, as out of a cutting), as it slopes away at the shoulder angle.</summary>
    [Export(PropertyHint.Range, "-30,30,0.1,or_greater,or_less,suffix:m")]
    public float ShoulderHeight { get => _shoulderHeight; set => Change(ref _shoulderHeight, value); }

    /// <summary>How steep the banks are. The default is the steepest the player can walk up.</summary>
    [Export(PropertyHint.Range, "5,85,0.5,degrees")]
    public float ShoulderAngle { get => _shoulderAngle; set => Change(ref _shoulderAngle, value); }

    /// <summary>Whether the road runs on from its last point back to its first, as a ring.</summary>
    [ExportGroup("Shape")]
    [Export]
    public bool Closed { get => _closed; set => Change(ref _closed, value); }

    /// <summary>
    /// A junction an end is joined to, if it is still there: one taken out of the scene (deleted, or
    /// undone) leaves that end loose, and saves as none, until it comes back.
    /// </summary>
    private Node3D? Joined(Node3D? junction) =>
        IsInstanceValid(junction) && Root(this) == Root(junction!) ? junction : null;

    /// <summary>The topmost node above one: out of the tree, two can only point at each other through a shared one.</summary>
    private static Node Root(Node node)
    {
        while (node.GetParent() is { } parent)
            node = parent;
        return node;
    }

    /// <summary>The junction or ramp the road starts at, its first point on it; none for a loose end.</summary>
    [ExportGroup("Junctions")]
    [Export]
    public Node3D? StartJunction
    {
        get => Joined(_startJunction);
        set => ChangeJunction(ref _startJunction, value, true);
    }

    /// <summary>The junction or ramp the road ends at, its last point on it; none for a loose end.</summary>
    [Export]
    public Node3D? EndJunction
    {
        get => Joined(_endJunction);
        set => ChangeJunction(ref _endJunction, value, false);
    }

    [ExportGroup("Materials")]
    [Export]
    public Material? SurfaceMaterial { get => _surfaceMaterial; set => Change(ref _surfaceMaterial, value); }

    [Export]
    public Material? WhiteLineMaterial { get => _whiteLineMaterial; set => Change(ref _whiteLineMaterial, value); }

    [Export]
    public Material? YellowLineMaterial { get => _yellowLineMaterial; set => Change(ref _yellowLineMaterial, value); }

    /// <summary>What the banks past its shoulders are made of; empty uses its surface's material.</summary>
    [Export]
    public Material? BankMaterial { get => _bankMaterial; set => Change(ref _bankMaterial, value); }

    /// <summary>What its viaducts and tunnels are made of; empty uses concrete.</summary>
    [Export]
    public Material? StructureMaterial { get => _structureMaterial; set => Change(ref _structureMaterial, value); }

    private RoadPathKind _pathKind = RoadPathKind.None;
    private float _pathWidth = 1f;

    /// <summary>
    /// Whether it is a path rather than a road (a plot's footpath or drive, kept under the plot, not
    /// in a road network), and of what: a strip of that laid on the ground along its points, as wide
    /// as <see cref="PathWidth"/>, with no lanes, lines, kerbs, signs or traffic; the ground is left as it is.
    /// </summary>
    [ExportGroup("Path")]
    [Export]
    public RoadPathKind PathKind { get => _pathKind; set => Change(ref _pathKind, value); }

    /// <summary>How wide it is as a path, in metres.</summary>
    [Export(PropertyHint.Range, "0.3,8,0.1,suffix:m")]
    public float PathWidth { get => _pathWidth; set => Change(ref _pathWidth, Mathf.Max(value, 0.3f)); }

    /// <summary>Whether it is a path rather than a road.</summary>
    public bool IsPath => _pathKind != RoadPathKind.None;

    public RoadCrossSection CrossSection
    {
        get
        {
            // A path: one strip its width, nothing either side.
            if (IsPath)
                return new(1, 0, _pathWidth, 0f);
            var (left, right) = SidewalkSides;
            return new(_forwardLanes, _backwardLanes, _laneWidth, _shoulderWidth, _medianWidth, _median, _shoulderHeight, _shoulderAngle,
                left ? _sidewalkWidth : 0f, right ? _sidewalkWidth : 0f);
        }
    }

    /// <summary>The points the road runs through, in order.</summary>
    public IReadOnlyList<RoadPoint> Points => GetChildren().OfType<RoadPoint>().ToList();

    /// <summary>Its whole centre line, sampled along it in world space; empty with fewer than two points or no lanes.</summary>
    public IReadOnlyList<RoadFrame> FullFrames()
    {
        var points = Points;
        if (points.Count < 2 || _forwardLanes + _backwardLanes == 0)
            return System.Array.Empty<RoadFrame>();
        return new RoadCurve(points.Select(p => p.ToCurvePoint()).ToList(), _closed).Frames(Spacing);
    }

    /// <summary>Its centre line where there is road: cut back at each end from the junction there.</summary>
    public IReadOnlyList<RoadFrame> Frames() => Cut(FullFrames());

    /// <summary>Its whole centre line cut back at each end from the junction there.</summary>
    private IReadOnlyList<RoadFrame> Cut(IReadOnlyList<RoadFrame> frames)
    {
        if (frames.Count < 2 || _closed || (StartJunction is not RoadJunction && EndJunction is not RoadJunction))
            return frames;
        var from = (StartJunction as RoadJunction)?.SetbackFor(this, true) ?? 0f;
        var to = frames[^1].Distance - ((EndJunction as RoadJunction)?.SetbackFor(this, false) ?? 0f);
        return RoadCurve.Trim(frames, from, to);
    }

    /// <summary>The ramps built into it.</summary>
    public IReadOnlyList<RoadRamp> Ramps => GetChildren().OfType<RoadRamp>().ToList();

    /// <summary>The viaducts and tunnels carrying it.</summary>
    public IReadOnlyList<RoadStructure> Structures => GetChildren().OfType<RoadStructure>().ToList();

    /// <summary>
    /// Whether guard rails run along its edges (not where it has a sidewalk): on a divided road of its own accord (Auto).
    /// They stop where ramps join it or leave, along its viaducts and tunnels (which have barriers of their own), and
    /// at its gaps (see RoadBarrierGap).
    /// </summary>
    [ExportGroup("Barriers")]
    [Export]
    public BarrierMode GuardRails { get => _guardRails; set => Change(ref _guardRails, value); }

    private BarrierMode _guardRails;

    /// <summary>Whether a barrier runs down its middle (two ways only): on a divided road of its own accord (Auto); not at its gaps.</summary>
    [Export]
    public BarrierMode MedianBarrier { get => _medianBarrier; set => Change(ref _medianBarrier, value); }

    private BarrierMode _medianBarrier;

    /// <summary>Its gaps in its barriers.</summary>
    public IReadOnlyList<RoadBarrierGap> BarrierGaps => GetChildren().OfType<RoadBarrierGap>().ToList();

    /// <summary>Whether guard rails run along its edges, its Guard Rails setting worked out (Auto: when divided).</summary>
    public bool HasGuardRails => _guardRails == BarrierMode.On || (_guardRails == BarrierMode.Auto && Divided);

    /// <summary>Whether it is divided: lanes both ways with a median between.</summary>
    private bool Divided => _forwardLanes > 0 && _backwardLanes > 0 && CrossSection.MedianWidth > 0f;

    /// <summary>How far one of its barriers stands from its centre line, to the right: its edges' a little in on its shoulders, its middle's on it.</summary>
    public float BarrierOffset(BarrierSide side)
    {
        var bands = CrossSection.Bands().Where(b => b.Kind != BandKind.Sidewalk).ToList();
        return side switch
        {
            BarrierSide.Left => bands.Min(b => b.Left) + RailIn,
            BarrierSide.Right => bands.Max(b => b.Right) - RailIn,
            _ => bands.Where(b => b.Kind == BandKind.Median).Select(b => b.Middle).DefaultIfEmpty(0f).First(),
        };
    }

    /// <summary>How far in from its paving's edge a guard rail stands, so its posts are on the shoulder.</summary>
    private const float RailIn = 0.4f;

    /// <summary>How far apart the points of a barrier are along it (its fence runs straight between them).</summary>
    private const float BarrierStep = 4f;

    private Node3D? _barriers;
    private string? _barriersKey;

    /// <summary>
    /// Its guard rails and median barrier, as fences of those kinds along it (see GuardRails, MedianBarrier): each
    /// a fence for each run between its gaps. Put up again only when what they follow has changed.
    /// </summary>
    private void PutUpBarriers(IReadOnlyList<RoadFrame> frames, IReadOnlyList<StructureSpec> structures)
    {
        if (frames.Count < 2)
            return;
        // A ring: on round its closing stretch, back to its start, too.
        frames = ToBuild(frames);
        var section = CrossSection;
        var sides = _guardRails == BarrierMode.On || (_guardRails == BarrierMode.Auto && Divided);
        var median = _forwardLanes > 0 && _backwardLanes > 0 && (_medianBarrier == BarrierMode.On || (_medianBarrier == BarrierMode.Auto && Divided));
        var gaps = BarrierGaps.Select(g => (g.Side, g.Start, g.End)).ToList();
        var ramps = Ramps.Select(r => r.Spec).ToList();
        var (from, to) = (frames[0].Distance, frames[^1].Distance);
        var rampRails = Ramps.Select(r => (r.Spec, r.GuardRails == BarrierMode.On || (r.GuardRails == BarrierMode.Auto && sides))).ToList();
        var key = string.Join("|", sides, median, section, from, to, string.Join(";", rampRails), frames[0].Position, frames[^1].Position, frames.Count,
            string.Join(";", gaps), string.Join(";", ramps), string.Join(";", structures), string.Join(";", frames.Where((_, i) => i % 8 == 0).Select(f => f.Position)));
        if (key == _barriersKey && _barriers is not null)
            return;
        _barriersKey = key;
        _barriers ??= AddInternal(new Node3D { Name = "Barriers" });
        foreach (var old in _barriers.GetChildren())
        {
            _barriers.RemoveChild(old);
            old.QueueFree();
        }

        var put = new List<(BarrierSide Side, RoadBarrierKind Kind, bool Flip)>();
        if (sides && section.SidewalkLeft <= 0f)
            put.Add((BarrierSide.Left, RoadBarrierKind.GuardRail, false));
        if (sides && section.SidewalkRight <= 0f)
            put.Add((BarrierSide.Right, RoadBarrierKind.GuardRail, true));
        if (median)
            put.Add((BarrierSide.Median, RoadBarrierKind.Barrier, false));

        foreach (var (side, kind, flip) in put)
        {
            // Left open: its gaps; for a guard rail, where a ramp on its side joins or leaves, and along viaducts and tunnels.
            var open = gaps.Where(g => g.Side == side).Select(g => (g.Start, g.End)).ToList();
            if (side != BarrierSide.Median)
            {
                var roadSide = side == BarrierSide.Left ? RoadSide.Left : RoadSide.Right;
                open.AddRange(structures.Select(s => (s.Start, s.Start + s.Length)));
            }

            // On a ring, a gap past its end carries on from its start.
            if (_closed)
                open = open.SelectMany(o => o.Item2 > to ? new[] { (o.Item1, to), (from, from + o.Item2 - to) } : new[] { o }).ToList();
            var runs = new List<(float From, float To)> { (from, to) };
            foreach (var (lo, hi) in open)
            {
                runs = runs.SelectMany(r => hi <= r.From || lo >= r.To ? new[] { r }
                    : new[] { (r.From, System.MathF.Min(lo, r.To)), (System.MathF.Max(hi, r.From), r.To) }.Where(x => x.Item2 - x.Item1 > 0.01f).ToArray()).ToList();
            }

            var offset = BarrierOffset(side);
            // Out past any ramp's lane on its side while that runs alongside (easing out over its taper), on round it.
            var sign = side == BarrierSide.Left ? -1f : side == BarrierSide.Right ? 1f : 0f;
            var sideRamps = side == BarrierSide.Median ? [] : ramps.Where(r => r.Side == (side == BarrierSide.Left ? RoadSide.Left : RoadSide.Right)).ToList();
            // (A ramp still joined to it there (no gap yet): its paving's edge is the ramp's outer edge, as the road's mesh has it.)
            // None where a ramp's lane lies along its edge (the ramp's own outer rail runs there, below), nor where the ramp
            // is coming away but not yet far enough for a rail between them: there the rail would cross its lane.
            // (Within its stretch only, its ends not: there the road's rail and the ramp's outer one meet, on the same spot.)
            bool RampThere(float d) => sideRamps.Any(r => d > r.Start + 0.01f && d < r.End - 0.01f && !RampClear(r, section, d, offset, sign));

            foreach (var (a, b) in runs.Where(r => r.To - r.From >= 2f))
            {
                // Finer where a ramp shapes its edge, so it stops and starts where it should.
                var step = sideRamps.Any(r => r.Start < b && r.End > a) ? RampRailStep : BarrierStep;
                var count = System.Math.Max(1, (int)System.MathF.Ceiling((b - a) / step));
                var piece = new List<Vector3>();
                // Its points, and each ramp's ends among them, so it stops (and starts) right where the ramp's rail does.
                var places = Enumerable.Range(0, count + 1).Select(k => a + (b - a) * k / count)
                    .Concat(sideRamps.SelectMany(r => new[] { r.Start, r.End }).Where(d => d > a && d < b)).Order().ToList();
                foreach (var d in places.Append(float.NaN))
                {
                    if (!float.IsNaN(d) && !RampThere(d))
                    {
                        piece.Add(ToLocal(RoadCurve.At(frames, d).Across(offset).ToGodot()));
                        continue;
                    }

                    if (piece.Count >= 2)
                        RoadLinks.AddBarrier(_barriers, $"{side}Barrier", kind, flip, [.. piece]);
                    piece.Clear();
                }
            }
        }

        // Each ramp, where it has parted from the road (joined, its lane is railed by the road's own, above): a rail along
        // its outer edge, and along its inner edge once there is room, at its paving's edges as the road's mesh has them,
        // along the road's own line.
        var full = FullFrames();
        foreach (var (spec, railed) in rampRails)
        {
            if (!railed || full.Count < 2)
                continue;
            var sign = spec.Side == RoadSide.Right ? 1f : -1f;
            var count = System.Math.Max(2, (int)System.MathF.Ceiling(spec.Length / RampRailStep));
            var along = Enumerable.Range(0, count + 1).Select(k => spec.Start + spec.Length * k / count).ToList();
            void Rail(System.Func<float, float?> across, bool flip)
            {
                // Each unbroken run of places it has, a fence.
                var run = new List<Vector3>();
                foreach (var d in along.Append(float.NaN))
                {
                    var x = float.IsNaN(d) ? null : across(d);
                    if (x is { } off)
                    {
                        run.Add(ToLocal(RoadCurve.At(full, d).Across(off).ToGodot()));
                        continue;
                    }

                    if (run.Count >= 2)
                        RoadLinks.AddBarrier(_barriers, "RampBarrier", RoadBarrierKind.GuardRail, flip, [.. run]);
                    run.Clear();
                }
            }

            // Outer: wherever its lane is, joined or not (the road's own rail leaves off there). Inner: once the gap
            // between it and the road leaves room for the rail.
            Rail(d => RoadRampShaper.Across(spec, section, d).EdgeOut - sign * RailIn, sign > 0f);
            var roadRail = BarrierOffset(spec.Side == RoadSide.Right ? BarrierSide.Right : BarrierSide.Left);
            Rail(d => RampClear(spec, section, d, roadRail, sign)
                ? RoadRampShaper.Across(spec, section, d).EdgeIn + sign * RailIn : null, sign < 0f);

            // A gore tip where those two rails begin (coming from the end it joins the road at), between them, facing
            // that end: the way traffic comes at it.
            var joinsAtEnd = RoadRampShaper.SeparatedAtStart(spec, section);
            var fromJoin = joinsAtEnd ? along.AsEnumerable().Reverse() : along;
            if (fromJoin.Cast<float?>().FirstOrDefault(d => RampClear(spec, section, d!.Value, roadRail, sign)) is { } nose)
            {
                var inner = RoadRampShaper.Across(spec, section, nose).EdgeIn + sign * RailIn;
                var frame = RoadCurve.At(full, nose);
                GoreTip.Put(_barriers, frame.Across((inner + roadRail) / 2f).ToGodot(), (joinsAtEnd ? frame.Forward : -frame.Forward).ToGodot());
            }
        }
    }

    /// <summary>
    /// Whether a ramp has come far enough away from its road at a distance along it for a rail along its inner edge to stand
    /// clear of the road's own rail (at this offset, on that side): from there both run, side by side, rather than crossing.
    /// </summary>
    private static bool RampClear(RampSpec ramp, RoadCrossSection section, float d, float roadRail, float sign)
    {
        if (RoadRampShaper.Profile(ramp, section, d).Gap <= 0.01f)
            return false;
        var inner = RoadRampShaper.Across(ramp, section, d).EdgeIn + sign * RailIn;
        return sign * (inner - roadRail) >= RailClearance;
    }

    /// <summary>How far apart a road's rail and a ramp's beside it have to be to both stand, in metres.</summary>
    private const float RailClearance = 0.3f;

    /// <summary>How far apart a rail's points are where a ramp shapes it, so it follows the paving's curve.</summary>
    private const float RampRailStep = 2f;

    /// <summary>Whether all of it is a viaduct, end to end (on a deck and pillars over the ground), whatever structures it has.</summary>
    [ExportGroup("Viaduct")]
    [Export]
    public bool Viaduct { get => _viaduct; set => Change(ref _viaduct, value); }

    private bool _viaduct;

    /// <summary>
    /// What its structures are: its RoadStructure nodes'; or, as a viaduct all along (see <see cref="Viaduct"/>), just the
    /// one from end to end, in their place (with them too, it was built twice over where they are, pillars and all).
    /// </summary>
    public List<StructureSpec> StructureSpecs()
    {
        if (_viaduct && FullFrames() is { Count: >= 2 } frames)
        {
            // As the first viaduct of its own is (its deck, barriers and pillars), if it has one.
            var like = Structures.Where(s => s.Kind == StructureKind.Viaduct).Select(s => (StructureSpec?)s.Spec).FirstOrDefault();
            var (from, length) = (frames[0].Distance, frames[^1].Distance - frames[0].Distance);
            return [like is { } own ? own with { Start = from, Length = length } : new StructureSpec(StructureKind.Viaduct, from, length)];
        }

        return Structures.Select(s => s.Spec).ToList();
    }

    /// <summary>Its parking along its sides (see RoadParking).</summary>
    public IReadOnlyList<RoadParking> Parking => GetChildren().OfType<RoadParking>().ToList();

    /// <summary>How high the network's terrain is under a point, if it has one there.</summary>
    public float? GroundAt(System.Numerics.Vector3 at)
    {
        if (Network?.Ground is not { } ground)
            return null;
        var height = ground.HeightAt(at.ToGodot());
        return float.IsNaN(height) ? null : height;
    }

    /// <summary>The network it is part of, if any.</summary>
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

    /// <summary>This road as an arm of the junction at its start or end: which way it leaves it, and what it is like across.</summary>
    public JunctionArm? ArmAt(bool start)
    {
        var frames = FullFrames();
        if (frames.Count < 2)
            return null;
        var forward = start ? frames[0].Forward : -frames[^1].Forward;
        var level = new System.Numerics.Vector3(forward.X, 0f, forward.Z);
        if (level.LengthSquared() < 1e-8f)
            return null;
        // Where the road really is a distance out from the junction, facing out from it.
        var length = frames[^1].Distance;
        RoadFrame Along(float distance)
        {
            if (start)
                return RoadCurve.At(frames, distance);
            var frame = RoadCurve.At(frames, length - distance);
            return frame with { Forward = -frame.Forward, Right = -frame.Right };
        }

        // Cut back no further than half way along it if a junction is at its other end too, so some of
        // it is left between them; nor right up to a loose end.
        var other = start ? EndJunction : StartJunction;
        var most = other is RoadJunction ? length / 2f - 1f : length - 2f;
        return new JunctionArm(System.Numerics.Vector3.Normalize(level), CrossSection, start, Along, most);
    }

    /// <summary>The path down each lane in the way its traffic goes, in world space.</summary>
    public IReadOnlyList<LanePath> LanePaths() => RoadLanes.Paths(Frames(), CrossSection, _closed);

    public override void _Ready()
    {
        _notifiedAt = GlobalTransform;
        SetNotifyTransform(true);
        // Points, ramps or structures added, taken away or put in another order; not the pieces of
        // itself it adds and takes away as it builds.
        // A method, not a lambda: the editor reconnects signals to methods after rebuilding the
        // scripts, but cannot to lambdas, and says so for every road each time.
        ChildOrderChanged += ChildrenChanged;
        QueueRebuild();
    }

    private void ChildrenChanged()
    {
        if (!_building)
            QueueRebuild();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTransformChanged && this.Moved(ref _notifiedAt))
            QueueRebuild();
    }

    private Transform3D? _notifiedAt;

    // Taken away: the viaducts over it stand pillars where it was.
    public override void _ExitTree()
    {
        Network?.FootprintRemoved(this);
        _maskedGround?.RemoveMask(MaskName);
        _maskedGround = null;
        // Taken away (deleted, say): the junctions it met shaped again round the roads left.
        (StartJunction as RoadJunction)?.QueueRebuild();
        (EndJunction as RoadJunction)?.QueueRebuild();
    }

    /// <summary>Rebuilds at the end of the frame, once, however many changes come before then.</summary>
    public void QueueRebuild()
    {
        if (_rebuildQueued || !IsInsideTree())
            return;
        _rebuildQueued = true;
        Callable.From(Rebuild).CallDeferred();
    }

    /// <summary>Whether it is building itself right now: adding and taking away its own pieces then is not a change to it.</summary>
    private bool _building;

    private void Rebuild()
    {
        _building = true;
        try
        {
            BuildNow();
        }
        finally
        {
            _building = false;
        }
    }

    private void BuildNow()
    {
        if (!IsInsideTree())
        {
            _rebuildQueued = false;
            return;
        }

        // Its junctions shape themselves round it, and it is cut back to them. One due to build
        // anyway (as the level loads) builds first, from where its roads' points are, so this is
        // cut right the first time instead of building twice. Otherwise they build after it: if
        // that changes how far back it is cut, they queue it to build again.
        var startBuilt = (StartJunction as RoadJunction)?.BuildIfQueued() == true;
        var endBuilt = (EndJunction as RoadJunction)?.BuildIfQueued() == true;
        _rebuildQueued = false;
        if (!startBuilt)
            (StartJunction as RoadJunction)?.RoadChanged();
        if (!endBuilt)
            (EndJunction as RoadJunction)?.RoadChanged();

        // An end on a ramp moved by hand moves the ramp's end with it; a cul-de-sac goes wherever its
        // road's end is; an end on a junction of several roads goes back onto it.
        // Not with a single point, which would be both ends at once, pulled to and fro between them.
        var ends = Points;
        if (ends.Count >= 2)
        {
            (StartJunction as RoadRamp)?.Follow(ends[0]);
            (EndJunction as RoadRamp)?.Follow(ends[^1]);
            (StartJunction as RoadJunction)?.KeepRoadEnd(this, ends[0]);
            (EndJunction as RoadJunction)?.KeepRoadEnd(this, ends[^1]);
            if (!_closed)
            {
                JoinParkingLot(ends[0], true);
                JoinParkingLot(ends[^1], false);
            }
        }

        _body ??= AddInternal(new StaticBody3D { Name = "Body" });
        _concrete ??= AddInternal(new MeshInstance3D { Name = "Structures" });
        var curvePoints = Points.Select(p => p.ToCurvePoint()).ToList();
        IReadOnlyList<RoadFrame> fullFrames = System.Array.Empty<RoadFrame>();
        var pointsAt = System.Array.Empty<float>();
        if (curvePoints.Count >= 2 && _forwardLanes + _backwardLanes > 0)
            fullFrames = new RoadCurve(curvePoints, _closed).Frames(Spacing, out pointsAt);
        var frames = Cut(fullFrames);
        if (frames.Count < 2)
        {
            ClearPieces();
            ClearCollision();
            _concrete.Mesh = null;
            _builtPoints = null;
            return;
        }

        if (IsPath)
        {
            BuildPath(frames);
            _builtPoints = null;
            if (Engine.IsEditorHint())
                UpdateGizmos();
            return;
        }

        _pathFaces = null;
        var ramps = Ramps;
        var structures = Structures;
        var rampSpecs = ramps.Select(r => r.Spec).ToList();
        var structureSpecs = StructureSpecs();
        var key = BuildKey(frames, fullFrames, rampSpecs, structureSpecs, curvePoints.Count);
        var toLocal = GlobalTransform.AffineInverse();

        // While its points are being dragged about in the editor, only the pieces round the change
        // build again, and the rest (collision, signs, what viaducts over it make of it) waits until
        // the dragging stops; otherwise all of it builds.
        var inPart = (Engine.IsEditorHint() || PartOutsideEditor) && !_wholeDue && key == _builtKey && BuildPart(curvePoints, pointsAt, frames, rampSpecs, structureSpecs, toLocal);
        var solids = inPart ? null : BuildWhole(frames, rampSpecs, structureSpecs, toLocal);
        _builtPoints = curvePoints;
        _builtPointsAt = pointsAt;
        _builtKey = key;

        ShowTunnelMask();
        var inside = new MeshData();
        var concrete = RoadStructureBuilder.Build(frames, CrossSection, structureSpecs, GroundAt, (at, half) => Network?.RoadUnder(at, half) == true, inside);
        var concreteMesh = new ArrayMesh();
        AddSurface(concreteMesh, concrete, _structureMaterial ?? DefaultMaterial("Concrete"), toLocal);
        AddSurface(concreteMesh, inside, DefaultMaterial("Tunnel-Inside"), toLocal);
        concrete.Append(inside);
        AddTunnelBars(concreteMesh, toLocal);
        _concrete.Mesh = concreteMesh.GetSurfaceCount() > 0 ? concreteMesh : null;

        // Its ramps' own roads follow where the ramps now end.
        var roads = ramps.Count > 0 ? Network?.Roads : null;
        foreach (var ramp in ramps)
            ramp.Refresh(fullFrames, roads);
        foreach (var structure in structures)
            structure.Refresh(fullFrames);
        // A fork at either end follows it (and puts its other roads' ends in place).
        foreach (var end in new[] { StartJunction, EndJunction })
            (end as RoadFork)?.QueueRebuild();
        foreach (var spot in Parking)
            spot.Refresh(fullFrames);

        if (inPart)
        {
            Settle();
        }
        else
        {
            _wholeDue = false;
            // Where it lies (its line and its ramps'), for viaducts over it to keep their pillars off it.
            Network?.FootprintChanged(this, FootprintStretches(frames, fullFrames, rampSpecs));

            // Collision from the surface and banks: the lines only stand a centimetre or two above it.
            ClearCollision();
            foreach (var solid in solids!)
            {
                if (solid.Indices.Count == 0)
                    continue;
                var faces = solid.Indices.Select(i => toLocal * solid.Vertices[i].ToGodot()).ToArray();
                _body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = faces } });
            }

            SetConcreteCollision(concrete, toLocal);

            PutUpSpeedSigns(frames, structureSpecs);
            PutUpGantries(frames);
            PutUpLampPosts(frames, structureSpecs);
            PutUpBarriers(frames, structureSpecs);

            // Clickable in the editor by its surface, as it is now.
            if (Engine.IsEditorHint())
                UpdateGizmos();

            // Its lanes have changed: the network's traffic lanes are worked out again when next wanted.
            Network?.TrafficChanged();
        }

        // Its smooth points face the way it runs through them, for turning by hand in the editor.
        if (Engine.IsEditorHint())
        {
            var points = Points;
            var curve = new RoadCurve(curvePoints, _closed);
            for (var i = 0; i < points.Count; i++)
                points[i].FaceAlong(curve.TangentAt(i).ToGodot());

            // An end at a junction holds the way it runs in: left smooth, it would swing whenever a
            // point near it moved, and turn the junction's arm with it.
            // Not where its neighbour is on top of it: there is no way through it to hold then, only
            // the curve's stand-in.
            if (!_closed && points.Count >= 2)
            {
                if (StartJunction is RoadJunction && !points[0].FollowRotation && points[0].GlobalPosition.DistanceTo(points[1].GlobalPosition) > 0.01f)
                    points[0].Pin(curve.TangentAt(0).ToGodot());
                if (EndJunction is RoadJunction && !points[^1].FollowRotation && points[^1].GlobalPosition.DistanceTo(points[^2].GlobalPosition) > 0.01f)
                    points[^1].Pin(curve.TangentAt(points.Count - 1).ToGodot());
            }
        }
    }

    /// <summary>
    /// What it can be clicked by in the editor, as triangles in its own space: its surface and
    /// banks, and its sidewalks, as its collision has them.
    /// </summary>
    public Vector3[] PickFaces() => _pathFaces is not null ? _pathFaces : _body is null
        ? System.Array.Empty<Vector3>()
        : _body.GetChildren().OfType<CollisionShape3D>().Select(s => s.Shape).OfType<ConcavePolygonShape3D>().SelectMany(s => s.Data).ToArray();

    /// <summary>How far its origin may be from the middle of its points before it is moved there, in metres.</summary>
    private const float CentreSlack = 0.5f;

    /// <summary>
    /// Moves its origin to the middle of its points (the middle of the box round them), leaving
    /// them, and everything else on it, where they are; nothing if it is there already. The road
    /// tool does, when it has made or changed a road. In the editor, as a step to undo: undoing an
    /// earlier move of one of its points by its gizmo (which puts it back where it was on the road)
    /// then still puts it back in the right place.
    /// </summary>
    public void Centre()
    {
        if (!IsInsideTree() || Points is not { Count: > 0 } points)
            return;
        var box = new Aabb(points[0].GlobalPosition, Vector3.Zero);
        foreach (var point in points)
            box = box.Expand(point.GlobalPosition);
        var middle = box.GetCenter();
        if (middle.DistanceTo(GlobalPosition) <= CentreSlack)
            return;

#if TOOLS
        if (Engine.IsEditorHint())
        {
            var undo = EditorInterface.Singleton.GetEditorUndoRedo();
            undo.CreateAction($"Centre {Name}", UndoRedo.MergeMode.Disable, this);
            undo.AddDoMethod(this, MethodName.MoveOrigin, middle);
            undo.AddUndoMethod(this, MethodName.MoveOrigin, GlobalPosition);
            undo.CommitAction();
            return;
        }
#endif
        MoveOrigin(middle);
    }

    /// <summary>Puts its origin here, everything on it staying where it is.</summary>
    public void MoveOrigin(Vector3 to)
    {
        var children = GetChildren().OfType<Node3D>().Select(c => (c, c.GlobalTransform)).ToList();
        GlobalPosition = to;
        foreach (var (child, kept) in children)
            child.GlobalTransform = kept;
    }

    /// <summary>How long a road is built in one piece, in metres: dragging a point builds only the pieces round it again.</summary>
    private const float PieceLength = 250f;

    /// <summary>How long its points have to be left alone before a road built in part (while dragged) is built whole again, in milliseconds.</summary>
    private const ulong SettleTime = 300;

    /// <summary>Its surface, lines and banks, piece by piece along it: from and to how far along, and the mesh.</summary>
    private readonly List<(float From, float To, MeshInstance3D Mesh)> _pieces = new();

    private MeshInstance3D? _concrete;

    /// <summary>Its points, how far along it each was, and everything else its shape hangs on, as last built: to tell what a change touched.</summary>
    private List<CurvePoint>? _builtPoints;
    private float[]? _builtPointsAt;
    private string? _builtKey;

    /// <summary>Whether it is to be built whole next time, not in part: it has not been yet, or has only been in part and has been left alone since.</summary>
    private bool _wholeDue = true;

    private ulong _settleAt;
    private bool _settling;

    /// <summary>Builds in part outside the editor too: only for measuring how long dragging takes, from a script run headless.</summary>
    private static bool PartOutsideEditor { get; set; }

    /// <summary>
    /// Everything but where its points are that its pieces hang on: how it is cut back at each end,
    /// what it is like across, its ramps and structures, what its ends meet, its materials. A
    /// change to any of them builds it whole.
    /// </summary>
    private string BuildKey(IReadOnlyList<RoadFrame> frames, IReadOnlyList<RoadFrame> fullFrames, IReadOnlyList<RampSpec> ramps, IReadOnlyList<StructureSpec> structures, int points) =>
        string.Join("|", _closed, CrossSection, System.MathF.Round(frames[0].Distance, 3), System.MathF.Round(fullFrames[^1].Distance - frames[^1].Distance, 3),
            string.Join(";", ramps), string.Join(";", structures), StartJunction is null, EndJunction is null, points, string.Join(";", Openings(frames)),
            _surfaceMaterial?.GetInstanceId(), _whiteLineMaterial?.GetInstanceId(), _yellowLineMaterial?.GetInstanceId(), _bankMaterial?.GetInstanceId());

    /// <summary>
    /// Where its sidewalks are left open for a way into a parking lot: at each of the network's
    /// parking lot corners that is an entrance and is on the back of one of its sidewalks, as wide
    /// as the corner says, centred on it.
    /// </summary>
    public IReadOnlyList<SidewalkOpening> Openings(IReadOnlyList<RoadFrame> frames)
    {
        if (Network is not { } network || frames.Count < 2 || (CrossSection.SidewalkLeft <= 0f && CrossSection.SidewalkRight <= 0f))
            return System.Array.Empty<SidewalkOpening>();
        var openings = new List<SidewalkOpening>();
        foreach (var lot in network.ParkingLots)
        {
            foreach (var corner in lot.Corners.Where(c => c.Entrance))
            {
                if (OpeningAt(frames, corner.GlobalPosition.ToNumerics()) is { } at)
                    openings.Add(new SidewalkOpening(at.Left, at.Middle - corner.EntranceWidth / 2f, at.Middle + corner.EntranceWidth / 2f));
            }
        }

        return openings;
    }

    /// <summary>Whether a parking lot's entrance corner here opens its sidewalk (the lot's way in from it).</summary>
    public bool OpensAt(System.Numerics.Vector3 at) =>
        (CrossSection.SidewalkLeft > 0f || CrossSection.SidewalkRight > 0f) && Frames() is { Count: >= 2 } frames && OpeningAt(frames, at) is not null;

    /// <summary>Where an entrance corner here opens its sidewalk, if it does: on which side, and how far along.</summary>
    private (bool Left, float Middle)? OpeningAt(IReadOnlyList<RoadFrame> frames, System.Numerics.Vector3 at)
    {
        var bands = CrossSection.Bands();
        var (left, right) = (bands[0].Left, bands[^1].Right);
        foreach (var frame in frames)
        {
            var offset = at - frame.Position;
            var along = System.Numerics.Vector3.Dot(offset, frame.Forward);
            if (System.MathF.Abs(along) > Spacing * 0.75f || System.MathF.Abs(offset.Y) > 2.5f)
                continue;
            var across = System.Numerics.Vector3.Dot(offset, frame.Right);
            var onLeft = CrossSection.SidewalkLeft > 0f && System.MathF.Abs(across - left) < OpeningReach;
            var onRight = CrossSection.SidewalkRight > 0f && System.MathF.Abs(across - right) < OpeningReach;
            if (onLeft || onRight)
                return (onLeft, frame.Distance + along);
        }

        return null;
    }

    /// <summary>How near the back of its sidewalk a parking lot's entrance corner has to be to open it, in metres.</summary>
    private const float OpeningReach = 0.5f;

    /// <summary>Its frames to build its surface along: a ring's go on round to its start again, at its whole length.</summary>
    private IReadOnlyList<RoadFrame> ToBuild(IReadOnlyList<RoadFrame> frames)
    {
        if (!_closed)
            return frames;
        var round = frames[^1].Distance + System.Numerics.Vector3.Distance(frames[^1].Position, frames[0].Position);
        return frames.Append(frames[0] with { Distance = round }).ToList();
    }

    /// <summary>
    /// Builds it whole, in pieces of about <see cref="PieceLength"/>; returns what its collision is
    /// made from (the pieces' surfaces and banks).
    /// </summary>
    private List<MeshData> BuildWhole(IReadOnlyList<RoadFrame> frames, IReadOnlyList<RampSpec> ramps, IReadOnlyList<StructureSpec> structures, Transform3D toLocal)
    {
        ClearPieces();
        var along = ToBuild(frames);
        var (from, to) = (along[0].Distance, along[^1].Distance);
        var count = System.Math.Max(1, (int)System.MathF.Round((to - from) / PieceLength));
        var solids = new List<MeshData>();
        for (var k = 0; k < count; k++)
        {
            var (a, b) = (from + (to - from) * k / count, from + (to - from) * (k + 1) / count);
            var meshes = BuildPiece(along, a, b, ramps, structures, toLocal, out var mesh);
            _pieces.Add((a, b, mesh));
            solids.Add(meshes.Surface);
            solids.Add(meshes.Banks);
            solids.Add(meshes.Sidewalks);
        }

        return solids;
    }

    /// <summary>
    /// Builds again only the pieces round the points that moved since it was last built, the rest
    /// kept and moved along by however much longer or shorter the road got; whether it could (not
    /// when its points were added or taken away, or a ring changed across its start).
    /// </summary>
    private bool BuildPart(List<CurvePoint> points, float[] pointsAt, IReadOnlyList<RoadFrame> frames, IReadOnlyList<RampSpec> ramps, IReadOnlyList<StructureSpec> structures, Transform3D toLocal)
    {
        if (_builtPoints is null || _builtPointsAt is null || _builtPoints.Count != points.Count || _pieces.Count == 0)
            return false;
        var changed = Enumerable.Range(0, points.Count).Where(i => !_builtPoints[i].Equals(points[i])).ToList();
        if (changed.Count == 0)
            return false;

        // A point's place shapes the curve from two points before it to one after it (the ways
        // through its neighbours lean towards it); the spans between those change.
        var spans = _closed ? points.Count : points.Count - 1;
        var (first, last) = (changed.Min() - 2, changed.Max() + 1);
        if (_closed && (first < 0 || last > spans - 1))
            return false;
        (first, last) = (System.Math.Max(first, 0), System.Math.Min(last, spans - 1));
        var (wasFrom, wasTo) = (_builtPointsAt[first], _builtPointsAt[last + 1]);
        if (System.MathF.Abs(pointsAt[first] - wasFrom) > 1e-3f)
            return false;
        var longer = pointsAt[last + 1] - wasTo;

        // The pieces that stretch covered, built again (in pieces as long again); those past it keep
        // their meshes, further along.
        var from = _pieces.FindIndex(p => p.To > wasFrom + 1e-3f);
        var to = _pieces.FindLastIndex(p => p.From < wasTo - 1e-3f);
        if (from < 0 || to < from)
            return false;
        var (a, b) = (_pieces[from].From, _pieces[to].To + longer);
        for (var i = from; i <= to; i++)
        {
            RemoveChild(_pieces[i].Mesh);
            _pieces[i].Mesh.QueueFree();
        }

        var along = ToBuild(frames);
        var count = System.Math.Max(1, (int)System.MathF.Round((b - a) / PieceLength));
        var rebuilt = new List<(float, float, MeshInstance3D)>();
        for (var k = 0; k < count; k++)
        {
            var (pieceFrom, pieceTo) = (a + (b - a) * k / count, a + (b - a) * (k + 1) / count);
            BuildPiece(along, pieceFrom, pieceTo, ramps, structures, toLocal, out var mesh);
            rebuilt.Add((pieceFrom, pieceTo, mesh));
        }

        for (var i = to + 1; i < _pieces.Count; i++)
            _pieces[i] = (_pieces[i].From + longer, _pieces[i].To + longer, _pieces[i].Mesh);
        _pieces.RemoveRange(from, to - from + 1);
        _pieces.InsertRange(from, rebuilt);
        return true;
    }

    /// <summary>Builds its surface, lines and banks from one distance along it to another as a mesh of its own.</summary>
    private RoadMeshes BuildPiece(IReadOnlyList<RoadFrame> along, float from, float to, IReadOnlyList<RampSpec> ramps, IReadOnlyList<StructureSpec> structures, Transform3D toLocal,
        out MeshInstance3D mesh)
    {
        var part = RoadCurve.Trim(along, from, to);
        // Only the ramps along this piece: one past it would be squeezed onto its end.
        var here = ramps.Where(r => r.End > from && r.Start < to).ToList();
        var meshes = RoadMeshBuilder.Build(part, CrossSection, false, here, structures,
            !_closed && StartJunction is null && from <= along[0].Distance + 1e-3f,
            !_closed && EndJunction is null && to >= along[^1].Distance - 1e-3f, Openings(along),
            Parking.Select(p => p.Spec).Where(p => p.End > from && p.Start < to).ToList());
        var arrays = new ArrayMesh();
        AddSurface(arrays, meshes.Surface, _surfaceMaterial ?? DefaultMaterial("Asphalt"), toLocal);
        AddSurface(arrays, meshes.WhiteLines, _whiteLineMaterial ?? DefaultMaterial("Road-Line-White"), toLocal);
        AddSurface(arrays, meshes.YellowLines, _yellowLineMaterial ?? DefaultMaterial("Road-Line-Yellow"), toLocal);
        AddSurface(arrays, meshes.Banks, _bankMaterial ?? DefaultMaterial("Asphalt"), toLocal);
        AddSurface(arrays, meshes.Sidewalks, DefaultMaterial("Sidewalk"), toLocal);
        mesh = AddInternal(new MeshInstance3D { Name = "Piece", Mesh = arrays });
        return meshes;
    }

    /// <summary>A path's surface, in its own space, for picking it by in the editor (it has no collision: the ground under it is walked on).</summary>
    private Vector3[]? _pathFaces;

    /// <summary>How far over the ground a path's surface lies, in metres: clear of it, not to flicker with it.</summary>
    private const float PathLift = 0.05f;

    /// <summary>How far a path's sloping sides reach down into the ground, and out from its edge, in metres.</summary>
    private const float PathSkirt = 0.3f;

    /// <summary>How far apart, at most, a path's surface follows the ground, along it and across it, in metres.</summary>
    private const float PathGrain = 0.5f;

    /// <summary>How long a stepping stone is along its path, and how far it is from one's start to the next's, in metres.</summary>
    private const float StoneLength = 0.45f, StoneStep = 0.75f;

    /// <summary>
    /// Built as a path: a strip its width along its line, each side on the ground under it (the
    /// terrain's, wherever it is in the level) and a little over it; stepping stones, a slab at a
    /// time. No collision, kerbs, lines, signs or lamps, and the ground is not fitted to it.
    /// </summary>
    private void BuildPath(IReadOnlyList<RoadFrame> frames)
    {
        // Its one piece kept and its mesh rewritten in place, not taken away and put in again:
        // in a level the size of the island, any node coming or going makes the editor rebuild
        // its whole scene tree, seconds after every stroke of the terrain tool under a path.
        var kept = KeepOnePathPiece();
        ClearCollision();
        if (_concrete is not null)
            _concrete.Mesh = null;
        var toLocal = GlobalTransform.AffineInverse();
        var ground = RoadLinks.Ground?.Invoke(LevelRoot());
        var half = _pathWidth / 2f;
        // Fitted to the ground again when it changes under it (see GroundChanged).
        ground?.Follow(new Callable(this, MethodName.GroundChanged));
        _refitDue = false;
        Vector3 OnGround(Vector3 at)
        {
            var height = ground is not null && ground.HeightAt(at) is var h && !float.IsNaN(h) ? h : at.Y;
            return toLocal * (at with { Y = height + PathLift });
        }

        // A line across it so far along: points no more than half a metre apart, each on the ground.
        var across = Mathf.Max(1, Mathf.CeilToInt(_pathWidth / PathGrain));
        Vector3[] Line(float along)
        {
            var frame = RoadCurve.At(frames, along);
            var at = frame.Position.ToGodot();
            var right = (frame.Right.ToGodot() with { Y = 0f }).Normalized();
            return [.. Enumerable.Range(0, across + 1).Select(c => OnGround(at + right * (-half + _pathWidth * c / across)))];
        }

        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        var faces = new List<Vector3>();
        // The strip between two lines across it, a quad for each pair of points, wound to face up (clockwise seen from above).
        void Strip(Vector3[] a, float aAlong, Vector3[] b, float bAlong)
        {
            for (var c = 0; c < across; c++)
            {
                var (u0, u1) = (_pathWidth * c / across, _pathWidth * (c + 1) / across);
                var corners = new[] { (a[c], new Vector2(u0, aAlong)), (a[c + 1], new Vector2(u1, aAlong)), (b[c + 1], new Vector2(u1, bAlong)), (b[c], new Vector2(u0, bAlong)) };
                foreach (var i in new[] { 0, 2, 1, 0, 3, 2 })
                {
                    tool.SetNormal(Vector3.Up);
                    tool.SetUV(corners[i].Item2);
                    tool.AddVertex(corners[i].Item1);
                    faces.Add(corners[i].Item1);
                }
            }
        }

        // A side sloping down and out into the ground from an edge of it (from p to q), like a verge,
        // so where the ground lies a little below it there is neither a gap under it nor a wall. Drawn
        // only: not for picking it.
        void Side(Vector3 p, Vector3 q)
        {
            var outward = (q - p).Cross(Vector3.Down) with { Y = 0f };
            outward = outward.Normalized();
            var slope = (Vector3.Down + outward) * PathSkirt;
            var (pd, qd) = (p + slope, q + slope);
            var normal = (outward + Vector3.Up).Normalized();
            foreach (var corner in new[] { p, qd, q, p, pd, qd })
            {
                tool.SetNormal(normal);
                tool.SetUV(new Vector2(corner.X + corner.Z, corner.Y));
                tool.AddVertex(corner);
            }
        }

        // From one place along it to another, in steps of half a metre at most; its sides and ends going down into the ground.
        void Run(float from, float to)
        {
            var steps = Mathf.Max(1, Mathf.CeilToInt((to - from) / PathGrain));
            var before = Line(from);
            for (var c = 0; c < across; c++)
                Side(before[c + 1], before[c]);
            for (var k = 1; k <= steps; k++)
            {
                var (was, now) = (Mathf.Lerp(from, to, (k - 1f) / steps), Mathf.Lerp(from, to, (float)k / steps));
                var line = Line(now);
                Strip(before, was, line, now);
                Side(before[0], line[0]);
                Side(line[across], before[across]);
                before = line;
            }

            for (var c = 0; c < across; c++)
                Side(before[c], before[c + 1]);
        }

        var (start, end) = (frames[0].Distance, frames[^1].Distance);
        if (_pathKind == RoadPathKind.SteppingStones)
        {
            for (var along = start + (StoneStep - StoneLength) / 2f; along + StoneLength <= end; along += StoneStep)
                Run(along, along + StoneLength);
        }
        else
        {
            Run(start, end);
        }

        var mesh = kept?.Mesh as ArrayMesh ?? new ArrayMesh();
        mesh.ClearSurfaces();
        tool.Commit(mesh);
        if (mesh.GetSurfaceCount() == 0)
        {
            _pathFaces = [];
            return;
        }

        mesh.SurfaceSetMaterial(0, _surfaceMaterial ?? DefaultMaterial(PathMaterial(_pathKind)));
        var piece = kept ?? AddInternal(new MeshInstance3D { Name = "Piece", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        // A footpath: not drawn from far off (see DrawDistance).
        RoadDrawDistance.LimitSmall(piece);
        if (piece.Mesh != mesh)
            piece.Mesh = mesh;
        _pieces.Add((start, end, piece));
        // Where it lies, seen from above, a little past its edges: ground changing there is under it.
        var first = frames[0].Position.ToGodot();
        _pathArea = new Rect2(first.X, first.Z, 0f, 0f);
        foreach (var frame in frames)
            _pathArea = _pathArea.Expand(new Vector2(frame.Position.X, frame.Position.Z));
        _pathArea = _pathArea.Grow(half + 1f);
        _pathFaces = [.. faces];
    }

    private Rect2 _pathArea;
    private bool _refitDue;

    /// <summary>
    /// The paths waiting to be laid on the ground again once a stroke is done: kept here so the
    /// stroke's end goes straight to them instead of through every node in the level. By instance
    /// id, not the paths themselves: tool code keeps no Godot objects in static fields.
    /// </summary>
    private static readonly HashSet<ulong> RefitWaiting = [];

    /// <summary>
    /// The ground changed somewhere: a path it changed under is laid on it again. Not while the
    /// ground is still being shaped (the left mouse button held, in the editor): then once the
    /// stroke is done, when the terrain tool says so (see <see cref="RefitPaths"/>).
    /// </summary>
    private void GroundChanged(Rect2 area)
    {
        if (!IsPath || !IsInsideTree() || !area.Intersects(_pathArea))
            return;
        if (Engine.IsEditorHint() && Input.IsMouseButtonPressed(MouseButton.Left))
        {
            _refitDue = true;
            RefitWaiting.Add(GetInstanceId());
        }
        else
            QueueRebuild();
    }

    /// <summary>Every path in a level whose ground changed while it was being shaped: laid on it again, now the shaping is done.</summary>
    public static void RefitPaths(Node? level)
    {
        if (level is null)
            return;
        foreach (var id in RefitWaiting.ToList())
        {
            if (InstanceFromId(id) is not Road path || !path._refitDue)
            {
                RefitWaiting.Remove(id);
                continue;
            }

            // A path in another open scene waits for a stroke there.
            if (!level.IsAncestorOf(path))
                continue;
            path._refitDue = false;
            path.QueueRebuild();
            RefitWaiting.Remove(id);
        }
    }

    /// <summary>The material (in Assets/Materials, by name) a kind of path is made of, unless it is given another.</summary>
    private static string PathMaterial(RoadPathKind kind) => kind switch
    {
        RoadPathKind.Gravel => "Path-Gravel",
        RoadPathKind.Concrete => "Path-Concrete",
        RoadPathKind.SteppingStones => "Path-SteppingStones",
        _ => "Path-PavingStones",
    };

    /// <summary>The level it is in: the scene being edited, or played.</summary>
    private Node? LevelRoot()
    {
#if TOOLS
        if (Engine.IsEditorHint())
            return EditorInterface.Singleton.GetEditedSceneRoot();
#endif
        // The level it is in: the current scene if it is in that, else the topmost node over it (a
        // level loaded by the menu builds before it becomes the current scene).
        if (GetTree().CurrentScene is { } current && (current == this || current.IsAncestorOf(this)))
            return current;
        Node top = this;
        while (top.GetParent() is { } parent && parent != GetTree().Root)
            top = parent;
        return top;
    }

    /// <summary>
    /// A path's piece, if it has one, kept (it has only ever the one) and the list of pieces
    /// emptied; any others it has, taken away.
    /// </summary>
    private MeshInstance3D? KeepOnePathPiece()
    {
        var pieces = GetChildren(true).OfType<MeshInstance3D>().Where(m => m.Name.ToString().StartsWith("Piece")).ToList();
        _pieces.Clear();
        foreach (var extra in pieces.Skip(1))
        {
            RemoveChild(extra);
            extra.QueueFree();
        }

        return pieces.FirstOrDefault();
    }

    private void ClearPieces()
    {
        foreach (var (_, _, mesh) in _pieces)
        {
            RemoveChild(mesh);
            mesh.QueueFree();
        }

        _pieces.Clear();

        // And any it lost track of: the list is emptied when the editor rebuilds the scripts, the
        // pieces it held left behind, which would show under (or over) the ones built next.
        foreach (var stale in GetChildren(true).OfType<MeshInstance3D>().Where(m => m.Name.ToString().StartsWith("Piece")).ToList())
        {
            RemoveChild(stale);
            stale.QueueFree();
        }
    }

    /// <summary>
    /// Something under its viaducts changed (a road or junction built, moved or taken away): only
    /// their concrete (pillars and all) is built again, kept off what is under them now, not the
    /// whole road.
    /// </summary>
    public void RefreshStructures()
    {
        // Once at the end of the frame, however many things under it change before then.
        if (_structuresQueued || !IsInsideTree())
            return;
        _structuresQueued = true;
        Callable.From(BuildStructures).CallDeferred();
    }

    private bool _structuresQueued;

    /// <summary>The ground its mask is on, if it has one.</summary>
    private IRoadGround? _maskedGround;

    private string MaskName => $"road:{GetInstanceId()}";

    /// <summary>The ground masked where its tunnels are, as they and it are now.</summary>
    public void ShowTunnelMask()
    {
        var ground = Network?.Ground;
        var frames = Frames();
        var tunnels = frames.Count < 2 ? [] : StructureSpecs().Where(s => s.Kind == StructureKind.Tunnel).ToList();
        if (ground is null || (tunnels.Count == 0 && _maskedGround is null))
            return;
        var masks = tunnels.Select(t => RoadStructureBuilder.TunnelMask(frames, CrossSection, t)).ToList();
        ground.SetMask(MaskName, [.. masks.SelectMany(m => m.Shapes)], [.. masks.SelectMany(m => m.Tops)]);
        _maskedGround = ground;
    }

    /// <summary>Its tunnels' light bars: where the light of each is, and the light it gives.</summary>
    public TunnelLight[] TunnelLightPlaces { get; private set; } = [];

    /// <summary>The bars' material in each colour used.</summary>
    private static readonly System.Collections.Generic.Dictionary<Color, Material?> BarMaterials = [];

    private static Material? BarMaterial(Color colour)
    {
        if (BarMaterials.TryGetValue(colour, out var known))
            return known;
        var material = DefaultMaterial("Tunnel-Light")?.Duplicate() as StandardMaterial3D;
        if (material is not null)
        {
            material.AlbedoColor = colour.Lerp(Colors.White, 0.3f);
            material.Emission = colour;
        }

        return BarMaterials[colour] = material;
    }

    /// <summary>How far out from its bar a bar's light is, so the wall and roof beside it are not burnt out.</summary>
    private const float TunnelLightOut = 0.5f;

    /// <summary>Its tunnels' light bars added to a mesh, and its network told where they are now.</summary>
    private void AddTunnelBars(ArrayMesh mesh, Transform3D toLocal)
    {
        var had = TunnelLightPlaces.Length;
        var lights = new System.Collections.Generic.List<TunnelLight>();
        foreach (var tunnel in StructureSpecs().Where(s => s.Kind == StructureKind.Tunnel))
        {
            var colour = new Color(tunnel.LightRed, tunnel.LightGreen, tunnel.LightBlue);
            var (bars, places) = RoadStructureBuilder.TunnelBars(Frames(), CrossSection, [tunnel]);
            AddSurface(mesh, bars, BarMaterial(colour), toLocal);
            lights.AddRange(places.Select(p => new TunnelLight(new Transform3D(Basis.Identity, p.At.ToGodot() + p.Facing.ToGodot() * TunnelLightOut), colour, tunnel.LightEnergy, tunnel.LightRange)));
        }

        TunnelLightPlaces = [.. lights];
        if (had != 0 || lights.Count != 0)
            Network?.LampsChanged();
    }

    private void BuildStructures()
    {
        _structuresQueued = false;
        // Building whole anyway: its structures with it.
        if (!IsInsideTree() || _concrete is null || _body is null || _rebuildQueued)
            return;
        var frames = Frames();
        if (frames.Count < 2)
            return;
        var toLocal = GlobalTransform.AffineInverse();
        var inside = new MeshData();
        var concrete = RoadStructureBuilder.Build(frames, CrossSection, StructureSpecs(), GroundAt,
            (at, half) => Network?.RoadUnder(at, half) == true, inside);
        ShowTunnelMask();
        var mesh = new ArrayMesh();
        AddSurface(mesh, concrete, _structureMaterial ?? DefaultMaterial("Concrete"), toLocal);
        AddSurface(mesh, inside, DefaultMaterial("Tunnel-Inside"), toLocal);
        concrete.Append(inside);
        AddTunnelBars(mesh, toLocal);
        _concrete.Mesh = mesh.GetSurfaceCount() > 0 ? mesh : null;
        SetConcreteCollision(concrete, toLocal);
    }

    /// <summary>The name its structures' collision goes by among its body's shapes, to be swapped on its own.</summary>
    private const string ConcreteShape = "Concrete";

    private void SetConcreteCollision(MeshData concrete, Transform3D toLocal)
    {
        if (_body is null)
            return;
        if (_body.GetNodeOrNull(ConcreteShape) is { } old)
        {
            _body.RemoveChild(old);
            old.QueueFree();
        }

        if (concrete.Indices.Count == 0)
            return;
        var faces = concrete.Indices.Select(i => toLocal * concrete.Vertices[i].ToGodot()).ToArray();
        _body.AddChild(new CollisionShape3D { Name = ConcreteShape, Shape = new ConcavePolygonShape3D { Data = faces } });
    }

    private void ClearCollision()
    {
        if (_body is null)
            return;
        foreach (var shape in _body.GetChildren())
        {
            _body.RemoveChild(shape);
            shape.QueueFree();
        }
    }

    /// <summary>Builds it whole once its points have been left alone for <see cref="SettleTime"/>.</summary>
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
        // By Godot's Connect, not C#'s += (whose hookup Godot tries to undo when this is freed first, and fails).
        GetTree().CreateTimer(wait).Connect(SceneTreeTimer.SignalName.Timeout, Callable.From(() =>
        {
            if (!IsInstanceValid(this))
                return;
            if (IsInsideTree() && Godot.Time.GetTicksMsec() < _settleAt)
            {
                WaitToSettle();
                return;
            }

            _settling = false;
            _wholeDue = true;
            QueueRebuild();
        }));
    }

    /// <summary>Where it lies, as viaducts over it need to know: its centre line, and each of its ramps', with their widths.</summary>
    public IReadOnlyList<FootprintStretch> FootprintStretches() =>
        Frames() is { Count: >= 2 } frames ? FootprintStretches(frames, FullFrames(), Ramps.Select(r => r.Spec).ToList()) : System.Array.Empty<FootprintStretch>();

    private IReadOnlyList<FootprintStretch> FootprintStretches(IReadOnlyList<RoadFrame> frames, IReadOnlyList<RoadFrame> fullFrames, IReadOnlyList<RampSpec> ramps)
    {
        var stretches = new List<FootprintStretch>();
        var bands = CrossSection.Bands();
        if (bands.Count > 0)
            stretches.Add(new FootprintStretch(frames, bands[0].Left, bands[^1].Right));
        // And as far out as its parking sets its side back, along it.
        foreach (var spot in Parking.Select(p => p.Spec))
        {
            if (bands.Count == 0 || RoadCurve.Trim(fullFrames, spot.Start, spot.End) is not { Count: >= 2 } along)
                continue;
            var (left, right) = (bands[0].Left, bands[^1].Right);
            stretches.Add(spot.Side == RoadSide.Right ? new FootprintStretch(along, left, right + spot.Depth) : new FootprintStretch(along, left - spot.Depth, right));
        }
        foreach (var ramp in ramps)
        {
            var own = ramp.Section.Bands();
            if (own.Count > 0)
                stretches.Add(new FootprintStretch(RoadRampShaper.BranchFrames(fullFrames, ramp, CrossSection), own[0].Left, own[^1].Right));
        }

        return stretches;
    }

    /// <summary>Adds a mesh made by the road maths to an ArrayMesh as a surface, moved into a node's own space.</summary>
    public static void AddSurface(ArrayMesh mesh, MeshData data, Material? material, Transform3D toLocal)
    {
        if (data.Indices.Count == 0)
            return;

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices.Select(v => toLocal * v.ToGodot()).ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = data.Normals.Select(n => (toLocal.Basis * n.ToGodot()).Normalized()).ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = data.UVs.Select(uv => uv.ToGodot()).ToArray();
        arrays[(int)Mesh.ArrayType.Index] = data.Indices.ToArray();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
    }

    /// <summary>One of the road materials in Assets/Materials, if it is there and loads.</summary>
    public static Material? DefaultMaterial(string name)
    {
        var path = $"res://addons/unpleasant_roads/Materials/{name}.tres";
        return ResourceLoader.Exists(path) ? ResourceLoader.Load<Material>(path) : null;
    }

    /// <summary>How far into a road from a junction or ramp its speed limit sign stands, in metres.</summary>
    private const float SpeedSignIn = 20f;

    private Node3D? _signs;

    /// <summary>How much further in a speed sign may go to stand on the ground rather than on a viaduct, in metres.</summary>
    private const float SpeedSignSlide = 40f;

    /// <summary>How far apart a road's speed sign and the direction sign ahead of it have to be, at least, in metres.</summary>
    private const float SignsApart = 30f;

    /// <summary>
    /// Puts up a speed limit sign for traffic coming onto it from each end joined to a junction or
    /// ramp: a little way in, on that traffic's right, facing it. Loose ends and rings have none.
    /// </summary>
    private void PutUpSpeedSigns(IReadOnlyList<RoadFrame> frames, IReadOnlyList<StructureSpec> structures)
    {
        _signs ??= AddInternal(new Node3D { Name = "Signs" });
        foreach (var old in _signs.GetChildren())
        {
            _signs.RemoveChild(old);
            old.QueueFree();
        }

        if (frames.Count < 2 || frames[^1].Distance - frames[0].Distance < SpeedSignIn * 2f)
            return;
        var scene = ResourceLoader.Exists("res://addons/unpleasant_roads/Scenes/Sign-SpeedLimit.tscn") ? ResourceLoader.Load<PackedScene>("res://addons/unpleasant_roads/Scenes/Sign-SpeedLimit.tscn") : null;
        if (scene is null)
            return;
        var bands = CrossSection.Bands();
        var limit = Mathf.RoundToInt(EffectiveSpeedLimit).ToString();
        // Where traffic comes onto it, and which way it then goes: at a junction or ramp at its start
        // (forward), at its end (backward), and a little past where each entrance ramp joins it.
        var places = new List<(float Distance, bool Forward)>();
        // Not on a road too short to have its sign and, at its far end, a junction's direction sign apart.
        var roomy = frames[^1].Distance - frames[0].Distance >= SpeedSignIn + RoadJunction.DirectionSignBefore + SignsApart;
        if (StartJunction is not null && _forwardLanes > 0 && (roomy || EndJunction is not RoadJunction))
            places.Add((frames[0].Distance + SpeedSignIn, true));
        if (EndJunction is not null && _backwardLanes > 0 && (roomy || StartJunction is not RoadJunction))
            places.Add((frames[^1].Distance - SpeedSignIn, false));
        var section = CrossSection;
        var length = frames[^1].Distance;
        foreach (var ramp in Ramps.Where(r => r.Kind == RampKind.Entrance))
        {
            var spec = ramp.Spec;
            var forward = spec.Side == RoadSide.Right ? _forwardLanes > 0 : _backwardLanes == 0;
            var joinedAt = RoadRampShaper.SeparatedAtStart(spec, section) ? spec.End : spec.Start;
            var distance = forward ? joinedAt + SpeedSignIn : joinedAt - SpeedSignIn;
            // Round a ring, on round past its start; on an open road, only along it.
            if (_closed)
                distance = (distance % length + length) % length;
            if (distance > frames[0].Distance && distance < length)
                places.Add((distance, forward));
        }

        // On the right of the traffic: going forward, the road's right; backward, its left.
        foreach (var (at, forward) in places)
        {
            // Off any viaduct or tunnel: further on, the way its traffic goes, until on the ground; if
            // it never is near enough, on the barrier's line, out of the lane.
            var distance = at;
            for (var slid = 0f; slid < SpeedSignSlide && structures.Any(s => s.Covers(distance)); slid += 5f)
                distance += forward ? 5f : -5f;
            if (distance <= frames[0].Distance || distance >= length)
                distance = at;
            var frame = RoadCurve.At(frames, distance);
            var carried = structures.Any(s => s.Covers(distance));
            var kerb = carried ? RoadSignals.CarriedKerb : RoadSignals.Kerb;
            // On the edge where the ground drops away beside it.
            // (Out past any parking there, its side set out as far.)
            var across = forward
                ? bands[^1].Right + ParkedOut(distance, RoadSide.Right) + RoadSignals.KerbBeside(Network?.Ground, frame, bands[^1].Right, 1f, kerb)
                : bands[0].Left - ParkedOut(distance, RoadSide.Left) - RoadSignals.KerbBeside(Network?.Ground, frame, bands[0].Left, -1f, kerb);
            var travelling = (forward ? frame.Forward : -frame.Forward).ToGodot() with { Y = 0f };
            if (scene.Instantiate() is not RoadSign sign)
                continue;
            sign.Text = limit;
            _signs.AddChild(sign);
            RoadDrawDistance.LimitSmall(sign);
            sign.GlobalTransform = new Transform3D(Basis.LookingAt(travelling.Normalized(), Vector3.Up), frame.Across(across).ToGodot());
        }
    }

    /// <summary>How far before an exit its overhead sign stands, in metres.</summary>
    public const float GantryBefore = 300f;

    /// <summary>
    /// Puts up an overhead sign across it a way before each exit, where two or more lanes go that
    /// way: the road's name over those lanes, and the exit's (with how far off it is) over the
    /// outer lane that leads to it.
    /// </summary>
    private void PutUpGantries(IReadOnlyList<RoadFrame> frames)
    {
        if (_signs is null || frames.Count < 2 || Network is not { } network)
            return;
        var scene = ResourceLoader.Exists("res://addons/unpleasant_roads/Scenes/HighwayGantry.tscn") ? ResourceLoader.Load<PackedScene>("res://addons/unpleasant_roads/Scenes/HighwayGantry.tscn") : null;
        if (scene is null)
            return;
        var section = CrossSection;
        var bands = section.Bands();
        var middle = (bands[0].Left + bands[^1].Right) / 2f;
        var length = frames[^1].Distance;
        foreach (var ramp in Ramps.Where(r => r.Kind == RampKind.Exit))
        {
            var spec = ramp.Spec;
            var forward = spec.Side == RoadSide.Right ? _forwardLanes > 0 : _backwardLanes == 0;
            var direction = forward ? LaneDirection.Forward : LaneDirection.Backward;
            var lanes = bands.Where(b => b.Kind == BandKind.Lane && b.Direction == direction).OrderBy(b => b.Middle).ToList();
            if (lanes.Count < 2)
                continue;
            var joinedAt = RoadRampShaper.SeparatedAtStart(spec, section) ? spec.End : spec.Start;
            var distance = forward ? joinedAt - GantryBefore : joinedAt + GantryBefore;
            if (_closed)
                distance = (distance % length + length) % length;
            if (distance <= frames[0].Distance || distance >= length)
                continue;
            if (scene.Instantiate() is not HighwayGantry gantry)
                continue;

            // Across as the traffic sees it: its right is the road's right going forward, its left going backward.
            float Seen(float offset) => forward ? offset - middle : middle - offset;
            var outer = forward ? lanes[^1] : lanes[0];
            var branch = network.Roads.FirstOrDefault(r => r.StartJunction == ramp);
            var name = branch?.StreetName ?? "";
            gantry.Span = section.Width + 3f;
            gantry.MainText = StreetName;
            gantry.MainAt = Seen((lanes[0].Left + lanes[^1].Right) / 2f);
            gantry.ExitText = string.IsNullOrWhiteSpace(name) ? RoadSignals.Distance(GantryBefore) : $"{name}\n{RoadSignals.Distance(GantryBefore)}";
            gantry.ExitAt = Seen(outer.Middle);
            _signs.AddChild(gantry);
            RoadDrawDistance.LimitSmall(gantry);
            var frame = RoadCurve.At(frames, distance);
            var travelling = (forward ? frame.Forward : -frame.Forward).ToGodot() with { Y = 0f };
            gantry.GlobalTransform = new Transform3D(Basis.LookingAt(travelling.Normalized(), Vector3.Up), frame.Across(middle).ToGodot());
        }
    }

    /// <summary>How far a lamp post stands in from the curb on a sidewalk, in metres.</summary>
    private const float LampIn = 0.6f;

    /// <summary>How far from its ends (and the junctions there) its lamp posts keep, and from a parking lot's entrance, in metres.</summary>
    private const float LampClearOfEnds = 10f;
    private const float LampClearOfOpenings = 2f;

    /// <summary>Its lamp posts: drawn a square of the ground at a time (see RoadDrawDistance.Chunks), each square not from far off.</summary>
    private Node3D? _lamps;
    private StaticBody3D? _lampBody;

    /// <summary>
    /// Where its lamp posts stand, in the world: each one's place, its arm reaching along its +z
    /// out over the road. Its network lights those nearest the camera. (A Godot list, kept when the
    /// editor rebuilds the scripts.)
    /// </summary>
    public Godot.Collections.Array<Transform3D> LampPlaces { get; private set; } = new();

    /// <summary>
    /// Puts up its lamp posts, as <see cref="LampPosts"/> and <see cref="LampSpacing"/> have them: one
    /// mesh drawing them all, and a body with a shape for each pole.
    /// </summary>
    private void PutUpLampPosts(IReadOnlyList<RoadFrame> frames, IReadOnlyList<StructureSpec> structures)
    {
        _lamps ??= AddInternal(new Node3D { Name = "LampPosts" });
        foreach (var old in _lamps.GetChildren())
        {
            _lamps.RemoveChild(old);
            old.QueueFree();
        }
        _lampBody ??= AddInternal(new StaticBody3D { Name = "LampBody" });
        PhysicsServer3D.BodyClearShapes(_lampBody.GetRid());
        // Where the road is, its poles' shapes placed from there: at the world's origin, it would
        // stretch the road's box in the editor all the way out to there.
        _lampBody.Transform = Transform3D.Identity;
        LampPlaces = new Godot.Collections.Array<Transform3D>();
        var (left, right, staggered) = LampSides;
        if (frames.Count < 2 || (!left && !right) || Network?.LampMesh() is not { } mesh)
        {
            Network?.LampsChanged();
            return;
        }

        var bands = CrossSection.Bands();
        var openings = Openings(frames);
        var (start, end) = (frames[0].Distance + (_closed ? 0f : LampClearOfEnds), frames[^1].Distance - (_closed ? _lampSpacing / 2f : LampClearOfEnds));
        var places = new List<Transform3D>();
        foreach (var (onLeft, first) in new[] { (false, 0f), (true, staggered ? _lampSpacing / 2f : 0f) })
        {
            if (onLeft ? !left : !right)
                continue;
            var band = onLeft ? bands[0] : bands[^1];
            var sidewalk = band.Kind == BandKind.Sidewalk;
            var up = sidewalk ? RoadCrossSection.CurbHeight : 0f;
            // Evenly along it, each side's first half a spacing in from where the posts may start.
            var count = Mathf.FloorToInt((end - start) / _lampSpacing);
            var offset = start + ((end - start) - count * _lampSpacing) / 2f + first;
            for (var k = 0; k <= count; k++)
            {
                var distance = offset + k * _lampSpacing;
                if (distance < start || distance > end || structures.Any(s => s.Covers(distance))
                    || openings.Any(o => o.Left == onLeft && distance > o.From - LampClearOfOpenings && distance < o.To + LampClearOfOpenings))
                    continue;
                var frame = RoadCurve.At(frames, distance);
                // On the sidewalk by the curb; with none, out past the shoulder where the signs stand
                // (on the shoulder's edge where the ground drops away beside it).
                var across = onLeft
                    ? sidewalk ? band.Right - LampIn : band.Left - RoadSignals.KerbBeside(Network?.Ground, frame, band.Left, -1f, RoadSignals.Kerb)
                    : sidewalk ? band.Left + LampIn : band.Right + RoadSignals.KerbBeside(Network?.Ground, frame, band.Right, 1f, RoadSignals.Kerb);
                // Its arm out over the road: its +z towards the road's middle.
                var inward = (onLeft ? frame.Right : -frame.Right).ToGodot() with { Y = 0f };
                if (inward.LengthSquared() < 1e-6f)
                    continue;
                // Out past any parking there: on the sidewalk behind it.
                across += onLeft ? -ParkedOut(distance, RoadSide.Left) : ParkedOut(distance, RoadSide.Right);
                var foot = frame.Across(across).ToGodot() + Vector3.Up * up;
                places.Add(new Transform3D(Basis.LookingAt(-inward.Normalized(), Vector3.Up), foot));
            }
        }

        var toLocal = GlobalTransform.AffineInverse();
        foreach (var place in places)
        {
            LampPlaces.Add(place);
            PhysicsServer3D.BodyAddShape(_lampBody.GetRid(), RoadNetwork.LampPoleShape.GetRid(), toLocal * place * RoadNetwork.LampPoleAt);
        }

        foreach (var chunk in RoadDrawDistance.Chunks(places))
        {
            var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = chunk.Count };
            for (var i = 0; i < chunk.Count; i++)
                multi.SetInstanceTransform(i, toLocal * chunk[i]);
            var drawn = new MultiMeshInstance3D { Name = "Chunk", Multimesh = multi };
            _lamps.AddChild(drawn);
            RoadDrawDistance.LimitSmall(drawn);
        }

        Network?.LampsChanged();
    }

    /// <summary>Its lamp posts drawn with the network's lamp post mesh as it is now (the unlit lamps' look changed).</summary>
    public void RedrawLamps()
    {
        if (Network?.LampMesh() is not { } mesh)
            return;
        foreach (var drawn in _lamps?.GetChildren().OfType<MultiMeshInstance3D>() ?? [])
        {
            if (drawn.Multimesh is { } multi)
                multi.Mesh = mesh;
        }
    }

    /// <summary>How far out its side is set at a distance along it for its parking there, if any.</summary>
    private float ParkedOut(float distance, RoadSide side)
    {
        var most = 0f;
        foreach (var parking in Parking)
        {
            var spec = parking.Spec;
            if (spec.Side == side)
                most = Mathf.Max(most, spec.DepthAt(distance));
        }

        return most;
    }

    private T AddInternal<T>(T node) where T : Node
    {
        AddChild(node, false, InternalMode.Back);
        return node;
    }

    /// <summary>
    /// A loose end put on a parking lot's edge joins it, and goes onto its edge, at its height; an end
    /// joined to one stays on its edge, and comes away from it only when taken well off it.
    /// </summary>
    private void JoinParkingLot(RoadPoint end, bool start)
    {
        var current = start ? StartJunction : EndJunction;
        if ((current is not null && current is not ParkingLot) || Network is not { } network)
            return;
        var at = end.GlobalPosition.ToNumerics();
        ParkingLot? lot = null;
        (System.Numerics.Vector3 Point, float Distance)? edge = null;
        foreach (var candidate in network.ParkingLots)
        {
            // One not built yet (as the level loads) leaves an end joined to it as it is.
            if (candidate.Edge.Count < 3)
            {
                if (candidate == current)
                    return;
                continue;
            }

            if (candidate.NearestEdge(at) is not { } near || near.Distance > (candidate == current ? ParkingLot.LeaveDistance : ParkingLot.JoinDistance))
                continue;
            if (edge is null || near.Distance < edge.Value.Distance)
                (lot, edge) = (candidate, near);
        }

        if (lot != current)
        {
            if (start)
                StartJunction = lot;
            else
                EndJunction = lot;
            (current as ParkingLot)?.QueueRebuild();
        }

        if (lot is null || edge is not { } onEdge)
            return;
        var to = onEdge.Point.ToGodot();
        if (!end.GlobalPosition.Near(to))
            end.GlobalPosition = to;
        lot.QueueRebuild();
    }

    private void ChangeJunction(ref Node3D? field, Node3D? value, bool start)
    {
        var old = field;
        field = value;
        if (Reloading)
            return;
        // Joined to a junction, that end of it moves onto the junction (not while the scene loads,
        // when its points have yet to come).
        if (value is RoadJunction junction && junction.IsInsideTree() && Points is { Count: > 0 } points)
        {
            var end = start ? points[0] : points[^1];
            if (!end.GlobalPosition.Near(junction.GlobalPosition))
                end.GlobalPosition = junction.GlobalPosition;
        }

        if (old is RoadJunction oldJunction && IsInstanceValid(oldJunction))
            oldJunction.QueueRebuild();
        (value as RoadJunction)?.QueueRebuild();
        if (value is RoadRamp ramp)
            ramp.Road?.QueueRebuild();
        if (old is RoadFork oldFork && IsInstanceValid(oldFork))
            oldFork.QueueRebuild();
        (value as RoadFork)?.QueueRebuild();
        QueueRebuild();
    }

    private void Change<T>(ref T field, T value)
    {
        field = value;
        if (!Reloading)
            QueueRebuild();
    }
}
