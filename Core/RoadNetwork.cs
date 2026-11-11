using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// The roads of a place, as <see cref="Road"/> children (at any depth), and the ground they lie
/// on: <see cref="FitTerrain"/> shapes its terrain's ground to them, flat under each road and a
/// margin past its edges, easing back to the ground as it was over a falloff beyond. In the editor
/// that is one undoable step: undo puts back the heights it changed.
/// <para>
/// Each road and junction remembers (as metadata saved with the scene) what shape the ground was
/// last fitted to, so fitting only touches the ones that have changed since, and with Fit As You
/// Draw on, the road tool fits each change as it is made.
/// </para>
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_roads/Icons/Road-Network.svg")]
public partial class RoadNetwork : Node3D
{
    /// <summary>The metadata a road or junction keeps what its ground was last fitted to in.</summary>
    private static readonly StringName FittedMeta = "road_fitted";

    /// <summary>Where a road lay when last fitted, as points along it (x and z, and in y how far from each its ground reached): the ground only it may have moved.</summary>
    private static readonly StringName FittedLineMeta = "road_fitted_line";

    /// <summary>How far apart those points are, in metres.</summary>
    private const float FittedLineStep = 8f;

    /// <summary>How long after an edit the road tool's fit waits, for roads and junctions to have rebuilt round it.</summary>
    private const double FitDelay = 0.25;

    private bool _fitQueued;

    /// <summary>The node whose ground the roads lie on; if not set, the first found in the level.</summary>
    [Export]
    public Node3D? Terrain { get; set; }

    /// <summary>Its terrain's ground, if it has one.</summary>
    public IRoadGround? Ground => RoadLinks.Ground?.Invoke(Terrain ?? (IsInsideTree() ? (Engine.IsEditorHint() ? GetTree().EditedSceneRoot : GetTree().CurrentScene) : null));

    /// <summary>
    /// How far past a road's edges no foliage is painted, in metres: for every road of it
    /// but those with a buffer of their own (0: none). Painting keeps off it while the Foliage dock's
    /// Road Buffer is on.
    /// </summary>
    [Export(PropertyHint.Range, "0,100,0.5,or_greater,suffix:m")]
    public float FoliageBuffer
    {
        get => _foliageBuffer;
        set
        {
            _foliageBuffer = Mathf.Max(0f, value);
            if (IsInsideTree())
            {
                foreach (var road in Roads)
                    road.FoliageBufferChanged();
            }
        }
    }

    private float _foliageBuffer;

    /// <summary>How far past a road's edge the ground is kept flat with it.</summary>
    [Export(PropertyHint.Range, "0,10,0.5,suffix:m")]
    public float Margin { get; set; } = 1f;

    /// <summary>How far beyond that the ground eases back to where it was.</summary>
    [Export(PropertyHint.Range, "0,50,0.5,suffix:m")]
    public float Falloff { get; set; } = 12f;

    /// <summary>
    /// How much of the roads' banks the ground covers: 0 follows them all the way down, 1 keeps it
    /// level with the roads, burying them. The rest of each bank shows.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.05")]
    public float BankCover { get; set; } = 0.7f;

    /// <summary>
    /// The ground as it was before any road was fitted to it: each grid point fitting has moved, as
    /// (x grid index, height, z grid index), kept from the first time it moved. Where no road,
    /// junction or lot wants a point any more (under a viaduct put in since, or where a road was
    /// moved from), fitting puts it back. Kept in a file of its own (in Assets/Terrain/Roads), not
    /// in the scene, which it made megabytes larger.
    /// </summary>
    [Export]
    public RoadGroundData? GroundOriginalFile { get; set; }

    /// <summary>
    /// Where it was kept before, in the scene: read, if a scene still has it, and moved into
    /// <see cref="GroundOriginalFile"/> the next time the ground is fitted. Saved empty from then on.
    /// </summary>
    [Export]
    public Vector3[] GroundOriginal { get => System.Array.Empty<Vector3>(); set => _legacyOriginal = value ?? System.Array.Empty<Vector3>(); }

    private Vector3[] _legacyOriginal = System.Array.Empty<Vector3>();

    /// <summary>The ground as it was, from its file or (until moved there) the scene.</summary>
    private Vector3[] GroundOriginalPoints => GroundOriginalFile?.Points is { Length: > 0 } kept ? kept : _legacyOriginal;

    /// <summary>Where networks keep the ground as it was before roads were fitted to it.</summary>
    
    /// <summary>Keeps the ground as it was, in its file (made the first time).</summary>
    public void KeepGroundOriginal(Vector3[] points)
    {
        if (GroundOriginalFile is null)
        {
            var scene = (Engine.IsEditorHint() ? GetTree().EditedSceneRoot : Owner)?.SceneFilePath.GetFile().GetBaseName();
            GroundOriginalFile = new RoadGroundData();
            var folder = RoadLinks.GroundFolder ?? (Engine.IsEditorHint() ? GetTree().EditedSceneRoot : Owner)?.SceneFilePath.GetBaseDir() ?? "res://";
            DirAccess.MakeDirRecursiveAbsolute(folder);
            GroundOriginalFile.TakeOverPath($"{folder}/{(string.IsNullOrEmpty(scene) ? "Scene" : scene)}-{Name}-Ground.res");
        }

        GroundOriginalFile.Points = points;
        _legacyOriginal = System.Array.Empty<Vector3>();
        if (ResourceSaver.Save(GroundOriginalFile, GroundOriginalFile.ResourcePath) != Error.Ok)
            GD.PushError($"{Name}: could not keep the ground as it was in {GroundOriginalFile.ResourcePath}.");
    }

    public override void _ValidateProperty(Godot.Collections.Dictionary property)
    {
        // Saved (empty, once moved to its file), not shown.
        if (property["name"].AsStringName() == PropertyName.GroundOriginal)
            property["usage"] = (int)PropertyUsageFlags.Storage;
    }

    /// <summary>Whether the road tool fits the ground to each road or junction as it is drawn or changed.</summary>
    [Export]
    public bool FitAsYouDraw { get; set; } = true;

    [ExportToolButton("Fit Terrain to Changed Roads", Icon = "Terrain")]
    public Callable FitTerrainButton => Callable.From(() => FitTerrain(false));

    [ExportToolButton("Refit Terrain to All Roads", Icon = "Reload")]
    public Callable RefitTerrainButton => Callable.From(() => FitTerrain(true));

    /// <summary>
    /// Forgets the ground as it was before roads were fitted to it, for when the terrain has been
    /// replaced: heights kept from the old one must never be put back on the new. The next fit keeps
    /// them afresh. One step to undo in the editor.
    /// </summary>
    [ExportToolButton("Forget Kept Ground", Icon = "Clear")]
    public Callable ForgetGroundButton => Callable.From(ForgetGround);

    public void ForgetGround()
    {
        var before = GroundOriginalPoints;
        var none = System.Array.Empty<Vector3>();
#if TOOLS
        if (Engine.IsEditorHint())
        {
            var undo = EditorInterface.Singleton.GetEditorUndoRedo();
            undo.CreateAction("Forget the kept ground");
            undo.AddDoMethod(this, MethodName.KeepGroundOriginal, none);
            undo.AddUndoMethod(this, MethodName.KeepGroundOriginal, before);
            undo.CommitAction();
            return;
        }
#endif
        KeepGroundOriginal(none);
    }

    [ExportToolButton("Save Terrain", Icon = "Save")]
    public Callable SaveTerrainButton => Callable.From(SaveTerrain);

#if TOOLS
    /// <summary>Bakes its look (see BakedRoadsNode), for the game to draw it faster; the level to be saved after.</summary>
    [ExportToolButton("Bake Roads", Icon = "Bake")]
    public Callable BakeButton => Callable.From(Bake);

    private void Bake()
    {
        if (EditorInterface.Singleton.GetEditedSceneRoot() is not { } root || string.IsNullOrEmpty(root.SceneFilePath))
        {
            GD.PushWarning("Bake Roads: save the level first.");
            return;
        }

        BakedRoadsNode.Bake(this, root, baked =>
        {
            EditorInterface.Singleton.MarkSceneAsUnsaved();
            GD.Print($"Baked {Name} into {baked?.GetChildCount() ?? 0} meshes. Save the level to keep them.");
        });
    }

    [ExportToolButton("Join Crossing Roads", Icon = "CurveCreate")]
    public Callable JoinCrossingsButton => Callable.From(JoinCrossings);

