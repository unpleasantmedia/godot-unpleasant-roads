using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using V3 = System.Numerics.Vector3;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>What stands between a fork's two lane groups, along the gore, once they have parted: as it is carried or railed (Auto), nothing, a guard rail, or a concrete wall as a viaduct's.</summary>
public enum GoreBarrier
{
    Auto,
    None,
    Rail,
    Concrete,
}

/// <summary>
/// Where a one-way road (its trunk) parts into two going the same way (a split), or two come together into
/// one (a merge), built into the end of the trunk like a ramp: over <see cref="Length"/> its lanes run on, the
/// traffic's left <see cref="LeftLanes"/> of them in one group and the rest in another, the two easing apart
/// evenly, each half the way, until there is a gore between them, painted with chevrons; and there the two branch roads start (or, merging, end), lane for lane. A split's trunk ends at it
/// and its branches start at it; a merge's branches end at it and its trunk starts at it. It keeps its branches'
/// ends in place, and their lanes as many as they take; moved or turned, it takes the trunk's end with it.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_roads/Icons/Road-Fork.svg")]
public partial class RoadFork : Node3D, IFoliageBuffered
{
    private int _leftLanes = 1;
    private float _length = 10f;
    /// <summary>
    /// A new branch made like its trunk (see Shared), once, as it is made: as fast as the trunk really is (its Auto
    /// worked out, as a branch's would go by its fewer lanes, and its sidewalks, buildings and lamps by that), and
    /// the rest of the trunk's settings. Free to be changed after.
    /// </summary>
    public static void Adopt(Road branch, Road trunk)
    {
        branch.SpeedLimit = trunk.EffectiveSpeedLimit;
        foreach (var name in Shared)
            branch.Set(name, trunk.Get(name));
    }

    /// <summary>The trunk's settings a new branch takes too: all but its name, signs and lanes' count (and median, a fork's roads going one way).</summary>
    private static readonly StringName[] Shared =
    [
        Road.PropertyName.LaneWidth, Road.PropertyName.ShoulderWidth, Road.PropertyName.ShoulderHeight, Road.PropertyName.ShoulderAngle,
        Road.PropertyName.Buildings, Road.PropertyName.Sidewalks, Road.PropertyName.SidewalkWidth,
        Road.PropertyName.LampPosts, Road.PropertyName.LampSpacing, Road.PropertyName.GuardRails, Road.PropertyName.MedianBarrier,
        Road.PropertyName.Viaduct, Road.PropertyName.SurfaceMaterial, Road.PropertyName.WhiteLineMaterial, Road.PropertyName.YellowLineMaterial,
        Road.PropertyName.BankMaterial, Road.PropertyName.StructureMaterial,
    ];

    private bool _rebuildQueued;
    private string? _builtKey;
    private bool _placing;

    /// <summary>Whether it has placed itself (built) yet, and how it faced when it last did: a turn by hand is told from that.</summary>
    private bool _built;
    private Basis _placedBasis = Basis.Identity;
    private Transform3D? _notifiedAt;
    private MeshInstance3D? _mesh;
    private StaticBody3D? _body;

    /// <summary>How many of the trunk's lanes go to (or come from) the branch on the traffic's left; the rest the right.</summary>
    [Export(PropertyHint.Range, "1,8,1")]
    public int LeftLanes { get => _leftLanes; set { _leftLanes = Math.Max(value, 1); QueueRebuild(); } }

    /// <summary>How long it is, from the trunk to where its branches start.</summary>
    [Export(PropertyHint.Range, "5,300,1,suffix:m")]
    public float Length { get => _length; set { _length = Mathf.Max(value, 5f); QueueRebuild(); } }

    private float _laneWidth;
    private float _shoulderWidth;

    private BarrierMode _guardRails;

    /// <summary>Whether guard rails run along its edges (not on a viaduct, which has walls): as its trunk's (Auto), always, or never.</summary>
    [Export]
    public BarrierMode GuardRails { get => _guardRails; set { _guardRails = value; QueueRebuild(); } }

    private Node3D? _rails;
    private GoreBarrier _gore;

