using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// Where roads meet: a T-junction, a crossroads, or more. Roads join it by naming it as their
/// start or end junction, with their first or last point on it; it works out from them how far
/// back each is cut, and builds its own surface, lines and collision between them, flat at its
/// height. Moving it brings the roads' ends along. It belongs under the same
/// <see cref="RoadNetwork"/> as its roads.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_roads/Icons/Road-Junction.svg")]
public partial class RoadJunction : Node3D, IFoliageBuffered
{
    /// <summary>
    /// Whether its saved settings have all been put back since it was made: false from when the
    /// scripts are reloaded (it is made anew, already in the tree and built) to the end of that
    /// frame. Settings put back then are not changes, and rebuild nothing: rebuilding everything
    /// after each reload froze the editor for a minute.
    /// </summary>
    private bool _reloadSettled;

    public RoadJunction() => new Callable(this, MethodName.ReloadSettle).CallDeferred();

    private void ReloadSettle() => _reloadSettled = true;

    /// <summary>Whether a setting is being put back as the scripts are reloaded, rather than changed.</summary>
    private bool Reloading => IsInsideTree() && !_reloadSettled;

    private float _cornerRadius = 6f;
    private float _turningRadius = 12f;
    private JunctionControl _control = JunctionControl.Auto;
    private float _greenTime = 20f;
    private float _amberTime = 3f;
    private float _allRedTime = 2f;
    private float _offset;
    private Node3D? _signals;
    private IReadOnlyList<ArmControl> _controls = System.Array.Empty<ArmControl>();
    private SignalPlan? _plan;
    private readonly List<(int Arm, TrafficLight Light)> _lights = new();
    private readonly List<(int Arm, RoadSign Board, float Before)> _boards = new();

    /// <summary>
    /// Its direction signs: for traffic coming in along which arm, the sign, what it says naming
    /// only roads (for when no place can be named), and how far before the junction's middle it stands.
    /// </summary>
    public IReadOnlyList<(int Arm, RoadSign Board, float Before)> Boards => _boards;

    /// <summary>How long the junction's lights have been running, in seconds.</summary>
    public float SignalTime { get; private set; }

    /// <summary>How often its lights are set as its plan has them, in seconds, and how long until next.</summary>
    private const double ShowEvery = 0.2;
    private double _untilShow;

    private static readonly System.Collections.Generic.Dictionary<string, PackedScene?> Scenes = new();

    private static PackedScene? Scene(string name)
    {
        if (!Scenes.TryGetValue(name, out var scene))
            Scenes[name] = scene = ResourceLoader.Exists($"res://addons/unpleasant_roads/Scenes/{name}.tscn") ? ResourceLoader.Load<PackedScene>($"res://addons/unpleasant_roads/Scenes/{name}.tscn") : null;
        return scene;
    }
    private int _armCount = -1;
    private float _shoulderHeight = 1f;
    private bool _matchRoads = true;
    private bool _viaduct;
    private StructureSpec _deck = new(StructureKind.Viaduct, 0f, 0f, PillarWidth: 3f);
    private float _shoulderAngle = RoadCrossSection.WalkableAngle;
    private bool _rebuildQueued;
    private MeshInstance3D? _mesh;
    private StaticBody3D? _body;
    private readonly Dictionary<(ulong Road, bool Start), float> _setbacks = new();

    /// <summary>How round the corners between its roads are, as the radius of the curve.</summary>
    [Export(PropertyHint.Range, "0,40,0.5,suffix:m")]
    public float CornerRadius
    {
        get => _cornerRadius;
        set
        {
            _cornerRadius = value;
            RebuildChanged();
        }
    }

    /// <summary>
    /// Raised or lowered, how far along each of its roads their points come up or down with it:
    /// the whole way at the junction, less further out, none from this far on.
    /// </summary>
    [Export(PropertyHint.Range, "0,100,1,suffix:m")]
    public float RaiseReach { get; set; } = 20f;

    /// <summary>
    /// With a single road, a cul-de-sac: how far its turning circle reaches from its middle to its
    /// outer edge. Never narrower than its road.
    /// </summary>
    [Export(PropertyHint.Range, "3,60,0.5,or_greater,suffix:m")]
    public float TurningRadius
    {
        get => _turningRadius;
        set
        {
            _turningRadius = value;
            RebuildChanged();
        }
    }