    /// <summary>
    /// Roads crossing at about the same height with no junction joining them there: each split
    /// there, at the junction one of them already has there, or at a new one. One step to undo.
    /// </summary>
    public void JoinCrossings()
    {
        var roads = Roads.Where(r => !r.IsPath && r.Points.Count >= 2).ToList();
        var lines = roads.Select(r => r.FullFrames().Select(f => f.Position.ToGodot()).ToList()).ToList();
        var found = new List<(Road A, Road B, Vector3 At)>();
        for (var a = 0; a < roads.Count; a++)
        for (var b = a + 1; b < roads.Count; b++)
        {
            var (la, lb) = (lines[a], lines[b]);
            for (var i = 0; i + 1 < la.Count; i++)
            for (var j = 0; j + 1 < lb.Count; j++)
            {
                if (Geometry2D.SegmentIntersectsSegment(Flat(la[i]), Flat(la[i + 1]), Flat(lb[j]), Flat(lb[j + 1])).Obj is not Vector2 hit)
                    continue;
                var t = Flat(la[i]).DistanceTo(hit) / Mathf.Max(Flat(la[i]).DistanceTo(Flat(la[i + 1])), 1e-4f);
                var u = Flat(lb[j]).DistanceTo(hit) / Mathf.Max(Flat(lb[j]).DistanceTo(Flat(lb[j + 1])), 1e-4f);
                var (at, bt) = (la[i].Lerp(la[i + 1], t), lb[j].Lerp(lb[j + 1], u));
                // One over the other (a bridge), or meeting at a junction already: not a crossing to join.
                if (Mathf.Abs(at.Y - bt.Y) > 4f || Junctions.Any(k => Flat(k.GlobalPosition).DistanceTo(hit) < 3f
                        && k.Arms().Any(x => x.Road == roads[a]) && k.Arms().Any(x => x.Road == roads[b])))
                    continue;
                if (!found.Any(f => Flat(f.At).DistanceTo(hit) < 3f))
                    found.Add((roads[a], roads[b], (at + bt) / 2f));
            }
        }

        if (found.Count == 0)
        {
            GD.Print($"{Name}: no roads cross without a junction.");
            return;
        }

        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        undo.CreateAction("Join crossing roads");
        foreach (var (roadA, roadB, at) in found)
        {
            // A junction one of them ends at there already: the other split at it. Else a new one, both split at it.
            var junction = Junctions.FirstOrDefault(k => Flat(k.GlobalPosition).DistanceTo(Flat(at)) < 3f
                && k.Arms().Any(x => x.Road == roadA || x.Road == roadB));
            var split = new List<Road>();
            if (junction is not null)
            {
                split.Add(junction.Arms().Any(x => x.Road == roadA) ? roadB : roadA);
            }
            else
            {
                junction = new RoadJunction { Name = "Junction" };
                undo.AddDoMethod(this, Node.MethodName.AddChild, junction, true);
                undo.AddDoMethod(junction, Node.MethodName.SetOwner, root);
                undo.AddDoProperty(junction, Node3D.PropertyName.GlobalPosition, at);
                undo.AddDoReference(junction);
                undo.AddUndoMethod(this, Node.MethodName.RemoveChild, junction);
                split.Add(roadA);
                split.Add(roadB);
            }

            foreach (var road in split)
            {
                var spot = junction.IsInsideTree() ? junction.GlobalPosition : at;
                var cut = new Editor.RoadSplit { Road = road, Junction = junction, Distance = Editor.RoadEdits.Along(road.FullFrames(), spot) };
                undo.AddDoMethod(this, MethodName.DoSplit, cut);
                undo.AddUndoMethod(this, MethodName.UndoSplit, cut);
                undo.AddDoReference(cut);
            }

            GD.Print($"{Name}: joined {roadA.Name} and {roadB.Name} at {junction.Name} ({at.X:0}, {at.Z:0}).");
        }

        undo.CommitAction();
        QueueFit();
    }

    private static Vector2 Flat(Vector3 v) => new(v.X, v.Z);

    public void DoSplit(Editor.RoadSplit split) => Editor.RoadEdits.Split(split);

    public void UndoSplit(Editor.RoadSplit split) => Editor.RoadEdits.Unsplit(split);
#endif

    /// <summary>
    /// Writes the terrain's regions to its files: for when an edit has not been saved with the scene.
    /// </summary>
    public void SaveTerrain()
    {
        if (Ground is not { } ground)
        {
            GD.PushError("RoadNetwork: set its Terrain first.");
            return;
        }

        ground.Save();
    }

    /// <summary>Every road under this network, at any depth.</summary>
    public IReadOnlyList<Road> Roads => Current(ref _roads);

    /// <summary>Every junction under this network, at any depth.</summary>
    public IReadOnlyList<RoadJunction> Junctions => Current(ref _junctions);

    /// <summary>
    /// One of its kept lists of parts, found again if any in it has left the scene since (taken away by an undo, say,
    /// without it being told): a part out of the scene has no place to be asked about.
    /// </summary>
    private List<T> Current<T>(ref List<T>? kept) where T : Node
    {
        if (kept is null || kept.Any(p => !IsInstanceValid(p) || !p.IsInsideTree()))
            kept = FindChildren("*", "", true, false).OfType<T>().ToList();
        return kept;
    }

    // Found once, and again only after a road or junction is added or taken away anywhere: looking
    // through every node under the network takes a few milliseconds, and it is asked often.
    private List<Road>? _roads;

    /// <summary>Its parking lots.</summary>
    public IReadOnlyList<ParkingLot> ParkingLots => Current(ref _parkingLots);
    private List<RoadJunction>? _junctions;
    private List<ParkingLot>? _parkingLots;

    /// <summary>Its forks and merges (see RoadFork).</summary>
    public IReadOnlyList<RoadFork> Forks => Current(ref _forks);
    private List<RoadFork>? _forks;

    private void NodeAddedOrRemoved(Node node)
    {
        if (node is Road)
            _roads = null;
        else if (node is RoadJunction)
            _junctions = null;
        else if (node is ParkingLot)
            _parkingLots = null;
        else if (node is RoadFork)
            _forks = null;
    }

    /// <summary>How fast traffic turns through a junction, in km/h (going straight on, at the roads' own speed).</summary>
    public const float TurnSpeed = 30f;

    /// <summary>How fast traffic drives through a parking lot, in km/h.</summary>
    public const float ParkingSpeed = 15f;

    /// <summary>How fast traffic goes down a ramp's lanes, in km/h.</summary>
    public const float RampSpeed = 60f;

    /// <summary>The group every road network is in, for places signs point to to find them.</summary>
    public const string Group = "road_networks";

    private bool _boardsQueued;
    private readonly Dictionary<(RoadJunction Junction, int Arm), int> _armLanes = new();

    public override void _EnterTree()
    {
        AddToGroup(Group);
        _roads = null;
        _junctions = null;
        _parkingLots = null;
        _forks = null;
        Watch(true);
        // The ground as it was, still in the scene: moved into its own file now, so the scene is
        // saved without it from the next save on (and has it still until then).
        if (Engine.IsEditorHint() && _legacyOriginal.Length > 0 && GroundOriginalFile is null)
            Callable.From(MoveGroundOriginal).CallDeferred();
    }

    private void MoveGroundOriginal()
    {
        if (_legacyOriginal.Length == 0 || GroundOriginalFile is not null)
            return;
        KeepGroundOriginal(_legacyOriginal);
        GD.Print($"{Name}: moved the ground as it was before roads were fitted out of the scene, into {GroundOriginalFile?.ResourcePath}. Save the scene to make it smaller.");
    }

    public override void _ExitTree()
    {
        Watch(false);
        PutOutLamps();
        StopHidingBaked();
    }

    /// <summary>
    /// Starts or stops hearing of nodes added to or taken from the tree; by method name, and only
    /// if not already (the editor lets go of them itself as it reloads the scripts).
    /// </summary>
    private void Watch(bool on)
    {
        var tree = GetTree();
        var heard = new Callable(this, MethodName.NodeAddedOrRemoved);
        foreach (var signal in new[] { SceneTree.SignalName.NodeAdded, SceneTree.SignalName.NodeRemoved })
        {
            var connected = tree.IsConnected(signal, heard);
            if (on && !connected)
                tree.Connect(signal, heard);
            else if (!on && connected)
                tree.Disconnect(signal, heard);
        }
    }

    /// <summary>
    /// Where each road and junction last lay (by instance): the part, its stretches (a road's centre
    /// line and its ramps', a junction's outline) and the box round them. Kept so that its viaducts
    /// know when what is under them changed, and where, and so that placing a pillar need not work
    /// every road's line out again.
    /// </summary>
    private readonly Dictionary<ulong, (Node3D Part, IReadOnlyList<FootprintStretch> Stretches, FlatBounds Area)> _footprints = new();

    /// <summary>Whether a road or junction may be missing from those, as the level loads: found and added the next time a pillar is placed.</summary>
    private bool _footprintsIncomplete = true;

    /// <summary>
    /// A road or junction built again, lying along these stretches: where it lies differently from
    /// before, the viaducts over the part that moved (where it was and where it is now) build
    /// again, their pillars kept off it. A viaduct built again but lying just as before passes
    /// nothing on, so this settles.
    /// </summary>
    public void FootprintChanged(Node3D part, IReadOnlyList<FootprintStretch> stretches)
    {
        if (RoadFootprint.Bounds(stretches) is not { } area)
            return;
        RoadLinks.FootprintChanged?.Invoke(part, stretches);
        var id = part.GetInstanceId();
        var had = _footprints.TryGetValue(id, out var before);
        _footprints[id] = (part, stretches, area);
        // Put back after being taken away (undoing a delete, say): the viaducts over it make room again.
        if (!had && _removed.Remove(id))
        {
            RefreshViaducts(part, area);
            return;
        }

        // The first time it builds, as the level loads, everything builds anyway.
        if (!had)
            return;
        if (RoadFootprint.Changed(before.Stretches, stretches) is { } moved)
            RefreshViaducts(part, moved);
    }