    /// <summary>
    /// What stands along its gore, between its two lane groups once they part: Auto, a concrete wall on a viaduct and a
    /// guard rail where it has guard rails; or none, a guard rail, or a concrete wall, whatever it is.
    /// </summary>
    [Export]
    public GoreBarrier GoreBarrier { get => _gore; set { _gore = value; QueueRebuild(); } }

    /// <summary>How wide its lanes are; 0 for its trunk's.</summary>
    [Export(PropertyHint.Range, "0,6,0.05,suffix:m")]
    public float LaneWidth { get => _laneWidth; set { _laneWidth = Mathf.Max(value, 0f); QueueRebuild(); } }

    /// <summary>How wide its shoulders are, along its outer edges; 0 for its trunk's.</summary>
    [Export(PropertyHint.Range, "0,5,0.05,suffix:m")]
    public float ShoulderWidth { get => _shoulderWidth; set { _shoulderWidth = Mathf.Max(value, 0f); QueueRebuild(); } }

    /// <summary>Whether it has a foliage buffer of its own (see <see cref="FoliageBuffer"/>), not its network's.</summary>
    [ExportGroup("Foliage")]
    [Export]
    public bool OwnFoliageBuffer
    {
        get => _ownFoliageBuffer;
        set
        {
            _ownFoliageBuffer = value;
            FoliageBufferChanged();
        }
    }

    private bool _ownFoliageBuffer;

    /// <summary>How far past its edges no foliage is painted, in metres (with Own Foliage Buffer on; else its network's).</summary>
    [Export(PropertyHint.Range, "0,100,0.5,or_greater,suffix:m")]
    public float FoliageBuffer
    {
        get => _foliageBuffer;
        set
        {
            _foliageBuffer = Mathf.Max(0f, value);
            FoliageBufferChanged();
        }
    }

    private float _foliageBuffer;

    /// <summary>How far past its edges no foliage is painted: its own, or its network's.</summary>
    public float FoliageBufferUsed => _ownFoliageBuffer ? _foliageBuffer : Network?.FoliageBuffer ?? 0f;

    /// <summary>Its buffer changed: told on (see RoadLinks.FootprintChanged), as where it lies is.</summary>
    public void FoliageBufferChanged()
    {
        if (!IsInsideTree())
            return;
        // Not built since its scripts were reloaded (its outline forgotten): built again, which tells on where it lies.
        if (Outline.Count < 3 || Network is not { } network)
            QueueRebuild();
        else
            network.FootprintChanged(this, [RoadNetwork.Outline(Outline)]);
    }

    /// <summary>Its banks, as its trunk's: how far down (up, if negative) they go, and how far out.</summary>
    public float BankHeight { get; private set; }

    /// <summary>Whether it is carried as a viaduct (its trunk is one where it meets it): on a deck and pillars, with no banks, and no ground fitted under it.</summary>
    public bool Carried { get; private set; }

    public float BankWidth { get; private set; }

    /// <summary>Its outline, in the world: for the ground to be fitted under it, and for clicking it.</summary>
    public IReadOnlyList<V3> Outline { get; private set; } = [];

    /// <summary>Its lanes through it: each from the road it comes in on (and that road's lane) to the road it goes on along, with its points.</summary>
    public IReadOnlyList<(Road From, int FromLane, Road To, int ToLane, IReadOnlyList<V3> Points)> Lanes { get; private set; } = [];

    public RoadNetwork? Network => GetParent() as RoadNetwork ?? GetParent()?.GetParent() as RoadNetwork;

    /// <summary>Its trunk (ending at it to split, starting at it to merge) and whether it splits.</summary>
    public (Road Road, bool Splits)? Trunk()
    {
        foreach (var road in Network?.Roads ?? [])
        {
            var (ends, starts) = (road.EndJunction == this, road.StartJunction == this);
            if (ends == starts)
                continue;
            // The trunk: the one road at its end the others are not alike to; found as the one whose other kin are two on its far side.
            var others = Network!.Roads.Count(r => r != road && (ends ? r.StartJunction == this : r.EndJunction == this));
            if (others == 2)
                return (road, ends);
        }

        return null;
    }

    public override void _Ready()
    {
        SetNotifyTransform(true);
        _notifiedAt = GlobalTransform;
        QueueRebuild();
    }

    // Gone: no longer where viaducts over it keep their pillars off.
    public override void _ExitTree() => Network?.FootprintRemoved(this);