    /// <summary>
    /// The signs chosen by hand, in place of what it would put up itself: by slot (see
    /// <see cref="SlotOf"/>), what goes there. For the light or sign for traffic coming in along a
    /// road, an <see cref="ArmControl"/> by name (Priority for none); for a street name sign,
    /// Hidden. A slot not in it is left to the junction. Set by clicking its signs in the editor.
    /// </summary>
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
        if (Shape?.Reach is { Count: >= 3 } outline)
            RoadLinks.FootprintChanged?.Invoke(this, [RoadNetwork.Outline(outline)]);
        else
            QueueRebuild();
    }

    [ExportGroup("Signs")]
    [Export]
    public Godot.Collections.Dictionary<string, string> SignChoices
    {
        get => _signChoices;
        set
        {
            _signChoices = value ?? new Godot.Collections.Dictionary<string, string>();
            RebuildChanged();
        }
    }

    private Godot.Collections.Dictionary<string, string> _signChoices = new();

    /// <summary>What a street name sign's slot holds when it is taken down.</summary>
    public const string Hidden = "Hidden";

    /// <summary>The slot a light or sign of its stands in, kept on the sign, for choosing what goes there.</summary>
    public static readonly StringName SlotMeta = "sign_slot";

    /// <summary>Marks a stand-in for a sign that is not up (none chosen, or taken down), shown only in the editor while it is selected, to click to choose one.</summary>
    public static readonly StringName GhostMeta = "sign_ghost";

    private bool _showGhosts;

    /// <summary>Shows or hides the stand-ins for signs not up: shown while it is selected in the editor.</summary>
    public void ShowGhosts(bool show)
    {
        _showGhosts = show;
        foreach (var node in _signals?.GetChildren().OfType<Node3D>() ?? Enumerable.Empty<Node3D>())
        {
            if (node.HasMeta(GhostMeta))
                node.Visible = show;
        }
    }

    /// <summary>A road meeting it, by its path from the network and which of its ends: what its slots are named by.</summary>
    private string ArmKey((Road Road, bool Start) arm) =>
        $"{(Network is { } network ? network.GetPathTo(arm.Road).ToString() : arm.Road.Name.ToString())}:{(arm.Start ? "start" : "end")}";

    /// <summary>The slot of the light or sign for traffic coming in along a road.</summary>
    private string ControlSlot((Road Road, bool Start) arm) => $"control:{ArmKey(arm)}";

    /// <summary>The slot of a street name sign of a road, on its left or right corner looking out along it.</summary>
    private string NameSlot((Road Road, bool Start) arm, bool right) => $"name:{ArmKey(arm)}:{(right ? "right" : "left")}";

    /// <summary>The slot a light or sign of its stands in, if it is one of those that can be chosen.</summary>
    public static string? SlotOf(Node sign) => sign.HasMeta(SlotMeta) ? sign.GetMeta(SlotMeta).AsString() : null;

    /// <summary>What can go in a slot, by name, Auto first (left to the junction).</summary>
    public static IReadOnlyList<string> ChoicesFor(string slot) => slot.StartsWith("control:")
        ? new[] { "Auto", nameof(ArmControl.Lights), nameof(ArmControl.Stop), nameof(ArmControl.GiveWay), nameof(ArmControl.Priority) }
        : new[] { "Auto", Hidden };

    /// <summary>What is chosen for a slot: Auto when nothing is.</summary>
    public string ChoiceFor(string slot) => _signChoices.TryGetValue(slot, out var choice) ? choice : "Auto";

    /// <summary>
    /// The light or sign of its (stand-ins included, while shown) nearest along a ray from the
    /// camera that hits it, for clicking it in the editor; none if the ray hits none.
    /// </summary>
    public Node3D? SignAlong(Vector3 from, Vector3 direction)
    {
        Node3D? best = null;
        var nearest = float.MaxValue;
        foreach (var sign in _signals?.GetChildren().OfType<Node3D>() ?? Enumerable.Empty<Node3D>())
        {
            if (!sign.Visible || SlotOf(sign) is null)
                continue;
            foreach (var mesh in sign.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>())
            {
                if (!mesh.IsVisibleInTree())
                    continue;
                var box = mesh.GlobalTransform * mesh.GetAabb();
                if (!box.IntersectsSegment(from, from + direction.Normalized() * 2000f))
                    continue;
                var distance = from.DistanceTo(box.GetCenter());
                if (distance < nearest)
                    (nearest, best) = (distance, sign);
            }
        }

        return best;
    }

    /// <summary>Makes a sign a stand-in: see-through, and shown only while the junction is selected.</summary>
    private void Ghost(Node3D sign)
    {
        sign.SetMeta(GhostMeta, true);
        sign.Visible = _showGhosts;
        foreach (var part in sign.FindChildren("*", nameof(GeometryInstance3D), true, false).OfType<GeometryInstance3D>())
            part.Transparency = 0.7f;
        foreach (var label in sign.FindChildren("*", nameof(Label3D), true, false).OfType<Label3D>())
            label.Modulate = label.Modulate with { A = 0.3f };
    }

    /// <summary>Whether it is a cul-de-sac: a single road turning round at it.</summary>
    public bool IsCulDeSac => _armCount == 1;

    /// <summary>Whether its banks match its roads' (the deepest of them), rather than its own shoulder height and angle.</summary>
    [ExportGroup("Banks")]
    [Export]
    public bool MatchRoads
    {
        get => _matchRoads;
        set
        {
            _matchRoads = value;
            NotifyPropertyListChanged();
            RebuildChanged();
        }
    }

    /// <summary>Its own bank settings are read only in the Inspector while it matches its roads', as they are not used.</summary>
    public override void _ValidateProperty(Godot.Collections.Dictionary property)
    {
        var name = property["name"].AsStringName();
        // A cul-de-sac is sized by its turning radius; a junction of several roads by its corners.
        if ((name == PropertyName.CornerRadius && IsCulDeSac) || (name == PropertyName.TurningRadius && !IsCulDeSac))
            property["usage"] = (int)(property["usage"].As<PropertyUsageFlags>() & ~PropertyUsageFlags.Editor);
        if (_matchRoads && (name == PropertyName.ShoulderHeight || name == PropertyName.ShoulderAngle))
            property["usage"] = (int)(property["usage"].As<PropertyUsageFlags>() | PropertyUsageFlags.ReadOnly);
    }

    /// <summary>Its banks' height and angle: its roads' (the deepest of them) while it matches them, else its own.</summary>
    public (float Height, float Angle) Banks
    {
        get
        {
            if (!_matchRoads)
                return (_shoulderHeight, _shoulderAngle);
            var roads = Arms().Select(a => a.Road).ToList();
            if (roads.Count == 0)
                return (_shoulderHeight, _shoulderAngle);
            var deepest = roads.OrderByDescending(r => Mathf.Abs(r.ShoulderHeight)).First();
            return (deepest.ShoulderHeight, deepest.ShoulderAngle);
        }
    }

    /// <summary>How far the banks round its corners drop from it (negative: rise), as they slope away at the shoulder angle; when not matching its roads.</summary>
    [Export(PropertyHint.Range, "-30,30,0.1,or_greater,or_less,suffix:m")]
    public float ShoulderHeight
    {
        get => _shoulderHeight;
        set
        {
            _shoulderHeight = value;
            RebuildChanged();
        }
    }

    /// <summary>How steep its banks are. The default is the steepest the player can walk up.</summary>
    [Export(PropertyHint.Range, "5,85,0.5,degrees")]
    public float ShoulderAngle
    {
        get => _shoulderAngle;
        set
        {
            _shoulderAngle = value;
            RebuildChanged();
        }
    }

    /// <summary>Whether it stands on a deck and pillar as a viaduct, over the ground, rather than on it.</summary>
    [ExportGroup("Viaduct")]
    [Export]
    public bool Viaduct
    {
        get => _viaduct;
        set
        {
            _viaduct = value;
            RebuildChanged();
        }
    }

    [Export(PropertyHint.Range, "0.3,5,0.05,suffix:m")]
    public float DeckDepth
    {
        get => _deck.DeckDepth;
        set
        {
            _deck = _deck with { DeckDepth = value };
            RebuildChanged();
        }
    }

    [Export(PropertyHint.Range, "0,3,0.05,suffix:m")]
    public float BarrierHeight
    {
        get => _deck.BarrierHeight;
        set
        {
            _deck = _deck with { BarrierHeight = value };
            RebuildChanged();
        }
    }

    /// <summary>
    /// How traffic is controlled here: Auto gives traffic lights where four or more roads meet,
    /// priority for the road through and give way signs for the rest where three do, and nothing
    /// where two do.
    /// </summary>
    [ExportGroup("Control")]
    [Export]
    public JunctionControl Control { get => _control; set { _control = value; RebuildChanged(); } }

    /// <summary>How long each phase of its lights is green, then amber, then red all round, in seconds.</summary>
    [Export(PropertyHint.Range, "3,120,1,suffix:s")]
    public float GreenTime { get => _greenTime; set { _greenTime = value; RebuildChanged(); } }

    [Export(PropertyHint.Range, "1,10,0.5,suffix:s")]
    public float AmberTime { get => _amberTime; set { _amberTime = value; RebuildChanged(); } }

    [Export(PropertyHint.Range, "0,10,0.5,suffix:s")]
    public float AllRedTime { get => _allRedTime; set { _allRedTime = value; RebuildChanged(); } }

    /// <summary>Where in their cycle its lights start, in seconds: to stagger them with neighbours'.</summary>
    [Export(PropertyHint.Range, "0,300,1,suffix:s")]
    public float Offset { get => _offset; set { _offset = value; RebuildChanged(); } }

    /// <summary>What traffic coming in along each arm (in its arms' order) must do, as last built.</summary>
    public IReadOnlyList<ArmControl> ArmControls => _controls;

    /// <summary>The colour of the lights for traffic coming in along an arm now; none where it has no lights.</summary>
    public Light? LightFor(int arm) =>
        _plan is { } plan && arm < _controls.Count && _controls[arm] == ArmControl.Lights ? plan.LightFor(arm, SignalTime) : null;

    /// <summary>Whether it has traffic lights to run.</summary>
    public bool HasLights => _lights.Count > 0;

    /// <summary>
    /// Its lights run on by a frame's time: called by its road network for all its junctions in one
    /// go (each junction called by Godot every frame cost more than the lights did). In the game only;
    /// in the editor they show how they start.
    /// </summary>
    public void RunLights(double delta)
    {
        if (_lights.Count == 0)
            return;
        SignalTime += (float)delta;
        // A few times a second: the lights change every few seconds.
        _untilShow -= delta;
        if (_untilShow > 0)
            return;
        _untilShow = ShowEvery;
        ShowLights();
    }

    private void ShowLights()
    {
        foreach (var (arm, light) in _lights)
        {
            if (IsInstanceValid(light) && LightFor(arm) is { } colour)
                light.State = colour;
        }
    }

    /// <summary>
    /// Puts up its lights and signs: for traffic coming in along each road, a traffic light, a stop
    /// or give way sign, or nothing, on that traffic's right where the road meets it; and a street
    /// name sign there for each named road.
    /// </summary>
    private void PutUpSignals(IReadOnlyList<(Road Road, bool Start)> arms, JunctionShape shape)
    {
        _signals ??= AddInternal(new Node3D { Name = "Signals" });
        foreach (var old in _signals.GetChildren())
        {
            _signals.RemoveChild(old);
            old.QueueFree();
        }

        _lights.Clear();
        _boards.Clear();

        var armShapes = arms.Select(a => a.Road.ArmAt(a.Start)!.Value).ToList();
        var controls = RoadSignals.Controls(armShapes, _control, arms.Select(a => a.Road.EffectiveSpeedLimit).ToList()).ToList();
        // As chosen by hand, where one is.
        for (var i = 0; i < controls.Count; i++)
        {
            if (_signChoices.TryGetValue(ControlSlot(arms[i]), out var chosen) && System.Enum.TryParse<ArmControl>(chosen, out var control))
                controls[i] = control;
        }

        _controls = controls;
        _plan = _controls.Any(c => c == ArmControl.Lights) ? RoadSignals.Plan(armShapes, _greenTime, _amberTime, _allRedTime, _offset) : null;
        if (shape.Ends is not { } ends || arms.Count < 2)
            return;

        for (var i = 0; i < arms.Count && i < ends.Count; i++)
        {
            if (!RoadSignals.HasIncoming(armShapes[i]))
                continue;
            // On a viaduct, on the shoulder rather than out past the edge over the drop.
            var (at, travelling) = RoadSignals.Place(ends[i], armShapes[i], _viaduct || Carried(arms[i], shape.Setbacks[i]) ? RoadSignals.CarriedKerb : RoadSignals.Kerb, Network?.Ground);
            var name = _controls[i] switch
            {
                ArmControl.Lights => "TrafficLight",
                ArmControl.Stop => "Sign-Stop",
                ArmControl.GiveWay => "Sign-GiveWay",
                _ => null,
            };
            var place = new Transform3D(Basis.LookingAt(travelling.ToGodot(), Vector3.Up), at.ToGodot());
            // With none, in the editor, a stand-in to click to choose one.
            var ghost = name is null;
            if (ghost && !Engine.IsEditorHint())
                continue;
            if (Scene(name ?? "Sign-GiveWay")?.Instantiate() is Node3D sign)
            {
                _signals.AddChild(sign);
                RoadDrawDistance.LimitSmall(sign);
                sign.GlobalTransform = place;
                sign.SetMeta(SlotMeta, ControlSlot(arms[i]));
                if (ghost)
                    Ghost(sign);
                else if (sign is TrafficLight light)
                    _lights.Add((i, light));
            }
        }

        // Street name signs on both corners of each named road, along it and facing in across it.
        // A little way along the road, clear of the lights and signs at its corners.
        for (var i = 0; i < arms.Count && i < ends.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(arms[i].Road.StreetName) || !armShapes[i].OutwardBands().Any(b => b.Band.Kind == BandKind.Lane))
                continue;
            var kerb = _viaduct || Carried(arms[i], shape.Setbacks[i]) ? RoadSignals.CarriedKerb : RoadSignals.Kerb;
            var (left, right) = armShapes[i].OuterEdges();
            var outward = new System.Numerics.Vector3(ends[i].Forward.X, 0f, ends[i].Forward.Z);
            if (outward.LengthSquared() < 1e-6f)
                continue;
            outward = System.Numerics.Vector3.Normalize(outward);
            var across = new System.Numerics.Vector3(ends[i].Right.X, 0f, ends[i].Right.Z);
            across = across.LengthSquared() < 1e-6f ? System.Numerics.Vector3.Cross(System.Numerics.Vector3.UnitY, outward) * -1f : System.Numerics.Vector3.Normalize(across);
            // Each on its sidewalk's edge where the ground drops away beside it.
            var ground = Network?.Ground;
            var rightKerb = RoadSignals.KerbBeside(ground, ends[i], right, 1f, kerb);
            var leftKerb = RoadSignals.KerbBeside(ground, ends[i], left, -1f, kerb);
            foreach (var (offset, facing, onRight) in new[] { (right + rightKerb, across, true), (left - leftKerb, -across, false) })
            {
                // Taken down by hand: in the editor, a stand-in to click to put it back.
                var slot = NameSlot(arms[i], onRight);
                var hidden = ChoiceFor(slot) == Hidden;
                if (hidden && !Engine.IsEditorHint())
                    continue;
                if (Scene("Sign-StreetName")?.Instantiate() is not RoadSign street)
                    break;
                street.Text = arms[i].Road.StreetName;
                _signals.AddChild(street);
                RoadDrawDistance.LimitSmall(street);
                var at = ends[i].Across(offset) + outward * StreetSignAlong;
                street.GlobalTransform = new Transform3D(Basis.LookingAt(facing.ToGodot(), Vector3.Up), at.ToGodot());
                street.SetMeta(SlotMeta, slot);
                if (hidden)
                    Ghost(street);
            }
        }

        if (!_viaduct)
            ShareCornerPoles(shape);

        // A direction sign on each road coming in, a way before the junction, naming the towns it leads to.
        for (var i = 0; i < arms.Count; i++)
        {
            // As the road says for this end of it: none if Off.
            var wanted = arms[i].Start ? arms[i].Road.DirectionSignAtStart : arms[i].Road.DirectionSignAtEnd;
            if (wanted == DirectionSign.Off || !RoadSignals.HasIncoming(armShapes[i]) || armShapes[i].Along is not { } along)
                continue;
            // Off any viaduct or tunnel: further back along the road until it is on the ground, or none.
            if (arms[i].Road.FullFrames() is not { Count: > 1 } full)
                continue;
            var roadLength = full[^1].Distance - full[0].Distance;
            var before = shape.Setbacks[i] + DirectionSignBefore;
            // On a road too short for that, halfway along what shows of it between its junctions.
            var farEnd = arms[i].Start ? arms[i].Road.EndJunction : arms[i].Road.StartJunction;
            var farCut = farEnd is RoadJunction far ? far.SetbackFor(arms[i].Road, !arms[i].Start) : 5f;
            // Where the road says, if it says: kept on what shows of the road.
            var chosen = arms[i].Start ? arms[i].Road.DirectionSignDistanceAtStart : arms[i].Road.DirectionSignDistanceAtEnd;
            if (chosen > 0f)
                before = Mathf.Min(shape.Setbacks[i] + chosen, roadLength - farCut);
            else if (before >= roadLength - farCut)
            {
                if (roadLength - farCut - shape.Setbacks[i] < ShortRoadSignRoom)
                    continue;
                before = (shape.Setbacks[i] + roadLength - farCut) / 2f;
            }

            while (before < roadLength - 5f && Carried(arms[i], before) && before < shape.Setbacks[i] + DirectionSignBefore + DirectionSignSlide)
                before += 5f;
            if (before >= roadLength - 5f || Carried(arms[i], before))
                continue;
            // Hidden until the network, once it has its lanes, fills in the towns it points to.
            if (Scene("Sign-Direction")?.Instantiate() is not RoadSign board)
                continue;
            var (at, travelling) = RoadSignals.Place(along(before), armShapes[i], ground: Network?.Ground);
            board.Visible = false;
            _signals.AddChild(board);
            RoadDrawDistance.LimitSmall(board);
            board.GlobalTransform = new Transform3D(Basis.LookingAt(travelling.ToGodot(), Vector3.Up), at.ToGodot());
            _boards.Add((i, board, before));
        }

        // The towns they point to are filled in once the network's lanes are worked out.
        Network?.QueueBoards();

        ShowLights();
    }

    /// <summary>How high above the ground a street name plate stands on its own pole, as its scene has it, in metres.</summary>
    private const float NamePlateHeight = 2.75f;

    /// <summary>How far apart street name plates stack up a shared pole, in metres.</summary>
    private const float NamePlateStep = 0.32f;

    /// <summary>How far above whatever else is on a shared pole its first street name plate stands, in metres.</summary>
    private const float NamePlateAbove = 0.25f;

    /// <summary>
    /// Where two or more of its lights and signs stand round the same corner (between two roads),
    /// they go on one pole at the middle of the corner, next to its sidewalk: a light or a stop or
    /// give way sign keeps its height, and the street name signs stack on top. The pole kept is
    /// the light's if there is one, made as tall as it needs to be; the rest are hidden.
    /// </summary>
    private void ShareCornerPoles(JunctionShape shape)
    {
        if (_signals is null || shape.Corners.Count == 0)
            return;
        var centre = GlobalPosition.ToNumerics();

        // Each light or sign, by the corner it is nearest.
        var byCorner = new Dictionary<int, List<Node3D>>();
        foreach (var item in _signals.GetChildren().OfType<Node3D>().Where(s => !s.HasMeta(GhostMeta)))
        {
            var at = item.GlobalPosition.ToNumerics();
            var nearest = Enumerable.Range(0, shape.Corners.Count)
                .Where(k => shape.Corners[k].Count > 0)
                .OrderBy(k => shape.Corners[k].Min(p => System.Numerics.Vector3.DistanceSquared(p with { Y = at.Y }, at)))
                .DefaultIfEmpty(-1).First();
            if (nearest < 0)
                continue;
            if (!byCorner.TryGetValue(nearest, out var list))
                byCorner[nearest] = list = new List<Node3D>();
            list.Add(item);
        }

        foreach (var (corner, items) in byCorner)
        {
            if (items.Count < 2)
                continue;

            // The middle of the corner, out past its sidewalk's edge.
            var curve = shape.Corners[corner];
            var middle = curve[curve.Count / 2];
            var away = new System.Numerics.Vector3(middle.X - centre.X, 0f, middle.Z - centre.Z);
            if (away.LengthSquared() < 1e-6f)
                continue;
            var pole = (middle + System.Numerics.Vector3.Normalize(away) * RoadSignals.Kerb).ToGodot();
            // Where the ground drops well below the corner there: on its edge instead.
            if (Network?.Ground is { } ground && ground.HeightAt(pole) is var below && !float.IsNaN(below) && below < pole.Y - RoadSignals.BigDrop)
                pole = (middle + System.Numerics.Vector3.Normalize(away) * RoadSignals.CarriedKerb).ToGodot();

            var names = items.OfType<RoadSign>().Where(IsStreetName).ToList();
            // One plate for each street: a road going on through it (two roads of one name, either
            // side) would have its name on the corner between them twice.
            foreach (var twice in names.GroupBy(n => n.Text).SelectMany(g => g.Skip(1)).ToList())
            {
                names.Remove(twice);
                items.Remove(twice);
                _signals.RemoveChild(twice);
                twice.QueueFree();
            }

            var others = items.Except(names).ToList();
            foreach (var item in items)
                item.GlobalPosition = pole;

            // The top of whatever is on it below the names: a light's head, a sign's face.
            var top = others.Count == 0 ? 0f : others.Max(TopOf);
            for (var k = 0; k < names.Count; k++)
            {
                var height = (others.Count == 0 ? NamePlateHeight : top + NamePlateAbove) + k * NamePlateStep;
                names[k].GlobalPosition = pole + Vector3.Up * (height - NamePlateHeight);
            }

            var keeper = others.OfType<TrafficLight>().FirstOrDefault() ?? others.FirstOrDefault() ?? names[0];
            foreach (var item in items.Where(i => i != keeper))
            {
                if (item.GetNodeOrNull<Node3D>("Pole") is { } hidden)
                    hidden.Visible = false;
            }

            // As tall as the highest name plate on it.
            if (names.Count > 0 && keeper.GetNodeOrNull<MeshInstance3D>("Pole") is { Mesh: CylinderMesh cylinder } kept)
            {
                var bottom = kept.Position.Y - cylinder.Height / 2f;
                var reach = (names[^1].GlobalPosition.Y - pole.Y) + NamePlateHeight + 0.15f;
                if (reach > kept.Position.Y + cylinder.Height / 2f)
                {
                    kept.Scale = new Vector3(1f, (reach - bottom) / cylinder.Height, 1f);
                    kept.Position = kept.Position with { Y = (reach + bottom) / 2f };
                }
            }
        }
    }

    private static bool IsStreetName(RoadSign sign) => sign.SceneFilePath.EndsWith("Sign-StreetName.tscn");

    /// <summary>How high the top of a light's head or a sign's face is above its foot, in metres.</summary>
    private static float TopOf(Node3D item)
    {
        var top = 0f;
        foreach (var part in new[] { "Head", "Face" })
        {
            if (item.GetNodeOrNull<Node3D>(part) is { } node)
            {
                var box = node.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>()
                    .Concat(node is MeshInstance3D self ? new[] { self } : System.Array.Empty<MeshInstance3D>())
                    .Select(m => (item.GlobalTransform.AffineInverse() * m.GlobalTransform * m.GetAabb()).End.Y)
                    .DefaultIfEmpty(0f).Max();
                top = Mathf.Max(top, box);
            }
        }

        return top;
    }

    /// <summary>Whether a road meeting it is carried by a viaduct or tunnel this far out from it along the road.</summary>
    private static bool Carried((Road Road, bool Start) arm, float fromJunction)
    {
        var frames = arm.Road.FullFrames();
        if (frames.Count < 2)
            return false;
        var along = arm.Start ? fromJunction : frames[^1].Distance - fromJunction;
        return arm.Road.StructureSpecs().Any(s => s.Covers(along));
    }

    /// <summary>How far along a road from where it meets a junction its street name signs stand, in metres.</summary>
    private const float StreetSignAlong = 1.5f;

    /// <summary>How much of a short road has to show between its junctions for a direction sign to stand halfway along it, in metres.</summary>
    private const float ShortRoadSignRoom = 4f;

    /// <summary>How far before a junction its direction signs stand, past where its roads are cut off, in metres.</summary>
    public const float DirectionSignBefore = 50f;

    /// <summary>How much further back a direction sign may go to stand on the ground rather than on a viaduct, in metres.</summary>
    public const float DirectionSignSlide = 100f;

    /// <summary>What it was last built as: none before it has been.</summary>
    public JunctionShape? Shape { get; private set; }

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

    /// <summary>
    /// The roads meeting here, each with whether it starts here (rather than ends), in a steady
    /// order. A road with fewer than two points is not one yet (half drawn, or cut down to one): it
    /// neither shapes the junction nor is moved by it.
    /// </summary>
    public IReadOnlyList<(Road Road, bool Start)> Arms()
    {
        var arms = new List<(Road, bool)>();
        foreach (var road in Network?.Roads ?? System.Array.Empty<Road>())
        {
            if (road.Points.Count < 2)
                continue;
            if (road.StartJunction == this)
                arms.Add((road, true));
            if (road.EndJunction == this)
                arms.Add((road, false));
        }

        return arms;
    }

    /// <summary>How far back from here a road meeting it is cut, at its start or its end.</summary>
    public float SetbackFor(Road road, bool start) =>
        _setbacks.TryGetValue((road.GetInstanceId(), start), out var setback) ? setback : RoadJunctionShaper.MinimumSetback;

    public override void _Ready()
    {
        _notifiedAt = GlobalTransform;
        SetNotifyTransform(true);
        QueueRebuild();
    }

    // Taken away: the viaducts over it stand pillars where it was.
    public override void _ExitTree() => Network?.FootprintRemoved(this);

    /// <summary>
    /// A cul-de-sac's place, its height included, is its road's: where that road's end is, it goes.
    /// A junction of several roads keeps its own: a road's end moved off it goes straight back.
    /// </summary>
    public void KeepRoadEnd(Road road, RoadPoint end)
    {
        if (!IsInsideTree() || end.GlobalPosition.Near(GlobalPosition))
            return;
        var arms = Arms();
        if (arms.Count == 1 && arms[0].Road == road)
            GlobalPosition = end.GlobalPosition;
        else
            end.GlobalPosition = GlobalPosition;
    }

    public override void _Notification(int what)
    {
        var was = _notifiedAt;
        if (what != NotificationTransformChanged || !this.Moved(ref _notifiedAt))
            return;

        // Raised or lowered (a junction of several roads; a cul-de-sac goes where its road's end does):
        // the points near it on its roads too, by less further out.
        var arms = Arms();
        if (was is { } before && arms.Count > 1 && !Reloading && GlobalPosition.Y - before.Origin.Y is var rise && Mathf.Abs(rise) > 1e-4f)
            foreach (var (road, start) in arms)
                CarryRise(road, start, rise);

        // Bring the roads' ends along, which rebuilds them, and then this.
        foreach (var (road, start) in arms)
        {
            var points = road.Points;
            if (points.Count == 0)
                continue;
            var end = start ? points[0] : points[^1];
            if (!end.GlobalPosition.Near(GlobalPosition))
                end.GlobalPosition = GlobalPosition;
        }

        QueueRebuild();
    }

    private Transform3D? _notifiedAt;

    /// <summary>A road's points, out from its end here, raised by rise at the junction, fading to nothing <see cref="RaiseReach"/> along it.</summary>
    private void CarryRise(Road road, bool start, float rise)
    {
        var points = road.Points;
        var along = 0f;
        for (var k = 1; k < points.Count - 1; k++)
        {
            var (prev, point) = start ? (points[k - 1], points[k]) : (points[^k], points[^(k + 1)]);
            // Level distances, so lowering it again brings each point back exactly.
            along += (prev.GlobalPosition with { Y = 0f }).DistanceTo(point.GlobalPosition with { Y = 0f });
            if (along >= RaiseReach)
                break;
            point.GlobalPosition += Vector3.Up * rise * (1f - along / RaiseReach);
        }
    }

    /// <summary>A setting changed: rebuilt, but not as the scripts are reloaded.</summary>
    private void RebuildChanged()
    {
        if (!Reloading)
            QueueRebuild();
    }

    public void QueueRebuild()
    {
        if (_rebuildQueued || !IsInsideTree())
            return;
        _rebuildQueued = true;
        // Not if a road meeting it has had it build first.
        Callable.From(() =>
        {
            if (_rebuildQueued)
                Rebuild();
        }).CallDeferred();
    }

    /// <summary>
    /// A road meeting it built again: it builds again too, if that road's line or cross-section is
    /// not what it was shaped round last time (as the level loads, it is, and need not).
    /// </summary>
    public void RoadChanged()
    {
        if (!_rebuildQueued && !ArmsShape().SequenceEqual(_builtArms))
            QueueRebuild();
    }

    /// <summary>What it is shaped round: each road meeting it, which end, its points and its cross-section.</summary>
    private IEnumerable<(ulong Road, bool Start, string Shape)> ArmsShape() =>
        Arms().Select(a => (a.Road.GetInstanceId(), a.Start,
            string.Join(";", a.Road.Points.Select(p => p.ToCurvePoint().ToString())) + a.Road.CrossSection));

    private List<(ulong Road, bool Start, string Shape)> _builtArms = new();

    /// <summary>
    /// Builds it now if it was to build at the end of the frame, or has never been shaped (in the
    /// editor, a road can come to build before its junction has asked to); whether it did.
    /// </summary>
    public bool BuildIfQueued()
    {
        if (!_rebuildQueued && (Shape is not null || !IsInsideTree()))
            return false;
        Rebuild();
        return true;
    }

    private void Rebuild()
    {
        _rebuildQueued = false;
        if (!IsInsideTree())
            return;

        _builtArms = ArmsShape().ToList();
        // Its gizmo, for clicking it in the editor, round what it builds now.
        UpdateGizmos();
        _mesh ??= AddInternal(new MeshInstance3D { Name = "Mesh" });
        _body ??= AddInternal(new StaticBody3D { Name = "Body" });
        foreach (var old in _body.GetChildren())
        {
            _body.RemoveChild(old);
            old.QueueFree();
        }

        var arms = Arms().Where(a => a.Road.ArmAt(a.Start) is not null).ToList();
        // Which of its sizes the Inspector shows follows how many roads meet it.
        if (arms.Count != _armCount)
        {
            _armCount = arms.Count;
            NotifyPropertyListChanged();
        }

        var shape = RoadJunctionShaper.Build(GlobalPosition.ToNumerics(), arms.Select(a => a.Road.ArmAt(a.Start)!.Value).ToList(), _cornerRadius,
            _viaduct ? 0f : Banks.Height, Banks.Angle, _turningRadius, carried: _viaduct);
        Shape = shape;
        PutUpSignals(arms, shape);
        Network?.TrafficChanged();

        // Where it lies, for viaducts over it to keep their pillars off it.
        if (shape.Reach.Count >= 3)
            Network?.FootprintChanged(this, new[] { RoadNetwork.Outline(shape.Reach) });

        // A road whose cut changed builds again to meet the new edge.
        for (var i = 0; i < arms.Count; i++)
        {
            var key = (arms[i].Road.GetInstanceId(), arms[i].Start);
            if (!_setbacks.TryGetValue(key, out var had) || Mathf.Abs(had - shape.Setbacks[i]) > 1e-3f)
            {
                _setbacks[key] = shape.Setbacks[i];
                arms[i].Road.QueueRebuild();
            }
        }

        if (shape.Surface.Indices.Count == 0)
        {
            _mesh.Mesh = null;
            return;
        }

        var toLocal = GlobalTransform.AffineInverse();
        var arrays = new ArrayMesh();
        var surfaceMaterial = arms.Select(a => a.Road.SurfaceMaterial).FirstOrDefault(m => m is not null) ?? Road.DefaultMaterial("Asphalt");
        Road.AddSurface(arrays, shape.Surface, surfaceMaterial, toLocal);
        Road.AddSurface(arrays, shape.WhiteLines, Road.DefaultMaterial("Road-Line-White"), toLocal);
        var bankMaterial = arms.Select(a => a.Road.BankMaterial).FirstOrDefault(m => m is not null) ?? surfaceMaterial;
        Road.AddSurface(arrays, shape.Banks, bankMaterial, toLocal);
        if (shape.Sidewalks is { } sidewalks)
            Road.AddSurface(arrays, sidewalks, Road.DefaultMaterial("Sidewalk"), toLocal);
        _mesh.Mesh = arrays;

        // As a viaduct, its deck, barriers and pillar.
        var concrete = new MeshData();
        if (_viaduct)
        {
            float? Ground(System.Numerics.Vector3 at) => arms.Select(a => a.Road.GroundAt(at)).FirstOrDefault(h => h is not null);
            concrete = RoadStructureBuilder.JunctionDeck(shape.Middle ?? GlobalPosition.ToNumerics(), shape.Reach, shape.Corners, _deck, Ground,
                (at, half) => Network?.RoadUnder(at, half) == true);
            var structureMaterial = arms.Select(a => a.Road.StructureMaterial).FirstOrDefault(m => m is not null) ?? Road.DefaultMaterial("Concrete");
            Road.AddSurface(arrays, concrete, structureMaterial, toLocal);
            _mesh.Mesh = arrays;
        }

        foreach (var solid in new[] { shape.Surface, shape.Banks, shape.Sidewalks ?? new MeshData(), concrete })
        {
            if (solid.Indices.Count == 0)
                continue;
            var faces = solid.Indices.Select(i => toLocal * solid.Vertices[i].ToGodot()).ToArray();
            _body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = faces } });
        }
    }

    private T AddInternal<T>(T node) where T : Node
    {
        AddChild(node, false, InternalMode.Back);
        return node;
    }
}