    /// <summary>Road and junction taken away (deleted, say): the viaducts over where it was stand pillars there again.</summary>
    public void FootprintRemoved(Node3D part)
    {
        RoadLinks.FootprintRemoved?.Invoke(part);
        var id = part.GetInstanceId();
        if (!_footprints.Remove(id, out var was))
            return;
        _removed.Add(id);
        RefreshViaducts(part, was.Area);
    }

    /// <summary>The ones taken away, by instance: coming back is a change, not a first build.</summary>
    private readonly HashSet<ulong> _removed = new();

    /// <summary>Every viaduct over an area (but the part that changed) builds again, placing its pillars afresh.</summary>
    private void RefreshViaducts(Node3D part, FlatBounds area)
    {
        var changed = area.Grow(PillarClearance + 5f);
        foreach (var (other, _, bounds) in _footprints.Values.ToList())
        {
            if (other == part || !IsInstanceValid(other) || !bounds.Intersects(changed))
                continue;
            // Just its pillars and the rest of its concrete, not the whole road.
            if (other is Road road && road.StructureSpecs().Any(s => s.Kind == StructureKind.Viaduct))
                road.RefreshStructures();
            else if (other is RoadJunction { Viaduct: true } junction)
                junction.QueueRebuild();
        }
    }

    /// <summary>A junction's outline as a stretch with no width, round it and back to its start.</summary>
    public static FootprintStretch Outline(IReadOnlyList<System.Numerics.Vector3> outline)
    {
        var frames = outline.Concat(outline.Take(1))
            .Select((p, i) => new RoadFrame(p, System.Numerics.Vector3.UnitZ, System.Numerics.Vector3.UnitX, i))
            .ToList();
        return new FootprintStretch(frames, 0f, 0f);
    }

    /// <summary>Adds any road or junction built before it could say where it lies (as the level loads) to those kept.</summary>
    private void FindFootprints()
    {
        if (!_footprintsIncomplete)
            return;
        _footprintsIncomplete = false;
        foreach (var road in Roads)
        {
            if (!_footprints.ContainsKey(road.GetInstanceId()) && road.FootprintStretches() is { Count: > 0 } stretches && RoadFootprint.Bounds(stretches) is { } area)
                _footprints[road.GetInstanceId()] = (road, stretches, area);
        }

        foreach (var junction in Junctions)
        {
            if (!_footprints.ContainsKey(junction.GetInstanceId()) && junction.Shape is { Outline.Count: >= 3 } shape)
            {
                var stretch = new[] { Outline(shape.Reach) };
                _footprints[junction.GetInstanceId()] = (junction, stretch, RoadFootprint.Bounds(stretch)!.Value);
            }
        }

        // And forks and merges, by their outlines, as junctions.
        foreach (var fork in Forks)
        {
            if (!_footprints.ContainsKey(fork.GetInstanceId()) && fork.Outline.Count >= 3)
            {
                var stretch = new[] { Outline(fork.Outline) };
                _footprints[fork.GetInstanceId()] = (fork, stretch, RoadFootprint.Bounds(stretch)!.Value);
            }
        }
    }

    /// <summary>How far below a viaduct's deck a road has to be for its pillars to keep off it, in metres: nearer is the deck's own road.</summary>
    private const float UnderBy = 2f;

    /// <summary>How far clear of a road's edge a pillar stands, in metres.</summary>
    private const float PillarClearance = 1f;

    /// <summary>
    /// Whether a road or junction runs under a point, well below it, within this many metres (a
    /// pillar's half width) and a little more: so a viaduct's pillar there would stand on it.
    /// </summary>
    public bool RoadUnder(System.Numerics.Vector3 at, float half)
    {
        FindFootprints();
        var margin = half + PillarClearance;
        foreach (var (part, stretches, area) in _footprints.Values)
        {
            if (!area.Grow(margin + Road.Spacing).Contains(at) || !IsInstanceValid(part))
                continue;
            if (part is RoadJunction or RoadFork)
            {
                // Its outline, round it and back to its start.
                var outline = stretches[0].Frames.Select(f => f.Position).ToList();
                outline.RemoveAt(outline.Count - 1);
                if (outline.Count < 3 || outline.Average(p => p.Y) > at.Y - UnderBy)
                    continue;
                if (Inside(outline, at) || outline.Select((p, i) => DistanceToSegment(at, p, outline[(i + 1) % outline.Count])).Min() <= margin)
                    return true;
                continue;
            }

            // Each road, and each ramp branching off one, along its centre line with its width.
            if (stretches.Any(s => RoadFootprint.Under(s, at, half, margin, UnderBy, Road.Spacing)))
                return true;
        }

        return false;
    }

    /// <summary>Whether a point is inside an outline, seen from above.</summary>
    private static bool Inside(IReadOnlyList<System.Numerics.Vector3> outline, System.Numerics.Vector3 at)
    {
        var inside = false;
        for (int i = 0, j = outline.Count - 1; i < outline.Count; j = i++)
        {
            var (a, b) = (outline[i], outline[j]);
            if ((a.Z > at.Z) != (b.Z > at.Z) && at.X < (b.X - a.X) * (at.Z - a.Z) / (b.Z - a.Z) + a.X)
                inside = !inside;
        }

        return inside;
    }

    /// <summary>How far a point is from a line between two others, seen from above.</summary>
    private static float DistanceToSegment(System.Numerics.Vector3 at, System.Numerics.Vector3 a, System.Numerics.Vector3 b)
    {
        var (p, s) = (new System.Numerics.Vector2(at.X - a.X, at.Z - a.Z), new System.Numerics.Vector2(b.X - a.X, b.Z - a.Z));
        var t = s.LengthSquared() > 1e-8f ? System.Math.Clamp(System.Numerics.Vector2.Dot(p, s) / s.LengthSquared(), 0f, 1f) : 0f;
        return (p - s * t).Length();
    }

    /// <summary>How long nothing has to change before its direction signs are filled in again, in milliseconds: not every frame of a drag.</summary>
    private const ulong BoardsSettle = 400;

    /// <summary>When its direction signs are next due to be filled in, if nothing changes before then.</summary>
    private ulong _boardsDue;

    /// <summary>
    /// Fills its direction signs in again once roads, junctions and places have stopped changing
    /// for a moment: working out every lane and route across the network is too slow for every
    /// frame of dragging a road about.
    /// </summary>
    public void QueueBoards()
    {
        if (!IsInsideTree())
            return;
        _boardsDue = Godot.Time.GetTicksMsec() + BoardsSettle;
        if (_boardsQueued)
            return;
        _boardsQueued = true;
        WaitForBoards();
    }

    private void WaitForBoards()
    {
        var wait = _boardsDue > Godot.Time.GetTicksMsec() ? (_boardsDue - Godot.Time.GetTicksMsec()) / 1000.0 : 0.0;
        // By Godot's Connect, not C#'s += (whose hookup Godot tries to undo when this is freed first, and fails).
        GetTree().CreateTimer(wait).Connect(SceneTreeTimer.SignalName.Timeout, Callable.From(() =>
        {
            if (!IsInstanceValid(this) || !IsInsideTree())
            {
                _boardsQueued = false;
                return;
            }

            // Changed again while waiting: wait on.
            if (Godot.Time.GetTicksMsec() < _boardsDue)
                WaitForBoards();
            else
                UpdateBoards();
        }));
    }