    public override void _Notification(int what)
    {
        if (what != NotificationTransformChanged || !this.Moved(ref _notifiedAt) || _placing)
            return;
        // Not yet built (just placed, facing nowhere in particular yet): not a move by hand; it places itself as it builds.
        if (!_built)
        {
            QueueRebuild();
            return;
        }

        // Moved or turned by hand: its trunk's end comes too, turned to run the way it now faces (and it then builds from there).
        if (Trunk() is { } trunk && trunk.Road.Points is { Count: >= 2 } points)
        {
            var end = trunk.Splits ? points[^1] : points[0];
            end.GlobalPosition = GlobalPosition;
            // Turned (not just moved): the trunk's end turned to match. It faces along its stretch: a split's trunk runs
            // on that way into it, a merge's runs on out of it the other way.
            var facing = -GlobalBasis.Z with { Y = 0f };
            var was = -_placedBasis.Z with { Y = 0f };
            if (facing.LengthSquared() > 1e-6f && was.LengthSquared() > 1e-6f && facing.Normalized().Dot(was.Normalized()) < 0.99995f)
                end.Pin(trunk.Splits ? facing : -facing);
        }

        QueueRebuild();
    }

    public void QueueRebuild()
    {
        if (_rebuildQueued || !IsInsideTree())
            return;
        _rebuildQueued = true;
        Callable.From(Rebuild).CallDeferred();
    }