    /// <summary>
    /// Fills in every junction's direction signs: each town (a <see cref="Town"/>) under the arrow
    /// of the road that reaches it soonest from there, with how far it is to drive from the sign to
    /// where that road first comes into the town. A sign in a town, or with no town to name, is hidden.
    /// </summary>
    private void UpdateBoards()
    {
        _boardsQueued = false;
        if (!IsInsideTree())
            return;
        var graph = Traffic;
        var towns = GetTree().GetNodesInGroup(RoadLinks.DestinationGroup).OfType<IRoadDestination>().ToList();
        var places = towns
            .Select(d => (Place: d, Near: Entries(graph, d.Inside())))
            .Where(p => p.Near.Count > 0)
            .ToList();
        var reach = new Dictionary<int, IReadOnlyDictionary<int, float>>();
        IReadOnlyDictionary<int, float> From(int lane) => reach.TryGetValue(lane, out var d) ? d : reach[lane] = graph.DistancesFrom(lane);

        foreach (var junction in Junctions)
        {
            var arms = junction.Arms().Where(a => a.Road.ArmAt(a.Start) is not null).ToList();
            foreach (var (arm, board, before) in junction.Boards)
            {
                if (!IsInstanceValid(board) || arm >= arms.Count)
                    continue;
                var (road, atStart) = arms[arm];
                var wanted = atStart ? road.DirectionSignAtStart : road.DirectionSignAtEnd;
                if (wanted != DirectionSign.On && towns.Any(t => t.Inside()(board.GlobalPosition.ToNumerics())))
                {
                    board.Visible = false;
                    continue;
                }

                // The towns chosen for it, if any; else all of them.
                var chosen = atStart ? road.DirectionTownsAtStart : road.DirectionTownsAtEnd;
                var named = chosen.Count == 0 ? places : places.Where(p => p.Place is Node3D place && chosen.Contains(place)).ToList();
                var coming = road.ArmAt(atStart)!.Value;
                var found = new List<(string, int, string, float)>();
                foreach (var (place, near) in named)
                {
                    // The way out that gets there soonest, not back the way the sign's traffic came,
                    // along whichever lane near it can be reached.
                    (string Arrow, float Distance)? best = null;
                    for (var j = 0; j < arms.Count; j++)
                    {
                        if (j == arm || !_armLanes.TryGetValue((junction, j), out var start))
                            continue;
                        var reachable = From(start);
                        foreach (var (lane, along, _) in near)
                        {
                            if (!reachable.TryGetValue(lane, out var to))
                                continue;
                            var distance = before + to + along;
                            if (best is null || distance < best.Value.Distance)
                                best = (RoadSignals.Arrow(-coming.Direction, arms[j].Road.ArmAt(arms[j].Start)!.Value.Direction), distance);
                        }
                    }

                    if (best is { } b)
                        found.Add((place.SignName, place.Importance, b.Arrow, b.Distance));
                }

                var text = RoadSignals.PlacesText(found);
                board.Text = text;
                board.Visible = !string.IsNullOrWhiteSpace(text);
            }
        }
    }

    /// <summary>
    /// Where the lanes come into a town: each lane that is ever inside it, and how far along it
    /// first is (0 for one that starts inside). Driving to the nearest of these is driving into the town.
    /// </summary>
    private static IReadOnlyList<(int Lane, float Along, float Distance)> Entries(LaneGraph graph, System.Func<System.Numerics.Vector3, bool> inside)
    {
        var entries = new List<(int, float, float)>();
        foreach (var lane in graph.Lanes)
        {
            var along = 0f;
            for (var i = 0; i < lane.Points.Count; i++)
            {
                if (i > 0)
                    along += System.Numerics.Vector3.Distance(lane.Points[i - 1], lane.Points[i]);
                if (!inside(lane.Points[i]))
                    continue;
                entries.Add((lane.Id, along, 0f));
                break;
            }
        }

        return entries;
    }

    private LaneGraph? _traffic;
    private readonly Dictionary<int, (RoadJunction Junction, int Arm)> _turns = new();

    /// <summary>Every lane of the network joined up, for traffic to drive and routes to be found: worked out when first wanted after a change.</summary>
    public LaneGraph Traffic => _traffic ??= BuildTraffic();


    /// <summary>A road or junction has changed: its lanes are worked out again when next wanted.</summary>
    public void TrafficChanged() => _traffic = null;

    /// <summary>
    /// Every roadside parking bay on its roads (see RoadParking): each one's middle, the way into it,
    /// and how wide and long it is. Where the game can park.
    /// </summary>
    public IEnumerable<(Vector3 Middle, Vector3 Facing, float Width, float Length)> ParkingBays() =>
        Roads.SelectMany(r => r.Parking).SelectMany(p => p.Bays());

    /// <summary>
    /// What traffic must do before driving a lane: for a turn through a junction, what its lights
    /// or signs say for the road it comes in along (and the lights' colour now, if it has them);
    /// for any other lane, nothing: it has priority. Asked for the turn about to be driven into.
    /// </summary>
    public (ArmControl Control, Light? Light) SignalFor(int lane)
    {
        _ = Traffic;
        if (!_turns.TryGetValue(lane, out var turn) || !IsInstanceValid(turn.Junction) || turn.Arm >= turn.Junction.ArmControls.Count)
            return (ArmControl.Priority, null);
        return (turn.Junction.ArmControls[turn.Arm], turn.Junction.LightFor(turn.Arm));
    }

    /// <summary>
    /// Joins every lane of the network: each road's lanes, cut where its ramps leave or join so
    /// traffic can take them there; each junction's turns (and a cul-de-sac's turning round) from
    /// the roads' lanes coming in to those going out; each ramp's lanes from the road's outer lane
    /// to the ramp's own road, or back.
    /// </summary>
    private LaneGraph BuildTraffic()
    {
        var graph = new LaneGraph();
        _turns.Clear();
        _armLanes.Clear();
        // Each road lane's pieces in the way it goes, with where along the road each begins and ends.
        var pieces = new Dictionary<(Road Road, LaneDirection Direction, int Index), List<(float From, float To, int Id)>>();
        foreach (var road in Roads)
        {
            var frames = road.Frames();
            if (frames.Count < 2)
                continue;
            var distances = frames.Select(f => f.Distance).ToList();
            if (road.Closed)
                distances.Add(frames[^1].Distance + System.Numerics.Vector3.Distance(frames[^1].Position, frames[0].Position));
            // Cut where ramps join and leave, and where their lanes stop running alongside: each lane
            // only by the ramps its own traffic can take, on its side of the road.
            List<float> CutsFor(LaneDirection direction) => road.Ramps
                .Where(r => FlowsForward(r.Spec, road.CrossSection) == (direction == LaneDirection.Forward))
                .SelectMany(r => new[] { r.Start, r.Start + r.Length, AlongsideEnd(r.Spec, road.CrossSection) }).ToList();
            var speed = road.EffectiveSpeedLimit;
            foreach (var path in RoadLanes.Paths(frames, road.CrossSection, road.Closed))
            {
                // Measured the way it goes: backward, down the road from its end.
                var backward = path.Direction == LaneDirection.Backward;
                var along = backward ? distances.AsEnumerable().Reverse().Select(d => -d).ToList() : distances;
                var list = new List<(float From, float To, int Id)>();
                foreach (var (from, to, points) in LaneGraph.Cut(path.Points, along, CutsFor(path.Direction).Select(c => backward ? -c : c)))
                {
                    var id = graph.Add(points, speed, road.StreetName, $"{road.Name} {path.Direction.ToString().ToLowerInvariant()} lane {path.Index + 1}");
                    if (list.Count > 0)
                        graph.Link(list[^1].Id, id);
                    list.Add(backward ? (-from, -to, id) : (from, to, id));
                }

                if (road.Closed && list.Count > 1)
                    graph.Link(list[^1].Id, list[0].Id);
                pieces[(road, path.Direction, path.Index)] = list;
            }

            // Lanes going the same way lie side by side, the inner on the left of the next.
            foreach (var direction in new[] { LaneDirection.Forward, LaneDirection.Backward })
            {
                for (var index = 0; pieces.ContainsKey((road, direction, index + 1)); index++)
                {
                    var (inner, outer) = (pieces[(road, direction, index)], pieces[(road, direction, index + 1)]);
                    for (var k = 0; k < Mathf.Min(inner.Count, outer.Count); k++)
                        graph.Beside(inner[k].Id, outer[k].Id);
                }
            }
        }

        List<(float From, float To, int Id)>? Lane(Road road, LaneDirection direction, int index) =>
            pieces.TryGetValue((road, direction, index), out var list) && list.Count > 0 ? list : null;

        // Through each junction: from the lanes coming in along one road to those going out along another.
        foreach (var junction in Junctions)
        {
            if (junction.Shape is not { } shape)
                continue;
            var arms = junction.Arms().Where(a => a.Road.ArmAt(a.Start) is not null).ToList();
            foreach (var turn in shape.Connections)
            {
                if (turn.FromArm >= arms.Count || turn.ToArm >= arms.Count || turn.Points.Count < 2)
                    continue;
                var (inRoad, inStart) = arms[turn.FromArm];
                var (outRoad, outStart) = arms[turn.ToArm];
                // A road starting here has its backward lanes coming in and its forward lanes going out.
                var arriving = Lane(inRoad, inStart ? LaneDirection.Backward : LaneDirection.Forward, turn.FromLane);
                var leaving = Lane(outRoad, outStart ? LaneDirection.Forward : LaneDirection.Backward, turn.ToLane);
                if (arriving is null || leaving is null)
                    continue;
                // No changing lanes just before a junction, or just after.
                var last = graph.Lanes[arriving[^1].Id];
                graph.ForbidChange(last.Id, last.Length - NoChangeBefore, last.Length);
                graph.ForbidChange(leaving[0].Id, 0f, NoChangeAfter);
                var inWay = turn.Points[1] - turn.Points[0];
                var outWay = turn.Points[^1] - turn.Points[^2];
                var straight = inWay.LengthSquared() > 1e-8f && outWay.LengthSquared() > 1e-8f
                    && System.Numerics.Vector3.Dot(System.Numerics.Vector3.Normalize(inWay), System.Numerics.Vector3.Normalize(outWay)) > 0.9f;
                var speed = Mathf.Min(inRoad.EffectiveSpeedLimit, outRoad.EffectiveSpeedLimit);
                var id = graph.Add(turn.Points, straight ? speed : Mathf.Min(speed, TurnSpeed), "", $"{junction.Name} {(inRoad == outRoad ? "turning round" : "turn")}");
                graph.Link(arriving[^1].Id, id);
                graph.Link(id, leaving[0].Id);
                _turns[id] = (junction, turn.FromArm);
                // Where traffic leaving along that arm starts, for measuring how far places are that way.
                _armLanes.TryAdd((junction, turn.ToArm), leaving[0].Id);
            }
        }

        // Through each fork: each lane on into the lane it becomes.
        foreach (var fork in Forks)
        {
            foreach (var (from, fromLane, to, toLane, points) in fork.Lanes)
            {
                if (Lane(from, LaneDirection.Forward, fromLane) is not { } arriving || Lane(to, LaneDirection.Forward, toLane) is not { } leaving || points.Count < 2)
                    continue;
                var id = graph.Add(points, Mathf.Min(from.EffectiveSpeedLimit, to.EffectiveSpeedLimit), to.StreetName, $"{fork.Name} lane {fromLane + 1}");
                graph.Link(arriving[^1].Id, id);
                graph.Link(id, leaving[0].Id);
            }
        }

        // Through each parking lot: in from each road joined to it, by way of its middle, out along any.
        foreach (var lot in ParkingLots)
        {
            var arriving = new List<int>();
            var leaving = new List<int>();
            foreach (var (road, start) in lot.Entrances())
            {
                for (var index = 0; ; index++)
                {
                    var forward = Lane(road, LaneDirection.Forward, index);
                    var backward = Lane(road, LaneDirection.Backward, index);
                    if (forward is null && backward is null)
                        break;
                    // At its start, its forward lanes leave the lot and its backward ones come in; at its end, the other way about.
                    var (inward, outward) = start ? (backward, forward) : (forward, backward);
                    if (inward is not null)
                        arriving.Add(inward[^1].Id);
                    if (outward is not null)
                        leaving.Add(outward[0].Id);
                }
            }

            foreach (var from in arriving)
            {
                foreach (var to in leaving)
                {
                    var points = new[] { graph.Lanes[from].Points[^1], lot.Middle, graph.Lanes[to].Points[0] };
                    var id = graph.Add(points, ParkingSpeed, "", $"{lot.Name} through");
                    graph.Link(from, id);
                    graph.Link(id, to);
                }
            }
        }

        // Down each ramp: off the road's outer lane on its side, onto its own road; or the reverse.
        foreach (var road in Roads)
        {
            var section = road.CrossSection;
            foreach (var ramp in road.Ramps)
            {
                var spec = ramp.Spec;
                var flowsForward = FlowsForward(spec, section);
                var direction = flowsForward ? LaneDirection.Forward : LaneDirection.Backward;
                var lanes = direction == LaneDirection.Forward ? section.ForwardLanes : section.BackwardLanes;
                if (lanes == 0 || Lane(road, direction, lanes - 1) is not { } outer)
                    continue;
                var exit = spec.Kind == RampKind.Exit;
                var branch = Roads.FirstOrDefault(r => exit ? r.StartJunction == ramp : r.EndJunction == ramp);
                var joined = RoadRampShaper.SeparatedAtStart(spec, section) ? spec.End : spec.Start;

                var frames = road.FullFrames();
                if (frames.Count < 2)
                    continue;
                var branchFrames = RoadRampShaper.BranchFrames(frames, spec, section);
                var distances = RoadCurve.Trim(frames, spec.Start, spec.End).Select(f => f.Distance).ToList();
                var alongsideEnd = AlongsideEnd(spec, section);
                // Which side of the road's outer lane it lies, as its traffic sees it.
                var onRight = (spec.Side == RoadSide.Right) == flowsForward;
                var rampLanes = new List<List<int>>();
                foreach (var path in RoadLanes.Paths(branchFrames, spec.Section, false).Where(p => p.Direction == LaneDirection.Forward).OrderBy(p => p.Index))
                {
                    // Its frames run the road's way: its traffic goes that way on a side that flows forward.
                    var points = flowsForward ? path.Points : path.Points.Reverse().ToList();
                    var along = flowsForward ? distances : distances.AsEnumerable().Reverse().Select(d => -d).ToList();
                    if (points.Count != along.Count)
                        continue;
                    // Cut where it stops running alongside the road: that stretch lies beside the road's outer lane.
                    var parts = LaneGraph.Cut(points, along, new[] { flowsForward ? alongsideEnd : -alongsideEnd })
                        .Select(p => graph.Add(p.Points, RampSpeed, branch?.StreetName ?? "", $"{ramp.Name} lane {path.Index + 1}")).ToList();
                    for (var k = 1; k < parts.Count; k++)
                        graph.Link(parts[k - 1], parts[k]);
                    rampLanes.Add(parts);

                    var own = branch is null ? null : Lane(branch, LaneDirection.Forward, path.Index);
                    // Its alongside stretch: where it leaves the road for an exit (first), where it joins for an entrance (last).
                    var alongside = exit ? parts[0] : parts[^1];
                    if (exit)
                    {
                        // In from the road where it joins, the inner lane from its outer lane; out onto its own road.
                        if (path.Index == 0 && Piece(outer, joined, ending: true) is { } before)
                            graph.Link(before, parts[0]);
                        if (own is not null)
                            graph.Link(parts[^1], own[0].Id);
                        // Not while it is still too narrow to drive into.
                        graph.ForbidChange(alongside, 0f, spec.Taper / 2f);
                    }
                    else
                    {
                        if (own is not null)
                            graph.Link(own[^1].Id, parts[0]);
                        if (path.Index == 0 && Piece(outer, joined, ending: false) is { } after)
                            graph.Link(parts[^1], after);
                        var length = graph.Lanes[alongside].Length;
                        graph.ForbidChange(alongside, length - spec.Taper / 2f, length);
                    }

                    // The inner lane runs beside the road's outer lane while alongside it.
                    if (path.Index == 0 && parts.Count > 1 && Beside(outer, joined, alongsideEnd) is { } roadLane)
                    {
                        if (onRight)
                            graph.Beside(roadLane, alongside);
                        else
                            graph.Beside(alongside, roadLane);
                    }
                }

                // Its own lanes side by side, the inner on the left as the traffic sees it.
                for (var i = 0; i + 1 < rampLanes.Count; i++)
                {
                    for (var k = 0; k < Mathf.Min(rampLanes[i].Count, rampLanes[i + 1].Count); k++)
                    {
                        if (onRight)
                            graph.Beside(rampLanes[i][k], rampLanes[i + 1][k]);
                        else
                            graph.Beside(rampLanes[i + 1][k], rampLanes[i][k]);
                    }
                }
            }
        }

        return graph;
    }

    /// <summary>Whether a ramp's traffic goes the road's way: the right side has its forward lanes, the left its backward ones, unless it is one way.</summary>
    private static bool FlowsForward(RampSpec spec, RoadCrossSection section) =>
        spec.Side == RoadSide.Right ? section.ForwardLanes > 0 : section.BackwardLanes == 0;

    /// <summary>How far traffic may not change lanes before a junction, and after one, in metres.</summary>
    public const float NoChangeBefore = 30f;
    public const float NoChangeAfter = 10f;

    /// <summary>Where along the road a ramp's lanes stop running alongside it: past its taper and alongside stretch, from its joined end.</summary>
    private static float AlongsideEnd(RampSpec spec, RoadCrossSection section) =>
        RoadRampShaper.SeparatedAtStart(spec, section) ? spec.End - spec.Taper - spec.Alongside : spec.Start + spec.Taper + spec.Alongside;

    /// <summary>The piece of a road's lane between two distances along the road (either way round); none if none does.</summary>
    private static int? Beside(List<(float From, float To, int Id)> lane, float a, float b)
    {
        var (low, high) = (Mathf.Min(a, b), Mathf.Max(a, b));
        foreach (var piece in lane)
        {
            var (from, to) = (Mathf.Min(piece.From, piece.To), Mathf.Max(piece.From, piece.To));
            if (Mathf.Abs(from - low) < 1f && Mathf.Abs(to - high) < 1f)
                return piece.Id;
        }

        return null;
    }

    /// <summary>The piece of a road's lane ending (or beginning) at a distance along the road; none if none does.</summary>
    private static int? Piece(List<(float From, float To, int Id)> lane, float distance, bool ending)
    {
        var best = lane.OrderBy(p => Mathf.Abs((ending ? p.To : p.From) - distance)).FirstOrDefault();
        return lane.Count > 0 && Mathf.Abs((ending ? best.To : best.From) - distance) < 1f ? best.Id : null;
    }

    /// <summary>Fits the ground to what has changed shortly, once the roads and junctions have rebuilt; for the road tool, after an edit.</summary>
    /// <remarks>Asked again before then (as something is dragged), it waits on from the latest asking.</remarks>
    public void QueueFit()
    {
        if (!FitAsYouDraw || Ground is null || !IsInsideTree())
            return;
        _fitDue = Godot.Time.GetTicksMsec() + (ulong)(FitDelay * 1000);
        if (_fitQueued)
            return;
        _fitQueued = true;
        WaitToFit();
    }