    /// <summary>How far one group of lanes has moved aside, so far along it: easing out, level at both ends.</summary>
    private static float Eased(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private void Rebuild()
    {
        _rebuildQueued = false;
        if (!IsInsideTree() || Trunk() is not { } trunkOf)
            return;
        var (trunk, splits) = trunkOf;
        var frames = trunk.FullFrames();
        if (frames.Count < 2 || trunk.BackwardLanes > 0 && trunk.ForwardLanes > 0)
            return;
        var network = Network!;
        var branches = network.Roads.Where(r => r != trunk && (splits ? r.StartJunction == this : r.EndJunction == this)).ToList();
        if (branches.Count != 2)
            return;
        // Built already from just this: nothing to do (its roads rebuilding ask it again after it moves their ends).
        var endFrame = splits ? frames[^1] : frames[0];
        // Carried, where a viaduct of its trunk reaches its end there: as that viaduct is.
        var endAlong = splits ? frames[^1].Distance - 0.5f : frames[0].Distance + 0.5f;
        var deck = trunk.StructureSpecs().Where(s => s.Kind == StructureKind.Viaduct && s.Covers(endAlong)).Select(s => (StructureSpec?)s).FirstOrDefault();
        var key = string.Join("|", deck, endFrame.Position, endFrame.Forward, trunk.CrossSection, _leftLanes, _length, splits, _laneWidth, _shoulderWidth, _guardRails, trunk.HasGuardRails, _gore,
            trunk.EffectiveSpeedLimit, string.Join(";", Shared.Select(n => trunk.Get(n).ToString())),
            string.Join(";", branches.Select(b => (b.GetInstanceId(), b.ForwardLanes, b.BackwardLanes,
                b.Points is { Count: > 0 } p ? (splits ? p[0] : p[^1]).GlobalPosition : Vector3.Zero))));
        // (Built without an outline, it is built again: its foliage buffer and pillars' room go by it.)
        if (key == _builtKey && _mesh is not null && Outline.Count >= 3)
            return;
        _builtKey = key;

        // Its frame: from the trunk's end, on the way it runs (a split), or back the way it came (a merge).
        var end = splits ? frames[^1] : frames[0];
        var origin = end.Position;
        var way = splits ? end.Forward : -end.Forward;
        // Across: the trunk's own right, so the trunk's lanes lie where they are on it.
        var right = end.Right;
        V3 At(float s, float x) => origin + way * s + right * x;
        _placing = true;
        GlobalTransform = new Transform3D(Basis.LookingAt(way.ToGodot() with { Y = 0f }, Vector3.Up), origin.ToGodot());
        _notifiedAt = GlobalTransform;
        _placedBasis = GlobalBasis;
        _built = true;
        _placing = false;

        // The trunk's lanes across it, the traffic's left first (a merge's traffic goes the other way along its frame).
        var section = trunk.CrossSection;
        var lanes = section.Bands().Where(b => b.Kind == BandKind.Lane).OrderBy(b => b.Middle).ToList();
        var total = lanes.Count;
        if (total < 2)
            return;
        // Its own lane and shoulder widths, where set; the trunk's otherwise. Its lanes laid out across it at its own width, about its middle.
        var width = _laneWidth > 0f ? _laneWidth : section.LaneWidth;
        var shoulder = _shoulderWidth > 0f ? _shoulderWidth : section.ShoulderWidth;
        if (_laneWidth > 0f)
            lanes = Enumerable.Range(0, total).Select(i => new RoadBand(BandKind.Lane, (i - total / 2f) * width, (i + 1 - total / 2f) * width, LaneDirection.Forward)).ToList();
        var leftCount = Math.Clamp(_leftLanes, 1, total - 1);
        // Traffic's left is the trunk's left (lanes from the left, going forward on it).
        var groups = new[] { lanes.Take(leftCount).ToList(), lanes.Skip(leftCount).ToList() };
        // Which group eases away, and which way across (the trunk's right for the traffic's right).
        // Each group half the way apart: the left one to the left, the right one to the right.
        var aside = (2f * shoulder + 1f) / 2f;
        float Off(int group, float s) => (group == 0 ? -aside : aside) * Eased(s / _length);

        // Its branches: the one more to the traffic's left (along the trunk's left) takes the left group.
        // By where each one's end on it lies (which it keeps in place), then by the point next to that (for two
        // not yet placed): not by their far ends, which may be anywhere (a road joined on runs who knows where).
        float Across(Road b, int fromEnd) => b.Points is { Count: > 0 } p
            ? V3.Dot(p[Math.Clamp(splits ? fromEnd : p.Count - 1 - fromEnd, 0, p.Count - 1)].GlobalPosition.ToNumerics() - origin, right) : 0f;
        var ordered = branches.OrderBy(b => MathF.Round(Across(b, 0), 1)).ThenBy(b => Across(b, 1)).ToList();
        for (var g = 0; g < 2; g++)
        {
            var road = ordered[g];
            var group = groups[g];
            if (road.ForwardLanes != group.Count)
                road.ForwardLanes = group.Count;
            if (road.BackwardLanes != 0)
                road.BackwardLanes = 0;
            if (road.Points is not { Count: > 0 } points)
                continue;
            // Its end at the fork's far end, in the middle of its lanes there, facing the way its traffic goes.
            var middle = (group[0].Left + group[^1].Right) / 2f + Off(g, _length);
            var at = At(_length, middle).ToGodot();
            var point = splits ? points[0] : points[^1];
            // Facing on the way the fork's end runs, so its road leaves (or meets) it straight on. Only where it is not
            // there already, by its facing alone (comparing whole turns, a sloped one was never quite equal, and
            // each setting had the road and the fork rebuild each other without end).
            if (!point.GlobalPosition.Near(at))
                point.GlobalPosition = at;
            var facing = (splits ? way : -way).ToGodot().Normalized();
            if (!point.FollowRotation || (-point.GlobalBasis.Z).Normalized().Dot(facing) < 0.9999f)
                point.Pin(facing);
        }

        // Along it a metre at a time: its two groups' paving, and their lines.
        var steps = Math.Max(4, (int)MathF.Ceiling(_length));
        var surface = new MeshData();
        var white = new MeshData();
        var outline = new List<V3>();
        var leftSide = new List<V3>();
        var rightSide = new List<V3>();
        var edges = new List<(float S, float LeftOuter, float LeftInner, float RightInner, float RightOuter)>();
        for (var k = 0; k <= steps; k++)
        {
            var s = _length * k / steps;
            var (inL, inR) = (groups[0][^1].Right + Off(0, s), groups[1][0].Left + Off(1, s));
            var (outL, outR) = (groups[0][0].Left + Off(0, s) - shoulder, groups[1][^1].Right + Off(1, s) + shoulder);
            var gap = inR - inL;
            // Their inner shoulders meet halfway while the gap is too narrow for both.
            var (edgeL, edgeR) = gap <= 2f * shoulder ? ((inL + inR) / 2f, (inL + inR) / 2f) : (inL + shoulder, inR - shoulder);
            edges.Add((s, outL, edgeL, edgeR, outR));
        }

        // Its banks, as the trunk's: down (or up) from each outer edge, and into the gap between the groups once
        // they have parted (no further each side than halfway across it).
        Carried = deck is not null;
        BankHeight = Carried ? 0f : section.ShoulderHeight;
        BankWidth = Carried ? 0f : section.BankWidth;
        var banks = new MeshData();
        var down = V3.UnitY * BankHeight;
        for (var k = 0; k + 1 < edges.Count; k++)
        {
            var (a, b) = (edges[k], edges[k + 1]);
            Strip(surface, At(a.S, a.LeftOuter), At(a.S, a.LeftInner), At(b.S, b.LeftInner), At(b.S, b.LeftOuter));
            Strip(surface, At(a.S, a.RightInner), At(a.S, a.RightOuter), At(b.S, b.RightOuter), At(b.S, b.RightInner));
            BankStrip(banks, At(a.S, a.LeftOuter), At(b.S, b.LeftOuter), At(b.S, b.LeftOuter - BankWidth) - down, At(a.S, a.LeftOuter - BankWidth) - down, -right);
            BankStrip(banks, At(a.S, a.RightOuter), At(b.S, b.RightOuter), At(b.S, b.RightOuter + BankWidth) - down, At(a.S, a.RightOuter + BankWidth) - down, right);
            // Where they have parted, the ground between: banks in from each inner edge, as far as they reach.
            var (gapA, gapB) = (a.RightInner - a.LeftInner, b.RightInner - b.LeftInner);
            if (gapA > 0.01f || gapB > 0.01f)
            {
                var (wa, wb) = (MathF.Min(BankWidth, gapA / 2f), MathF.Min(BankWidth, gapB / 2f));
                var (da, db) = (BankWidth > 0f ? down * (wa / BankWidth) : V3.Zero, BankWidth > 0f ? down * (wb / BankWidth) : V3.Zero);
                BankStrip(banks, At(a.S, a.LeftInner), At(b.S, b.LeftInner), At(b.S, b.LeftInner + wb) - db, At(a.S, a.LeftInner + wa) - da, right);
                BankStrip(banks, At(a.S, a.RightInner), At(b.S, b.RightInner), At(b.S, b.RightInner - wb) - db, At(a.S, a.RightInner - wa) - da, -right);
            }
        }

        // Its outline (its paving's), round the left side out and the right side back.
        outline.AddRange(edges.Select(e => At(e.S, e.LeftOuter)));
        outline.AddRange(edges.AsEnumerable().Reverse().Select(e => At(e.S, e.RightOuter)));
        Outline = outline;

        // Lines: each group's outer edge; between its lanes, dashed; each group's inner edge, the gore's sides; chevrons between.
        IReadOnlyList<V3> Line(Func<float, float> x) => Enumerable.Range(0, steps + 1).Select(k => _length * k / steps).Select(s => At(s, x(s))).ToList();
        var lineWidth = RoadMeshBuilder.LineWidth;
        Painted(white, Line(s => groups[0][0].Left + Off(0, s)), lineWidth);
        Painted(white, Line(s => groups[1][^1].Right + Off(1, s)), lineWidth);
        Painted(white, Line(s => groups[0][^1].Right + Off(0, s)), lineWidth);
        Painted(white, Line(s => groups[1][0].Left + Off(1, s)), lineWidth);
        for (var g = 0; g < 2; g++)
        {
            for (var i = 0; i + 1 < groups[g].Count; i++)
            {
                var x = groups[g][i].Right;
                var group = g;
                foreach (var dash in Dashes(Line(s => x + Off(group, s))))
                    Painted(white, dash, lineWidth);
            }
        }

        for (var s = 3f; s < _length; s += 3f)
        {
            var (inL, inR) = (groups[0][^1].Right + Off(0, s), groups[1][0].Left + Off(1, s));
            if (inR - inL < 0.6f)
                continue;
            // A V between the two sides, its point back towards the trunk.
            var back = s - 1.5f;
            var tip = ((groups[0][^1].Right + Off(0, back)) + (groups[1][0].Left + Off(1, back))) / 2f;
            Painted(white, new[] { At(s, inL), At(back, tip), At(s, inR) }, 0.3f);
        }

        // Its lanes through it, for traffic: the trunk's lane on into its branch's, or a branch's on into the trunk's.
        var paths = new List<(Road, int, Road, int, IReadOnlyList<V3>)>();
        for (var g = 0; g < 2; g++)
        {
            for (var i = 0; i < groups[g].Count; i++)
            {
                var x = groups[g][i].Middle;
                var group = g;
                var points = Line(s => x + Off(group, s)).ToList();
                var trunkLane = g == 0 ? i : leftCount + i;
                if (splits)
                    paths.Add((trunk, trunkLane, ordered[g], i, points));
                else
                {
                    points.Reverse();
                    paths.Add((ordered[g], i, trunk, trunkLane, points));
                }
            }
        }

        Lanes = paths;

        var toLocal = GlobalTransform.AffineInverse();
        var mesh = new ArrayMesh();
        Road.AddSurface(mesh, surface, trunk.SurfaceMaterial ?? Road.DefaultMaterial("Asphalt"), toLocal);
        Road.AddSurface(mesh, white, trunk.WhiteLineMaterial ?? Road.DefaultMaterial("Road-Line-White"), toLocal);
        Road.AddSurface(mesh, banks, trunk.BankMaterial ?? Road.DefaultMaterial("Asphalt"), toLocal);
        // Carried: each group of lanes on a deck of its own, as its trunk's viaduct is, with its barriers and pillars.
        var concrete = new MeshData();
        if (deck is { } carried)
        {
            for (var g = 0; g < 2; g++)
            {
                var group = groups[g];
                var middle = (group[0].Left + group[^1].Right) / 2f;
                var forward = V3.Normalize(way);
                var along = Enumerable.Range(0, steps + 1).Select(k => _length * k / steps)
                    .Select(s => new RoadFrame(At(s, middle + Off(g, s)), forward, right, s)).ToList();
                var part = new RoadCrossSection(group.Count, 0, width, shoulder, ShoulderHeight: section.ShoulderHeight, ShoulderAngle: section.ShoulderAngle);
                // Its barriers its own (below): a group's own on both its sides would stand across the other's lanes where they still run together.
                var built = RoadStructureBuilder.Build(along, part, [carried with { Start = 0f, Length = _length, BarrierHeight = 0f }], trunk.GroundAt,
                    (at, half) => network.RoadUnder(at, half));
                var first = concrete.Vertices.Count;
                for (var i = 0; i < built.Vertices.Count; i++)
                    concrete.Add(built.Vertices[i], built.Normals[i], built.UVs[i]);
                concrete.Indices.AddRange(built.Indices.Select(i => i + first));
            }

            // Barriers: along both outer edges all the way; and along each group's inner paved edge from where the two
            // decks part (each on its own deck, facing its own lanes, so the two never meet), on into the branch roads' own.
            var (bw, bh) = (carried.BarrierWidth, carried.BarrierHeight);
            Barrier(concrete, edges.Select(e => (At(e.S, e.LeftOuter), right)).ToList(), bw, bh);
            Barrier(concrete, edges.Select(e => (At(e.S, e.RightOuter), -right)).ToList(), bw, bh);
        }

        // Its gore's barrier (see GoreBarrier): as it is carried or railed, or as chosen.
        var railed = deck is null && (_guardRails == BarrierMode.On || (_guardRails == BarrierMode.Auto && trunk.HasGuardRails));
        var gore = _gore != GoreBarrier.Auto ? _gore : deck is not null ? GoreBarrier.Concrete : railed ? GoreBarrier.Rail : GoreBarrier.None;
        // Where its gore's barrier begins, for a gore tip there (put up with its rails, below).
        V3? tipAt = null;
        if (gore == GoreBarrier.Concrete)
        {
            // A wall along each group's inner paved edge, from the last place they still meet (right where the paving
            // splits), each facing its own lanes, as the viaduct's (or a viaduct's usual) walls are.
            var spec = deck ?? new StructureSpec(StructureKind.Viaduct, 0f, 0f);
            var split = Math.Max(edges.FindLastIndex(e => e.RightInner - e.LeftInner <= 0.01f), 0);
            var parted = edges.Skip(split).ToList();
            if (parted.Count >= 2)
            {
                Barrier(concrete, parted.Select(e => (At(e.S, e.LeftInner), -right)).ToList(), spec.BarrierWidth, spec.BarrierHeight);
                Barrier(concrete, parted.Select(e => (At(e.S, e.RightInner), right)).ToList(), spec.BarrierWidth, spec.BarrierHeight);
                tipAt = At(parted[0].S, (parted[0].LeftInner + parted[0].RightInner) / 2f);
            }
        }

        if (concrete.Indices.Count > 0)
            Road.AddSurface(mesh, concrete, trunk.StructureMaterial ?? Road.DefaultMaterial("Concrete"), toLocal);
        _mesh ??= AddInternal(new MeshInstance3D { Name = "Mesh" });
        _mesh.Mesh = mesh.GetSurfaceCount() > 0 ? mesh : null;
        _body ??= AddInternal(new StaticBody3D { Name = "Body" });
        foreach (var old in _body.GetChildren())
        {
            _body.RemoveChild(old);
            old.QueueFree();
        }

        // Guard rails (not carried: a viaduct's walls are its own): along both outer edges, and along each group's
        // inner edge from where they part, as fences of that kind, a little in on the shoulders.
        _rails ??= AddInternal(new Node3D { Name = "Rails" });
        // (Internal ones too: its gore tip is.)
        foreach (var old in _rails.GetChildren(true))
        {
            _rails.RemoveChild(old);
            old.QueueFree();
        }

        const float railIn = 0.4f;
        void Rail(IEnumerable<V3> line, bool flip)
        {
            var points = line.Select(p => ToLocal(p.ToGodot())).ToArray();
            if (points.Length >= 2)
                // (A merge's stretch runs against its traffic, its rails drawn the other way: turned round to face it.)
                RoadLinks.AddBarrier(_rails, "Rail", RoadBarrierKind.GuardRail, flip == splits, points);
        }

        var every = Math.Max(1, (int)MathF.Round(4f / MathF.Max(_length / steps, 0.01f)));
        var railAlong = edges.Where((_, i) => i % every == 0 || i == edges.Count - 1).ToList();
        if (railed)
        {
            Rail(railAlong.Select(e => At(e.S, e.LeftOuter + railIn)), false);
            Rail(railAlong.Select(e => At(e.S, e.RightOuter - railIn)), true);
        }

        // Its gore's rails: from where there is room for both, each on its own group's paving.
        if (gore == GoreBarrier.Rail)
        {
            var parted = railAlong.SkipWhile(e => e.RightInner - e.LeftInner <= 2f * railIn + 0.3f).ToList();
            Rail(parted.Select(e => At(e.S, e.LeftInner - railIn)), true);
            Rail(parted.Select(e => At(e.S, e.RightInner + railIn)), false);
            if (parted.Count > 0)
                tipAt = At(parted[0].S, (parted[0].LeftInner + parted[0].RightInner) / 2f);
        }

        // A gore tip where its gore's barrier begins, facing back along it towards its trunk (where traffic comes at it).
        if (tipAt is { } nose)
            GoreTip.Put(_rails, nose.ToGodot(), (-way).ToGodot());

        // Its paving and banks solid, as a road's are.
        foreach (var solid in new[] { surface, banks, concrete }.Where(m => m.Indices.Count > 0))
            _body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = solid.Indices.Select(i => toLocal * solid.Vertices[i].ToGodot()).ToArray() } });
        UpdateGizmos();
        network.TrafficChanged();
        // Where it lies, for viaducts over it to keep their pillars off it.
        if (Outline.Count >= 3)
            network.FootprintChanged(this, [RoadNetwork.Outline(Outline)]);
        // (The ground is fitted to it by the road tool once it is placed: not here, while a new branch may still be being raised into a viaduct.)
    }

    /// <summary>A quad of paving facing up, its corners round in order (any way round).</summary>
    private static void Strip(MeshData mesh, V3 a, V3 b, V3 c, V3 d) => Face(mesh, [a, b, c, d], V3.UnitY);

    /// <summary>A bank from an edge (a to b along it) out to its foot (c, d), facing up and out, with a skirt down from its foot.</summary>
    private static void BankStrip(MeshData mesh, V3 a, V3 b, V3 c, V3 d, V3 outward)
    {
        var across = d - a;
        if (across.LengthSquared() < 1e-6f)
            return;
        Face(mesh, [a, b, c, d], V3.Normalize(V3.UnitY + V3.Normalize(outward) * 0.5f));
        Skirt(mesh, d, c, outward);
    }

    /// <summary>
    /// A barrier along an edge (its points, each with the way in from it onto the paving it stands at the edge of), so
    /// wide in and so high: its outer face, its top, its inner face, and its ends closed.
    /// </summary>
    private static void Barrier(MeshData mesh, IReadOnlyList<(V3 At, V3 In)> edge, float width, float height)
    {
        if (edge.Count < 2 || height <= 0f)
            return;
        var up = V3.UnitY * height;
        V3 Inner(int i) => edge[i].At + V3.Normalize(edge[i].In) * width;
        for (var i = 0; i + 1 < edge.Count; i++)
        {
            var (a, b) = (edge[i].At, edge[i + 1].At);
            var (ai, bi) = (Inner(i), Inner(i + 1));
            var into = V3.Normalize(edge[i].In);
            Face(mesh, [a, b, b + up, a + up], -into);
            Face(mesh, [a + up, b + up, bi + up, ai + up], V3.UnitY);
            Face(mesh, [ai, bi, bi + up, ai + up], into);
        }

        foreach (var (i, j) in new[] { (0, 1), (edge.Count - 1, edge.Count - 2) })
        {
            var outward = V3.Normalize((edge[i].At - edge[j].At) with { Y = 0f });
            Face(mesh, [edge[i].At, Inner(i), Inner(i) + up, edge[i].At + up], outward);
        }
    }

    /// <summary>A skirt down from an edge into the ground, facing a way.</summary>
    private static void Skirt(MeshData mesh, V3 a, V3 b, V3 facing)
    {
        var down = V3.UnitY * RoadMeshBuilder.SkirtDepth;
        Face(mesh, [a, b, b - down, a - down], facing);
    }

    private static void Face(MeshData mesh, V3[] corners, V3 normal)
    {
        normal = V3.Normalize(normal);
        var cross = V3.Cross(corners[1] - corners[0], corners[2] - corners[0]);
        var order = V3.Dot(cross, normal) > 0f ? corners.Reverse().ToArray() : corners;
        var level = MathF.Abs(normal.Y) > 0.7f;
        var first = mesh.Vertices.Count;
        foreach (var p in order)
            mesh.Add(p, normal, (level ? new System.Numerics.Vector2(p.X, p.Z) : new System.Numerics.Vector2(p.X + p.Z, p.Y)) / RoadMeshBuilder.TextureSize);
        for (var k = 1; k + 1 < order.Length; k++)
            mesh.Indices.AddRange(new[] { first, first + k, first + k + 1 });
    }

    /// <summary>A painted line along points, so wide, just over the paving.</summary>
    private static void Painted(MeshData mesh, IReadOnlyList<V3> line, float width)
    {
        var lift = V3.UnitY * RoadMeshBuilder.LineLift;
        for (var i = 0; i + 1 < line.Count; i++)
        {
            var (a, b) = (line[i], line[i + 1]);
            var along = (b - a) with { Y = 0f };
            if (along.LengthSquared() < 1e-8f)
                continue;
            var side = V3.Normalize(new V3(-along.Z, 0f, along.X)) * (width / 2f);
            Face(mesh, [a - side + lift, a + side + lift, b + side + lift, b - side + lift], V3.UnitY);
        }
    }

    /// <summary>A line cut into 3 m dashes 6 m apart.</summary>
    private static IEnumerable<IReadOnlyList<V3>> Dashes(IReadOnlyList<V3> line)
    {
        var dash = new List<V3>();
        var along = 0f;
        for (var i = 0; i < line.Count; i++)
        {
            if (i > 0)
                along += V3.Distance(line[i - 1], line[i]);
            if (along % 9f < 3f)
                dash.Add(line[i]);
            else
            {
                if (dash.Count >= 2)
                    yield return dash;
                dash = new List<V3>();
            }
        }

        if (dash.Count >= 2)
            yield return dash;
    }

    private T AddInternal<T>(T node) where T : Node
    {
        AddChild(node, false, InternalMode.Front);
        return node;
    }
}