    /// <summary>When the ground is next due to be fitted, if nothing more changes before then.</summary>
    private ulong _fitDue;

    private void WaitToFit()
    {
        var wait = _fitDue > Godot.Time.GetTicksMsec() ? (_fitDue - Godot.Time.GetTicksMsec()) / 1000.0 : 0.0;
        // By Godot's Connect, not C#'s += (whose hookup Godot tries to undo when this is freed first, and fails).
        GetTree().CreateTimer(wait).Connect(SceneTreeTimer.SignalName.Timeout, Callable.From(() =>
        {
            if (!IsInstanceValid(this) || !IsInsideTree())
            {
                _fitQueued = false;
                return;
            }

            if (Godot.Time.GetTicksMsec() < _fitDue)
            {
                WaitToFit();
                return;
            }

            _fitQueued = false;
            FitTerrain(false);
        }));
    }

    /// <summary>
    /// Shapes the terrain's ground to the roads and junctions changed since it was last fitted to
    /// them (or all of them): each grid point near one moves towards the height it wants there, all
    /// the way under the road, less and less over the falloff. Where a changed one meets one that
    /// has not, the other's wants count too, so the ground where they meet still suits both.
    /// </summary>
    public void FitTerrain(bool all)
    {
        if (Ground is not { } ground)
        {
            GD.PushError("RoadNetwork: set its Terrain first.");
            return;
        }

        var spacing = ground.VertexSpacing;
        var parts = Roads.Cast<Node3D>().Concat(Junctions).Concat(ParkingLots).Concat(Forks).ToList();
        var keys = parts.ToDictionary(p => p, Key);
        var changed = parts.Where(p => all || !p.HasMeta(FittedMeta) || p.GetMeta(FittedMeta).AsString() != keys[p]).ToList();
        if (changed.Count == 0)
            return;

        var wanted = new Dictionary<(int X, int Z), GroundTarget>();
        void Want(Dictionary<(int X, int Z), GroundTarget> targets, bool onlyWanted)
        {
            foreach (var (at, target) in targets)
            {
                var has = wanted.TryGetValue(at, out var had);
                if ((!has && !onlyWanted) || (has && target.Beats(had)))
                    wanted[at] = target;
            }
        }

        foreach (var part in changed)
            Want(Targets(part, spacing), false);
        // Their neighbours only where the changed ones reach.
        foreach (var part in changed.SelectMany(Neighbours).Distinct().Except(changed))
            Want(Targets(part, spacing), true);

        // Each as (x, z, height): grid indices fit a float exactly.
        var beforeList = new List<Vector3>(wanted.Count);
        var fitted = new Dictionary<(int X, int Z), float>(wanted.Count);
        foreach (var ((x, z), target) in wanted)
        {
            if (ground.SampleHeight(x, z) is not { } height)
                continue; // outside the terrain's regions
            beforeList.Add(new Vector3(x, height, z));
            fitted[(x, z)] = Mathf.Lerp(height, target.Height, target.Weight);
        }

        // Smooth where it eases back to the ground, so it is not left bumpy beside the road.
        var smoothed = RoadGround.Smoothed(fitted, wanted, at => ground.SampleHeight(at.X, at.Z));
        var afterList = smoothed.Select(p => new Vector3(p.Key.X, p.Value, p.Key.Z)).ToList();

        // The ground as it was, kept for each point the first time it moves; and put back where
        // the changed parts once had it moved and nothing wants it now.
        var originalsBefore = GroundOriginalPoints;
        var originals = new Dictionary<(int X, int Z), float>(originalsBefore.Length);
        foreach (var o in originalsBefore)
            originals[((int)o.X, (int)o.Z)] = o.Y;
        foreach (var point in beforeList)
            originals.TryAdd(((int)point.X, (int)point.Z), point.Y);
        foreach (var ((x, z), height) in Released(changed, wanted, originals, spacing))
        {
            if (ground.SampleHeight(x, z) is not { } now)
                continue;
            beforeList.Add(new Vector3(x, now, z));
            afterList.Add(new Vector3(x, height, z));
            originals.Remove((x, z));
        }

        var originalsAfter = originals.Select(o => new Vector3(o.Key.X, o.Value, o.Key.Z)).ToArray();

        var before = beforeList.ToArray();
        var after = afterList.ToArray();
        var (holesBefore, holesAfter) = Holes(changed.OfType<Road>(), ground);

#if TOOLS
        if (Engine.IsEditorHint())
        {
            var undo = EditorInterface.Singleton.GetEditorUndoRedo();
            undo.CreateAction("Fit terrain to roads");
            undo.AddDoMethod(this, MethodName.SetHeights, after);
            undo.AddUndoMethod(this, MethodName.SetHeights, before);
            undo.AddDoMethod(this, MethodName.KeepGroundOriginal, originalsAfter);
            undo.AddUndoMethod(this, MethodName.KeepGroundOriginal, originalsBefore);
            if (holesAfter.Length > 0)
            {
                undo.AddDoMethod(this, MethodName.SetHoles, holesAfter);
                undo.AddUndoMethod(this, MethodName.SetHoles, holesBefore);
            }
            foreach (var part in changed)
            {
                if (part is Road fittedRoad)
                {
                    undo.AddDoMethod(fittedRoad, GodotObject.MethodName.SetMeta, FittedLineMeta, FittedLine(fittedRoad, spacing));
                    if (fittedRoad.HasMeta(FittedLineMeta))
                        undo.AddUndoMethod(fittedRoad, GodotObject.MethodName.SetMeta, FittedLineMeta, fittedRoad.GetMeta(FittedLineMeta));
                    else
                        undo.AddUndoMethod(fittedRoad, GodotObject.MethodName.RemoveMeta, FittedLineMeta);
                }

                undo.AddDoMethod(part, GodotObject.MethodName.SetMeta, FittedMeta, keys[part]);
                if (part.HasMeta(FittedMeta))
                    undo.AddUndoMethod(part, GodotObject.MethodName.SetMeta, FittedMeta, part.GetMeta(FittedMeta));
                else
                    undo.AddUndoMethod(part, GodotObject.MethodName.RemoveMeta, FittedMeta);
            }

            undo.CommitAction();
            return;
        }
#endif
        SetHeights(after);
        KeepGroundOriginal(originalsAfter);
        if (holesAfter.Length > 0)
            SetHoles(holesAfter);
        foreach (var part in changed)
        {
            part.SetMeta(FittedMeta, keys[part]);
            if (part is Road road)
                road.SetMeta(FittedLineMeta, FittedLine(road, spacing));
        }
    }

    /// <summary>Where a road lies now, for the next fit to tell which ground it moved (see <see cref="FittedLineMeta"/>).</summary>
    private Vector3[] FittedLine(Road road, float spacing)
    {
        var line = new List<Vector3>();
        foreach (var stretch in road.FootprintStretches())
        {
            var reach = System.MathF.Max(System.MathF.Abs(stretch.Left), System.MathF.Abs(stretch.Right)) + road.CrossSection.BankWidth + Margin + Falloff + spacing + FittedLineStep;
            System.Numerics.Vector3? last = null;
            for (var i = 0; i < stretch.Frames.Count; i++)
            {
                var at = stretch.Frames[i].Position;
                if (last is { } was && i < stretch.Frames.Count - 1 && System.Numerics.Vector3.Distance(was, at) < FittedLineStep)
                    continue;
                line.Add(new Vector3(at.X, reach, at.Z));
                last = at;
            }
        }

        return line.ToArray();
    }

    /// <summary>
    /// The grid points round where the changed roads lay when last fitted that fitting once moved and
    /// that nothing wants now (not they, nor any other road, junction or lot reaching there): each with
    /// its height as it was before, to be put back. Only there: a height kept for anywhere else was
    /// not moved by these roads, and may be of ground since replaced.
    /// </summary>
    private IEnumerable<((int X, int Z) At, float Height)> Released(IReadOnlyList<Node3D> changed, Dictionary<(int X, int Z), GroundTarget> wanted,
        Dictionary<(int X, int Z), float> originals, float spacing)
    {
        var reach = Margin + Falloff + spacing;
        var line = changed.OfType<Road>().Where(r => r.HasMeta(FittedLineMeta)).SelectMany(r => r.GetMeta(FittedLineMeta).AsVector3Array()).ToArray();
        if (line.Length == 0 || originals.Count == 0)
            yield break;
        var bounds = new FlatBounds(
            new System.Numerics.Vector2(line.Min(p => p.X - p.Y), line.Min(p => p.Z - p.Y)),
            new System.Numerics.Vector2(line.Max(p => p.X + p.Y), line.Max(p => p.Z + p.Y)));
        bool Near(float x, float z)
        {
            foreach (var p in line)
            {
                if ((p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z) <= p.Y * p.Y)
                    return true;
            }

            return false;
        }

        var candidates = originals.Keys.Where(k => !wanted.ContainsKey(k)
            && bounds.Contains(new System.Numerics.Vector3(k.X * spacing, 0f, k.Z * spacing)) && Near(k.X * spacing, k.Z * spacing)).ToHashSet();
        if (candidates.Count == 0)
            yield break;

        // Anything else reaching there keeps what it wants.
        var others = new List<Node3D>();
        foreach (var road in Roads.Except(changed.OfType<Road>()))
        {
            if (RoadFootprint.Bounds(road.FootprintStretches())?.Grow(reach) is { } b && b.Intersects(bounds))
                others.Add(road);
        }

        others.AddRange(Junctions.Where(j => bounds.Grow(50f).Contains(j.GlobalPosition.ToNumerics())));
        others.AddRange(ParkingLots.Where(l => l.OuterEdge.Any(p => bounds.Grow(reach).Contains(p))));
        foreach (var other in others.Except(changed))
            candidates.ExceptWith(Targets(other, spacing).Keys);

        foreach (var at in candidates)
            yield return (at, originals[at]);
    }

    /// <summary>
    /// Levels the ground under each of these outlines to its height (with a margin that far past
    /// it), easing back to the ground as it was over the falloff: pads for buildings to stand on.
    /// One step to undo in the editor. Returns the grid points it moved as they were before (x and
    /// z grid indices, and height), for putting back with <see cref="RestoreGround"/>.
    /// </summary>
    public Vector3[] LevelGround(IReadOnlyList<(IReadOnlyList<System.Numerics.Vector3> Outline, float Height)> pads, float margin, float falloff, string action)
    {
        if (pads.Count == 0 || Ground is not { } ground)
            return System.Array.Empty<Vector3>();
        var spacing = ground.VertexSpacing;
        var wanted = new Dictionary<(int X, int Z), GroundTarget>();
        foreach (var (outline, height) in pads)
        {
            foreach (var (at, target) in RoadGround.ShapeTargets(outline, height, spacing, margin, falloff))
            {
                if (!wanted.TryGetValue(at, out var had) || target.Beats(had))
                    wanted[at] = target;
            }
        }

        var before = new List<Vector3>(wanted.Count);
        var levelled = new Dictionary<(int X, int Z), float>(wanted.Count);
        foreach (var ((x, z), target) in wanted)
        {
            if (ground.SampleHeight(x, z) is not { } height)
                continue;
            before.Add(new Vector3(x, height, z));
            levelled[(x, z)] = Mathf.Lerp(height, target.Height, target.Weight);
        }

        var smoothed = RoadGround.Smoothed(levelled, wanted, at => ground.SampleHeight(at.X, at.Z));
        var after = smoothed.Select(p => new Vector3(p.Key.X, p.Value, p.Key.Z)).ToArray();
#if TOOLS
        if (Engine.IsEditorHint())
        {
            var undo = EditorInterface.Singleton.GetEditorUndoRedo();
            undo.CreateAction(action);
            undo.AddDoMethod(this, MethodName.SetHeights, after);
            undo.AddUndoMethod(this, MethodName.SetHeights, before.ToArray());
            undo.CommitAction();
            return before.ToArray();
        }
#endif
        SetHeights(after);
        return before.ToArray();
    }

    /// <summary>Puts grid points of the ground back as they were (x and z grid indices, and height); one step to undo in the editor.</summary>
    public void RestoreGround(Vector3[] points, string action)
    {
        if (points.Length == 0 || Ground is not { } ground)
            return;
        var now = points.Select(p => ground.SampleHeight((int)p.X, (int)p.Z) is { } h ? new Vector3(p.X, h, p.Z) : p).ToArray();
#if TOOLS
        if (Engine.IsEditorHint())
        {
            var undo = EditorInterface.Singleton.GetEditorUndoRedo();
            undo.CreateAction(action);
            undo.AddDoMethod(this, MethodName.SetHeights, points);
            undo.AddUndoMethod(this, MethodName.SetHeights, now);
            undo.CommitAction();
            return;
        }
#endif
        SetHeights(points);
    }

    /// <summary>The heights a road (with its ramps) or a junction wants the ground at.</summary>
    private Dictionary<(int X, int Z), GroundTarget> Targets(Node3D part, float spacing)
    {
        var targets = new Dictionary<(int X, int Z), GroundTarget>();
        void Add(Dictionary<(int X, int Z), GroundTarget> more)
        {
            foreach (var (at, target) in more)
            {
                if (!targets.TryGetValue(at, out var had) || target.Beats(had))
                    targets[at] = target;
            }
        }

        switch (part)
        {
            case Road road:
                // Only where it lies on the ground: not under its viaducts, nor over its tunnels.
                var structures = road.StructureSpecs();
                foreach (var run in RoadStructureBuilder.OnGround(road.Frames(), structures))
                {
                    Add(RoadGround.Targets(run, road.CrossSection, spacing, Margin, Falloff, BankCover));
                    // Under its sidewalks, from the sidewalks as they are built.
                    var parking = road.Parking.Select(p => p.Spec).ToList();
                    if (road.CrossSection.SidewalkLeft > 0f || road.CrossSection.SidewalkRight > 0f)
                        UnderSidewalks(targets, RoadMeshBuilder.Build(run, road.CrossSection, false, openings: road.Openings(run), parking: parking).Sidewalks, spacing);
                    // Under its parking, which sets its side out past its cross-section, as under the road.
                    if (parking.Count > 0)
                        UnderSidewalks(targets, RoadMeshBuilder.ParkingTops(run, road.CrossSection, parking), spacing);
                }
                // Its rounded loose ends, where they lie on the ground.
                if (!road.Closed && road.Frames() is { Count: >= 2 } ends)
                {
                    foreach (var (frame, along, loose) in new[] { (ends[0], -1f, road.StartJunction is null), (ends[^1], 1f, road.EndJunction is null) })
                    {
                        if (!loose || structures.Any(s => s.Covers(frame.Distance)))
                            continue;
                        var (middle, outline) = RoadMeshBuilder.RoundEndOutline(frame, along, road.CrossSection);
                        // No bank round one with sidewalks: the ground meets them.
                        var walked = road.CrossSection.SidewalkLeft > 0f || road.CrossSection.SidewalkRight > 0f;
                        Add(RoadGround.ShapeTargets(outline, middle.Y, spacing, Margin, Falloff, road.ShoulderHeight,
                            walked ? 0f : road.CrossSection.BankWidth, middle, BankCover));
                    }
                }

                foreach (var ramp in road.Ramps)
                    Add(RoadGround.Targets(RoadRampShaper.BranchFrames(road.FullFrames(), ramp.Spec, road.CrossSection), ramp.Spec.Section, spacing, Margin, Falloff, BankCover));
                break;
            case RoadJunction { Viaduct: true }:
                break; // over the ground, not on it
            // (Carried as a viaduct: over the ground, not on it.)
            case RoadFork { Outline.Count: >= 3, Carried: false } fork:
                // Under it as under a road: flat under its paving, easing out round it.
                Add(RoadGround.ShapeTargets(fork.Outline, fork.Outline.Average(p => p.Y), spacing, Margin, Falloff, fork.BankHeight, fork.BankWidth,
                    fork.Outline.Aggregate(System.Numerics.Vector3.Zero, (a, b) => a + b) / fork.Outline.Count, BankCover));
                break;
            case ParkingLot { OuterEdge.Count: >= 3 } lot:
                // Round it, easing out from its edge at the edge's own height; under it, just under its asphalt and sidewalks as they are built.
                Add(RoadGround.ShapeTargets(lot.OuterEdge, lot.Middle.Y, spacing, Margin, Falloff, middle: lot.Middle, surfaceOutline: lot.Edge));
                if (lot.Asphalt is { } asphalt)
                    UnderSidewalks(targets, asphalt, spacing);
                if (lot.Sidewalks is { } lotWalks)
                    UnderSidewalks(targets, lotWalks, spacing);
                break;
            case RoadJunction { Shape: { } shape } junction:
                Add(RoadGround.ShapeTargets(shape.Reach, (shape.Middle ?? junction.GlobalPosition.ToNumerics()).Y, spacing, Margin, Falloff,
                    junction.Banks.Height, RoadCrossSection.Bank(junction.Banks.Height, junction.Banks.Angle),
                    shape.Middle ?? junction.GlobalPosition.ToNumerics(), BankCover, shape.Outline));
                if (shape.Sidewalks is { } sidewalks)
                    UnderSidewalks(targets, sidewalks, spacing);
                break;
        }

        return targets;
    }

    /// <summary>
    /// The ground under sidewalks wanted just under them, whatever else this part wants there; and
    /// beside them too, a grid step out, unless the part wants it as part of its own surface.
    /// </summary>
    private static void UnderSidewalks(Dictionary<(int X, int Z), GroundTarget> targets, MeshData sidewalks, float spacing)
    {
        foreach (var (at, (target, outside)) in RoadGround.SidewalkTargets(sidewalks, spacing))
        {
            if (outside <= 0f || !targets.TryGetValue(at, out var had) || had.Beyond > 0.5f || had.Height > target.Height)
                targets[at] = target;
        }
    }

    /// <summary>
    /// For finding where the ground shows through (see the roads tool's Find Ground Through Roads):
    /// at each of the grid points given, every road, junction and parking lot that wants a height
    /// there, and what it wants, as fitting would weigh them.
    /// </summary>
    public Dictionary<(int X, int Z), List<(string Part, GroundTarget Target)>> WishesAt(IEnumerable<(int X, int Z)> points)
    {
        var wanted = points.Distinct().ToDictionary(p => p, _ => new List<(string, GroundTarget)>());
        if (Ground is not { } ground || wanted.Count == 0)
            return wanted;
        var parts = Roads.Cast<Node3D>().Concat(Junctions).Concat(ParkingLots).Concat(Forks);
        foreach (var part in parts)
        {
            var targets = Targets(part, ground.VertexSpacing);
            foreach (var (at, list) in wanted)
            {
                if (targets.TryGetValue(at, out var target))
                    list.Add((part.Name, target));
            }
        }

        return wanted;
    }

    /// <summary>What a road or junction's ground meets: a road's junctions and ramps' roads, the road a ramp's road comes off, a junction's roads.</summary>
    private IEnumerable<Node3D> Neighbours(Node3D part)
    {
        switch (part)
        {
            case Road road:
                foreach (var end in new[] { road.StartJunction, road.EndJunction })
                {
                    if (end is RoadJunction or ParkingLot or RoadFork)
                        yield return end;
                    else if (end is RoadRamp { Road: { } from })
                        yield return from;
                }

                var ramps = road.Ramps;
                foreach (var other in Roads)
                {
                    if (ramps.Any(r => other.StartJunction == r || other.EndJunction == r))
                        yield return other;
                }

                break;
            case RoadJunction junction:
                foreach (var (road, _) in junction.Arms())
                    yield return road;
                break;
            case ParkingLot lot:
                foreach (var (road, _) in lot.Entrances())
                    yield return road;
                break;
            case RoadFork fork:
                foreach (var road in Roads.Where(r => r.StartJunction == fork || r.EndJunction == fork))
                    yield return road;
                break;
        }
    }

    /// <summary>What shape a road or junction's ground depends on, as a short fingerprint.</summary>
    /// <summary>
    /// A fingerprint of everything in it as saved: every node under it (not those it makes itself), its
    /// name, place and every setting kept in the scene (not metadata). The same in the editor and in play
    /// for the same level; changed by any edit to it. For telling whether its baked look is out of date.
    /// </summary>
    public string Fingerprint() => FingerprintOf(this, ".", children: true);

    /// <summary>The same of a resource kept in the scene (a material made there, say), by what is stored of it.</summary>
    public static string ResourceFingerprint(GodotObject resource) => FingerprintOf(resource, ".", children: false);

    private static string FingerprintOf(GodotObject of, string at, bool children)
    {
        var text = new System.Text.StringBuilder();
        void Take(GodotObject node, string path, bool deep)
        {
            text.Append(path).Append('{');
            foreach (var property in node.GetPropertyList())
            {
                var usage = (PropertyUsageFlags)property["usage"].AsInt32();
                var name = property["name"].AsString();
                if ((usage & PropertyUsageFlags.Storage) == 0 || name.StartsWith("metadata/") || name == "script")
                    continue;
                // An object by its file (or, one kept in the scene, its kind): not by its id, which differs each run.
                var value = node.Get(name);
                var shown = value.VariantType == Variant.Type.Object
                    ? value.AsGodotObject() is Resource { ResourcePath: { Length: > 0 } file } && !file.Contains("::") ? file : value.AsGodotObject()?.GetClass() ?? "null"
                    : value.ToString();
                text.Append(name).Append('=').Append(shown).Append(';');
            }

            text.Append('}');
            if (!deep || node is not Node parent)
                return;
            foreach (var child in parent.GetChildren())
                Take(child, $"{path}/{child.Name}", true);
        }

        Take(of, at, children);
        return text.ToString().Md5Text();
    }

    private string Key(Node3D part)
    {
        var text = new System.Text.StringBuilder();
        text.Append(System.FormattableString.Invariant($"{Margin:0.###} {Falloff:0.###} {BankCover:0.###};"));
        void Point(System.Numerics.Vector3 v) => text.Append(System.FormattableString.Invariant($"{v.X:0.00},{v.Y:0.00},{v.Z:0.00};"));
        switch (part)
        {
            case Road road:
                text.Append(road.CrossSection.ToString()).Append(road.Closed);
                foreach (var frame in road.Frames())
                    Point(frame.Position);
                foreach (var ramp in road.Ramps)
                    text.Append(ramp.Spec.ToString());
                foreach (var structure in road.StructureSpecs())
                    text.Append(structure.ToString());
                // Its parking widens it: the ground fitted again under that too when it changes.
                foreach (var parking in road.Parking)
                    text.Append(parking.Spec.ToString());
                break;
            case RoadJunction junction:
                text.Append(System.FormattableString.Invariant($"{junction.GlobalPosition.Y:0.00} {junction.Banks.Height:0.00} {junction.Banks.Angle:0.0} {junction.Viaduct};"));
                foreach (var corner in junction.Shape?.Reach ?? new List<System.Numerics.Vector3>())
                    Point(corner);
                break;
            case RoadFork fork:
                text.Append("fork;");
                foreach (var corner in fork.Outline)
                    Point(corner);
                break;
            case ParkingLot lot:
                text.Append("lot;");
                foreach (var corner in lot.OuterEdge)
                    Point(corner);
                break;
        }

        return text.ToString().Md5Text();
    }

    /// <summary>
    /// The holes to open or close in the terrain for these roads: open where a tunnel's tube meets
    /// the ground, closed under the rest of the road (where a tunnel has moved away or gone). Each
    /// as (x grid index, 1 for a hole or 0, z grid index), as they were and as they are to be, only
    /// where that changes.
    /// </summary>
    private static (Vector3[] Before, Vector3[] After) Holes(IEnumerable<Road> roads, IRoadGround ground)
    {
        var spacing = ground.VertexSpacing;
        var wanted = new Dictionary<(int X, int Z), bool>();
        foreach (var road in roads)
        {
            var frames = road.Frames();
            if (frames.Count < 2)
                continue;
            var section = road.CrossSection;
            var structures = road.StructureSpecs();
            // Under the road where it lies on the ground, no holes.
            foreach (var run in RoadStructureBuilder.OnGround(frames, structures))
            {
                foreach (var (at, target) in RoadGround.Targets(run, section, spacing, 0f, 0.01f))
                {
                    if (target.Weight >= 1f)
                        wanted.TryAdd(at, false);
                }
            }

            foreach (var tunnel in structures.Where(s => s.Kind == StructureKind.Tunnel))
            {
                foreach (var at in RoadStructureBuilder.TunnelCells(frames, section, tunnel, spacing))
                    wanted[at] = false;
            }
        }

        var before = new List<Vector3>();
        var after = new List<Vector3>();
        foreach (var ((x, z), hole) in wanted)
        {
            var at = new Vector3(x * spacing, 0f, z * spacing);
            if (ground.SampleHeight(x, z) is null)
                continue;
            var had = ground.IsHole(at);
            if (had == hole)
                continue;
            before.Add(new Vector3(x, had ? 1f : 0f, z));
            after.Add(new Vector3(x, hole ? 1f : 0f, z));
        }

        return (before.ToArray(), after.ToArray());
    }

    /// <summary>Opens or closes holes in the terrain, each as (x grid index, 1 for a hole or 0, z grid index), and redraws it.</summary>
    public void SetHoles(Vector3[] holes) =>
        Ground?.SetHoles(holes.Select(h => ((int)h.X, (int)h.Z, h.Y > 0.5f)));

    /// <summary>Sets grid points' heights, each given as (x grid index, height, z grid index), and redraws the terrain.</summary>
    public void SetHeights(Vector3[] heights)
    {
        // While a job reads the terrain, held back and written once it is done (in the order given).
        if (RoadJob.TerrainRead)
        {
            _heldHeights.Add(heights);
            if (_heldHeights.Count == 1)
                GetTree().ProcessFrame += WriteHeldHeights;
            return;
        }

        Ground?.SetHeights(heights.Select(h => ((int)h.X, (int)h.Z, h.Y)));
    }

    /// <summary>Heights to write to the ground once no job reads it any more.</summary>
    private readonly List<Vector3[]> _heldHeights = [];

    private void WriteHeldHeights()
    {
        if (RoadJob.TerrainRead)
            return;
        GetTree().ProcessFrame -= WriteHeldHeights;
        var held = _heldHeights.ToList();
        _heldHeights.Clear();
        if (!IsInstanceValid(this))
            return;
        foreach (var heights in held)
            Ground?.SetHeights(heights.Select(h => ((int)h.X, (int)h.Z, h.Y)));
        GD.Print($"{Name}: wrote the road ground held back while a job read the terrain.");
    }
}
