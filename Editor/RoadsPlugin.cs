#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantRoads.Editor;

/// <summary>
/// Draws roads in the 3D editor. With Draw Roads on in the viewport's toolbar and a RoadNetwork,
/// a Road or a RoadPoint selected:
/// <list type="bullet">
/// <item>a click on the ground adds a road point: after the selected point, or at the end of the
/// selected road; with a network selected, or after finishing a road, it starts a new road of the
/// chosen preset;</item>
/// <item>dragging a point moves it along the ground;</item>
/// <item>ctrl-clicking a point deletes it;</item>
/// <item>right-click or escape finishes the road, so the next click starts another;</item>
/// <item>clicking a junction's marker, another road's loose end, or the middle of another road
/// joins the road being drawn there at a junction: the one clicked, a new one at that end, or a
/// new one splitting that road in two (opening it, if it is a ring);</item>
/// <item>with Exit ramp or Entrance ramp picked (or shift held), clicking a road builds that ramp into it on the side clicked, its
/// far end where the click was, and starts the ramp's own road there.</item>
/// </list>
/// With a junction selected (Draw Roads off), clicking one of its lights or signs picks what
/// stands there; the places with nothing up show faintly, to click too.
/// Every change is one step to undo. Points go where the mouse ray hits something solid, or else
/// where the terrain says its ground is, so whether the terrain collides in the editor does not matter.
/// </summary>
[Tool]
public partial class RoadsPlugin : EditorPlugin
{
    /// <summary>How near the mouse has to be to a point's marker to pick it, in pixels.</summary>
    private const float PickRadius = 12f;

    /// <summary>How far above the ground a new point goes: the depth fitting the terrain sinks it by, so fitting leaves it be.</summary>
    private const float AboveGround = RoadGround.Sink;

    private static readonly (string Name, RoadCrossSection Section)[] Presets =
    {
        ("Two-lane road", RoadCrossSection.TwoLane),
        ("Four-lane road", new RoadCrossSection(2, 2, 3.5f, 1f, 1f)),
        ("Divided highway (3 + 3)", RoadCrossSection.Divided(3)),
        ("One-way lane", RoadCrossSection.OneWay(1)),
    };

    private HBoxContainer? _toolbar;
    private Button? _drawButton;
    private OptionButton? _presetMenu;
    private Button? _lanesButton;

    /// <summary>How far from the camera lanes are shown, in metres.</summary>
    private const float LaneViewDistance = 500f;
    private OptionButton? _rampMenu;
    private SpinBox? _rampLanes;
    private SpinBox? _rampLength;

    /// <summary>Where the first click put a viaduct or tunnel's start, waiting for the click at its end.</summary>
    private (Road Road, float Distance)? _structureStart;

    /// <summary>How long a new ramp takes to swing away from the road once its lane splits off.</summary>
    private const float DivergeLength = 80f;

    private Node? _selected;
    private Vector2 _mouse;
    private Vector3? _hover;
    private RoadPoint? _hoverPoint;
    private RoadJunction? _hoverJunction;
    private RoadPoint? _dragging;

    /// <summary>A point just placed, the mouse still held, being raised or lowered by dragging up or down.</summary>
    private RoadPoint? _raising;

    /// <summary>The junction being raised or lowered by a shift-drag on it, where it was, and the mouse's height on screen at the press.</summary>
    private RoadJunction? _liftingJunction;

    private Vector3 _liftFrom;
    private float _liftPressY;
    private Vector3 _raiseBase;
    private float _raiseFromY;
    private float _metresPerPixel;

    /// <summary>How high above the ground a point has to be raised for the road up to it to become a viaduct.</summary>
    private const float ViaductHeight = 1f;

    /// <summary>The steps a point's height above the ground goes in while raising it with ctrl held.</summary>
    private const float RaiseStep = 0.5f;

    /// <summary>Where the right button went down, to tell a click (which finishes the road) from turning the camera.</summary>
    private Vector2? _rightPressedAt;
    private Vector3 _dragFrom;

    /// <summary>How wide and long, on screen, the selected point's gizmo arrow for raising it is, in pixels.</summary>
    private const float GizmoArrowWidth = 22f;
    private const float GizmoArrowLength = 120f;

    /// <summary>Where the point being dragged was, and the mouse, when raising it with shift began.</summary>
    private float _dragBaseY;
    private float _dragPressY;

    /// <summary>How high above the ground the point being dragged is, which it keeps as it moves.</summary>
    private float _dragHeight = AboveGround;
    private bool _finished;

    private bool Drawing => _drawButton?.ButtonPressed == true;

    /// <summary>Lets roads be clicked to select them.</summary>
    private RoadGizmoPlugin? _gizmos;

    /// <summary>Lets junctions, parking lots, fences, plots and building areas be clicked to select them.</summary>
    private PickGizmoPlugin? _pickGizmos;

    private void ShowToggled(bool on) => UpdateOverlays();

    // Something else to build picked: any viaduct or tunnel half placed is dropped.
    private void BuildPicked(long index) => _structureStart = null;

    public override void _EnterTree()
    {
        _gizmos = new RoadGizmoPlugin();
        AddNode3DGizmoPlugin(_gizmos);
        _pickGizmos = new PickGizmoPlugin();
        AddNode3DGizmoPlugin(_pickGizmos);
        _parkingGizmos = new ParkingGizmoPlugin();
        AddNode3DGizmoPlugin(_parkingGizmos);
        _gapGizmos = new BarrierGapGizmoPlugin();
        AddNode3DGizmoPlugin(_gapGizmos);
        AddToolMenuItem(CentreOriginsItem, new Callable(this, MethodName.CentreOrigins));
        AddToolMenuItem(TidyJunctionsItem, new Callable(this, MethodName.TidyAllJunctions));
        AddToolMenuItem(GroundCheckItem, new Callable(this, MethodName.FindGroundThroughRoads));
        AddToolMenuItem(RemoveJunctionsItem, new Callable(this, MethodName.RemoveSelectedJunctions));
        AddToolMenuItem(RampRoadItem, new Callable(this, MethodName.AddRoadsToSelectedRamps));
        AddToolMenuItem(ForkRoadItem, new Callable(this, MethodName.AddRoadsToSelectedForks));
        AddToolMenuItem(SwapBranchesItem, new Callable(this, MethodName.SwapSelectedForkBranches));
        // Outlines (towns, districts, plots, lots) shown only for what is selected: shown again as it changes.
        // By Callable, not C#'s += (whose hookup is lost as the scripts reload, its -= then failing).
        var outlines = new Callable(this, MethodName.OutlinesSelectionChanged);
        if (!EditorInterface.Singleton.GetSelection().IsConnected(EditorSelection.SignalName.SelectionChanged, outlines))
            EditorInterface.Singleton.GetSelection().Connect(EditorSelection.SignalName.SelectionChanged, outlines);
        _joinMenu = new RoadJoinMenu { Plugin = this };
        AddContextMenuPlugin(EditorContextMenuPlugin.ContextMenuSlot.SceneTree, _joinMenu);
        _toolbar = new HBoxContainer();
        _toolbar.AddChild(new VSeparator());
        _drawButton = new Button { Text = "Draw Roads", ToggleMode = true, Flat = true, TooltipText =
            "Click the ground to add road points, drag a point to move it, ctrl-click one to delete it; right-click or Esc finishes a road." };
        // By method name, not a lambda: the editor can then let go of them as it reloads the scripts.
        _drawButton.Connect(BaseButton.SignalName.Toggled, new Callable(this, MethodName.ShowToggled));
        Iconify(_drawButton, "Road-Draw");
        _toolbar.AddChild(_drawButton);
        _presetMenu = new OptionButton { TooltipText = "What new roads are like across. Change a road's lanes later in the Inspector." };
        foreach (var (name, _) in Presets)
            _presetMenu.AddItem(name);
        _toolbar.AddChild(_presetMenu);
        _lanesButton = new Button { Text = "Show Lanes", ToggleMode = true, Flat = true, TooltipText =
            "Show the lanes traffic drives, with arrows the way it goes: roads' lanes blue, turns through junctions orange, ramps pink." };
        _lanesButton.Connect(BaseButton.SignalName.Toggled, new Callable(this, MethodName.ShowToggled));
        Iconify(_lanesButton, "Lanes");
        _toolbar.AddChild(_lanesButton);
        _rampMenu = new OptionButton { TooltipText =
            "Pick a ramp, then click a road where the ramp's own road should start: it is built into the road on the side clicked.\n" +
            "Pick a viaduct or tunnel, then click a road where it starts and again where it ends.\n" +
            "Pick a cul-de-sac, then click a road's loose end point to turn it round there.\n" +
            "Pick a fork or merge, then click a one-way road of two lanes or more where it should part (or come together)." };
        _rampMenu.AddItem("Nothing to build");
        _rampMenu.AddItem("Exit ramp");
        _rampMenu.AddItem("Entrance ramp");
        _rampMenu.AddItem("Viaduct");
        _rampMenu.AddItem("Tunnel");
        _rampMenu.AddItem("Cul-de-sac");
        _rampMenu.AddItem("Parking: parallel lane");
        _rampMenu.AddItem("Parking: lay-by");
        _rampMenu.AddItem("Parking: angled");
        _rampMenu.AddItem("Parking: perpendicular");
        _rampMenu.AddItem("Fork (split)");
        _rampMenu.AddItem("Merge");
        _rampMenu.Connect(OptionButton.SignalName.ItemSelected, new Callable(this, MethodName.BuildPicked));
        _toolbar.AddChild(_rampMenu);
        _rampLanes = new SpinBox { MinValue = 1, MaxValue = 3, Value = 1, Suffix = "lanes", TooltipText = "How many lanes a new ramp and its road have." };
        _toolbar.AddChild(_rampLanes);
        _rampLength = new SpinBox { MinValue = 0, MaxValue = 1000, Step = 5, Value = 100, Suffix = "m", TooltipText = "How long a new ramp's lane runs at full width beside the road before it splits off." };
        _toolbar.AddChild(_rampLength);
        _toolbar.Visible = false;
        ToolButtons.StyleAll(_toolbar);
        AddControlToContainer(CustomControlContainer.SpatialEditorMenu, _toolbar);
        // Roads deleted: the junctions they met looked at again once the deletion is done.
        GetTree().Connect(SceneTree.SignalName.NodeRemoved, new Callable(this, MethodName.NodeRemoved));
        EditorInterface.Singleton.GetEditorUndoRedo().Connect(EditorUndoRedoManager.SignalName.HistoryChanged, new Callable(this, MethodName.HistoryChanged));
    }

    private const string CentreOriginsItem = "Centre Road Origins";

    private const string TidyJunctionsItem = "Tidy Junctions";
    private const string RemoveJunctionsItem = "Remove Selected Junctions";

    /// <summary>The selection changed: outlines shown only for what is selected now (see RoadOutlines).</summary>
    /// <summary>Shows one of the plugin's icons in place of a button's text.</summary>
    private static void Iconify(Button button, string icon)
    {
        button.Icon = GD.Load<Texture2D>($"res://addons/unpleasant_roads/Icons/{icon}.svg");
        button.AddThemeConstantOverride("icon_max_width", 20);
        button.TooltipText = $"{button.Text}: {button.TooltipText}";
        button.Text = "";
    }

    private void OutlinesSelectionChanged()
    {
        if (IsInsideTree())
            RoadOutlines.Refresh(GetTree());
    }

    /// <summary>The handles on a road's parking's ends (see ParkingGizmoPlugin).</summary>
    private ParkingGizmoPlugin? _parkingGizmos;

    /// <summary>The handles on a road's barrier gaps' ends (see BarrierGapGizmoPlugin).</summary>
    private BarrierGapGizmoPlugin? _gapGizmos;

    /// <summary>The scene dock's right-click Join Roads (see JoinRoads).</summary>
    private RoadJoinMenu? _joinMenu;

    /// <summary>
    /// The selected junctions taken away where only two roads alike across meet at them: the two merged
    /// into one road through where it was (as when a road is deleted; see TidyJunctions). One step to undo.
    /// Any where more roads meet, or the two are not alike, is kept, and said so in the Output.
    /// </summary>
    private void RemoveSelectedJunctions()
    {
        var picked = EditorInterface.Singleton.GetSelection().GetSelectedNodes().OfType<RoadJunction>().ToList();
        if (picked.Count == 0)
        {
            GD.PushWarning("Remove Selected Junctions: select the junctions first.");
            return;
        }

        foreach (var junction in picked.Where(j => j.Arms().Count > 2))
            GD.Print($"{junction.Name} has {junction.Arms().Count} roads meeting at it: kept it.");
        TidyJunctions(picked, keepCulDeSacs: true);
    }

    /// <summary>Every junction in the scene looked at as if a road it met had just been deleted (see <see cref="TidyJunctions"/>).</summary>
    private void TidyAllJunctions()
    {
        if (EditorInterface.Singleton.GetEditedSceneRoot() is { } root)
            TidyJunctions([.. root.FindChildren("*", "", true, false).OfType<RoadJunction>()], keepCulDeSacs: true);
    }

    /// <summary>
    /// Every road in the scene with its origin moved to its middle (the box round its
    /// points), where it is not there already: so each is picked out no bigger than it is. One step to undo.
    /// </summary>
    private void CentreOrigins()
    {
        if (EditorInterface.Singleton.GetEditedSceneRoot() is not { } root)
            return;
        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        var moved = 0;
        undo.CreateAction(CentreOriginsItem);
        foreach (var node in root.FindChildren("*", "", true, true))
        {
            switch (node)
            {
                case Road { Points.Count: > 0 } road:
                {
                    var box = new Aabb(road.Points[0].GlobalPosition, Vector3.Zero);
                    foreach (var point in road.Points)
                        box = box.Expand(point.GlobalPosition);
                    var middle = box.GetCenter();
                    if (middle.DistanceTo(road.GlobalPosition) <= 0.5f)
                        continue;
                    undo.AddDoMethod(road, Road.MethodName.MoveOrigin, middle);
                    undo.AddUndoMethod(road, Road.MethodName.MoveOrigin, road.GlobalPosition);
                    moved++;
                    break;
                }
            }
        }

        undo.CommitAction();
        GD.Print($"Moved the origins of {moved} roads to their middles.");
    }

    /// <summary>The junctions met by roads taken out of the scene since the last step was done or undone.</summary>
    private readonly HashSet<RoadJunction> _leftBehind = new();

    private void NodeRemoved(Node node)
    {
        // Only as a step is done: not as one is undone or redone.
        if (node is not Road road || EditorInterface.Singleton.GetEditedSceneRoot() is not { } root)
            return;
        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        if (!undo.GetHistoryUndoRedo(undo.GetObjectHistoryId(root)).IsCommittingAction())
            return;
        foreach (var end in new[] { road.StartJunction, road.EndJunction })
        {
            if (end is RoadJunction junction)
                _leftBehind.Add(junction);
        }
    }

    /// <summary>
    /// A step done: if it deleted roads (by the editor's own Delete, not by undoing something), the
    /// junctions they met are looked at, after it, as a step of their own.
    /// </summary>
    private void HistoryChanged()
    {
        var left = _leftBehind.ToList();
        _leftBehind.Clear();
        if (EditorInterface.Singleton.GetEditedSceneRoot() is not { } root)
            return;
        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        var history = undo.GetHistoryUndoRedo(undo.GetObjectHistoryId(root));
        if (left.Count == 0 || history.GetCurrentActionName() is not ("Remove Node(s)" or "Cut Node(s)"))
            return;
        Callable.From(() => TidyJunctions(left)).CallDeferred();
    }

    /// <summary>
    /// Junctions a deleted road met, looked at again: with no road left, gone; with one, gone and
    /// that road's end loose; with two alike across, gone and the two merged into one road. One step to undo.
    /// </summary>
    /// <remarks>With keepCulDeSacs, one with one road left is kept: a cul-de-sac, there on purpose.</remarks>
    private void TidyJunctions(List<RoadJunction> junctions, bool keepCulDeSacs = false)
    {
        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        var todo = new List<(RoadJunction Junction, IReadOnlyList<(Road Road, bool Start)> Arms, RoadMerge? Merge)>();
        // One road meeting itself there, start and end: closed into a ring through where it was.
        var rings = new List<(RoadJunction Junction, Road Road)>();
        foreach (var junction in junctions.Where(j => IsInstanceValid(j) && j.IsInsideTree()))
        {
            var arms = junction.Arms();
            if (arms.Count > 2 || (keepCulDeSacs && arms.Count == 1))
                continue;
            // Two alike across: merged, the first kept, the second carrying it on; unlike, left as they are.
            RoadMerge? merge = null;
            if (arms.Count == 2)
            {
                var ((kept, keptStart), (merged, mergedStart)) = (arms[0], arms[1]);
                if (kept == merged)
                {
                    if (keptStart != mergedStart && !kept.Closed && kept.Points.Count >= 3)
                        rings.Add((junction, kept));
                    continue;
                }
                var atEnd = !keptStart;
                var reversed = atEnd ? !mergedStart : mergedStart;
                if (!RoadEdits.CanMerge(kept, merged, reversed))
                {
                    GD.Print($"{junction.Name} has only {kept.Name} and {merged.Name} left, but they are not alike across ({kept.CrossSection} / {merged.CrossSection}): kept it.");
                    continue;
                }
                merge = new RoadMerge { Kept = kept, Merged = merged, AtEnd = atEnd, Reversed = reversed };
            }

            todo.Add((junction, arms, merge));
        }

        if (todo.Count == 0 && rings.Count == 0)
            return;
        undo.CreateAction("Tidy junctions after deleting roads");
        foreach (var (junction, road) in rings)
        {
            // Its ends loose, its last point (on top of its first, at the junction) taken out, and it closed.
            undo.AddDoProperty(road, Road.PropertyName.StartJunction, default(Variant));
            undo.AddDoProperty(road, Road.PropertyName.EndJunction, default(Variant));
            undo.AddUndoProperty(road, Road.PropertyName.StartJunction, junction);
            undo.AddUndoProperty(road, Road.PropertyName.EndJunction, junction);
            // And any other on top of its first (the two ends that met there, both left on the junction's place).
            var points = road.Points;
            var stacked = points.Skip(1).Where((p, i) => i == points.Count - 2 || p.GlobalPosition.DistanceTo(points[0].GlobalPosition) < 0.05f).ToList();
            foreach (var gone in stacked.AsEnumerable().Reverse())
            {
                undo.AddDoMethod(road, Node.MethodName.RemoveChild, gone);
                undo.AddUndoMethod(road, Node.MethodName.AddChild, gone, true);
                undo.AddUndoMethod(road, Node.MethodName.MoveChild, gone, gone.GetIndex());
                undo.AddUndoMethod(gone, Node.MethodName.SetOwner, root);
                undo.AddUndoReference(gone);
            }
            undo.AddDoProperty(road, Road.PropertyName.Closed, true);
            undo.AddUndoProperty(road, Road.PropertyName.Closed, false);
            // Its first point no longer held facing the way it left the junction: the ring's own curve turns it,
            // or the road would twist there, where it comes round to it from the other end.
            var first = road.Points[0];
            if (first.FollowRotation)
            {
                undo.AddDoProperty(first, RoadPoint.PropertyName.FollowRotation, false);
                undo.AddUndoProperty(first, RoadPoint.PropertyName.FollowRotation, true);
            }
            var parent = junction.GetParent();
            undo.AddDoMethod(parent, Node.MethodName.RemoveChild, junction);
            undo.AddUndoMethod(parent, Node.MethodName.AddChild, junction, true);
            undo.AddUndoMethod(parent, Node.MethodName.MoveChild, junction, junction.GetIndex());
            undo.AddUndoMethod(junction, Node.MethodName.SetOwner, root);
            undo.AddUndoReference(junction);
            GD.Print($"{junction.Name} joined {road.Name} to itself: closed it into a ring and removed the junction.");
        }

        foreach (var (junction, arms, merge) in todo)
        {
            // Its roads' ends there loose, and it gone.
            foreach (var (road, start) in arms)
            {
                var end = start ? Road.PropertyName.StartJunction : Road.PropertyName.EndJunction;
                undo.AddDoProperty(road, end, default(Variant));
                undo.AddUndoProperty(road, end, junction);
            }

            var parent = junction.GetParent();
            undo.AddDoMethod(parent, Node.MethodName.RemoveChild, junction);
            undo.AddUndoMethod(parent, Node.MethodName.AddChild, junction, true);
            undo.AddUndoMethod(parent, Node.MethodName.MoveChild, junction, junction.GetIndex());
            undo.AddUndoMethod(junction, Node.MethodName.SetOwner, root);
            undo.AddUndoReference(junction);
            if (merge is not null)
            {
                undo.AddDoMethod(this, MethodName.DoMerge, merge);
                undo.AddUndoMethod(this, MethodName.UndoMerge, merge);
                undo.AddDoReference(merge);
                GD.Print($"{junction.Name} had only {merge.Kept.Name} and {merge.Merged.Name} left: merged them into one road.");
            }
            else
            {
                GD.Print($"{junction.Name} had {(arms.Count == 0 ? "no road" : $"only {arms[0].Road.Name}")} left: removed it.");
            }
        }

        undo.CommitAction();
    }

    public override void _ExitTree()
    {
        if (GetTree().IsConnected(SceneTree.SignalName.NodeRemoved, new Callable(this, MethodName.NodeRemoved)))
            GetTree().Disconnect(SceneTree.SignalName.NodeRemoved, new Callable(this, MethodName.NodeRemoved));
        var history = EditorInterface.Singleton.GetEditorUndoRedo();
        if (history.IsConnected(EditorUndoRedoManager.SignalName.HistoryChanged, new Callable(this, MethodName.HistoryChanged)))
            history.Disconnect(EditorUndoRedoManager.SignalName.HistoryChanged, new Callable(this, MethodName.HistoryChanged));
        if (_gizmos is not null)
        {
            RemoveNode3DGizmoPlugin(_gizmos);
            _gizmos = null;
        }

        if (_parkingGizmos is not null)
        {
            RemoveNode3DGizmoPlugin(_parkingGizmos);
            _parkingGizmos = null;
        }

        if (_gapGizmos is not null)
        {
            RemoveNode3DGizmoPlugin(_gapGizmos);
            _gapGizmos = null;
        }

        RemoveToolMenuItem(CentreOriginsItem);
        RemoveToolMenuItem(TidyJunctionsItem);
        RemoveToolMenuItem(GroundCheckItem);
        RemoveToolMenuItem(RemoveJunctionsItem);
        RemoveToolMenuItem(RampRoadItem);
        RemoveToolMenuItem(ForkRoadItem);
        RemoveToolMenuItem(SwapBranchesItem);
        var outlines = new Callable(this, MethodName.OutlinesSelectionChanged);
        if (EditorInterface.Singleton.GetSelection().IsConnected(EditorSelection.SignalName.SelectionChanged, outlines))
            EditorInterface.Singleton.GetSelection().Disconnect(EditorSelection.SignalName.SelectionChanged, outlines);
        if (_joinMenuPopup is not null && IsInstanceValid(_joinMenuPopup))
            _joinMenuPopup.QueueFree();
        if (_joinMenu is not null)
        {
            RemoveContextMenuPlugin(_joinMenu);
            _joinMenu = null;
        }
        if (_pickGizmos is not null)
        {
            RemoveNode3DGizmoPlugin(_pickGizmos);
            _pickGizmos = null;
        }

        _signMenu?.QueueFree();
        _signMenu = null;

        if (_toolbar is null)
            return;
        RemoveControlFromContainer(CustomControlContainer.SpatialEditorMenu, _toolbar);
        _toolbar.QueueFree();
        _toolbar = null;
    }

    public override bool _Handles(GodotObject @object) =>
        @object is RoadNetwork or Road or RoadPoint or RoadJunction
        // Several roads selected at once (the editor hands them over together): for the viewport's Join Roads.
        || @object.GetClass() == "MultiNodeEdit" && SelectedRoads().Count >= 2;

    /// <summary>The roads selected in the editor: each selected road, and the road of each selected point.</summary>
    private static List<Road> SelectedRoads() =>
        [.. EditorInterface.Singleton.GetSelection().GetSelectedNodes()
            .Select(n => n as Road ?? n.GetParent() as Road).OfType<Road>().Distinct()];

    public override void _Edit(GodotObject? @object)
    {
        // A junction's stand-ins for signs not up show while it is selected.
        if (_selected is RoadJunction was && IsInstanceValid(was) && was != @object)
            was.ShowGhosts(false);
        if (@object is RoadJunction junction)
            junction.ShowGhosts(true);
        _selected = @object as Node;
        _finished = false;
        UpdateOverlays();
    }

    public override void _MakeVisible(bool visible)
    {
        if (_toolbar is not null)
            _toolbar.Visible = visible;
        if (!visible)
        {
            if (_selected is RoadJunction was && IsInstanceValid(was))
                was.ShowGhosts(false);
            _selected = null;
            UpdateOverlays();
        }
    }

    public override int _Forward3DGuiInput(Camera3D camera, InputEvent @event)
    {
        // With two or more roads selected, a right-click that does not move (the right button still
        // turns the camera) opens a menu to join them.
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right } menuClick && SelectedRoads() is { Count: >= 2 } roads)
        {
            if (menuClick.Pressed)
            {
                _menuPressedAt = menuClick.Position;
                return (int)AfterGuiInput.Pass;
            }

            var still = _menuPressedAt is { } from && from.DistanceTo(menuClick.Position) < 4f;
            _menuPressedAt = null;
            if (still)
            {
                ShowJoinMenu(roads);
                return (int)AfterGuiInput.Stop;
            }
        }

        // A selected point's height label follows it, however it is being moved.
        if ((_selected is RoadPoint || _lanesButton?.ButtonPressed == true) && @event is InputEventMouseMotion)
            UpdateOverlays();
        // A junction's light or sign clicked: what stands there.
        if (!Drawing && _selected is RoadJunction junction && IsInstanceValid(junction)
            && @event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press
            && junction.SignAlong(camera.ProjectRayOrigin(press.Position), camera.ProjectRayNormal(press.Position)) is { } sign
            && RoadJunction.SlotOf(sign) is { } slot)
        {
            ShowSignMenu(junction, slot);
            return (int)AfterGuiInput.Stop;
        }

        if (LiftJunction(camera, @event))
            return (int)AfterGuiInput.Stop;

        if (!Drawing || _selected is null || !IsInstanceValid(_selected))
            return (int)AfterGuiInput.Pass;

        switch (@event)
        {
            case InputEventMouseMotion motion:
                _mouse = motion.Position;
                _hover = Ground(camera, _mouse);
                if (_raising is not null)
                {
                    // With ctrl held, its height steps by RaiseStep on a grid of world heights.
                    var raised = _raiseBase + Vector3.Up * (AboveGround + Raise(motion.Position));
                    if (motion.CtrlPressed)
                        raised.Y = Mathf.Snapped(raised.Y, RaiseStep);
                    _raising.GlobalPosition = raised;
                    UpdateOverlays();
                    return (int)AfterGuiInput.Stop;
                }

                _hoverPoint = _dragging is null ? PointUnder(camera, _mouse) : null;
                _hoverJunction = _dragging is null && _hoverPoint is null ? JunctionUnder(camera, _mouse) : null;
                if (_dragging is not null && motion.ShiftPressed)
                {
                    // With shift held, up or down instead, as when placing one; it keeps its new height above the ground after.
                    var y = _dragBaseY + (_dragPressY - motion.Position.Y) * _metresPerPixel;
                    if (motion.CtrlPressed)
                        y = Mathf.Snapped(y, RaiseStep);
                    var at = _dragging.GlobalPosition with { Y = y };
                    _dragging.GlobalPosition = at;
                    if (_dragging.GetParent() is Road raised && raised.GroundAt(at.ToNumerics()) is { } floor)
                        _dragHeight = y - floor;
                }
                else if (_dragging is not null && _hover is { } to)
                {
                    _dragging.GlobalPosition = to + Vector3.Up * _dragHeight;
                    _dragBaseY = _dragging.GlobalPosition.Y;
                    _dragPressY = motion.Position.Y;
                }
                UpdateOverlays();
                return _dragging is not null ? (int)AfterGuiInput.Stop : (int)AfterGuiInput.Pass;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left } click:
                return LeftClick(camera, click);

            // The right button still turns the camera: only a click that does not move finishes the road.
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } rightClick:
                if (rightClick.Pressed)
                {
                    _rightPressedAt = rightClick.Position;
                }
                else if (_rightPressedAt is { } pressedAt && pressedAt.DistanceTo(rightClick.Position) < 4f)
                {
                    Finish();
                }

                if (!rightClick.Pressed)
                    _rightPressedAt = null;
                return (int)AfterGuiInput.Pass;

            case InputEventKey { Keycode: Key.Escape, Pressed: true }:
                Finish();
                return (int)AfterGuiInput.Stop;
        }

        return (int)AfterGuiInput.Pass;
    }

    /// <summary>Where the right button went down, for telling a click (the join menu) from turning the camera.</summary>
    private Vector2? _menuPressedAt;

    private PopupMenu? _joinMenuPopup;
    private List<Road> _joinMenuRoads = [];

    /// <summary>The viewport's right-click menu for the roads selected: Join Roads, at the mouse.</summary>
    private void ShowJoinMenu(List<Road> roads)
    {
        _joinMenuRoads = roads;
        if (_joinMenuPopup is null || !IsInstanceValid(_joinMenuPopup))
        {
            _joinMenuPopup = new PopupMenu();
            _joinMenuPopup.AddItem("Join Roads");
            _joinMenuPopup.IdPressed += _ => JoinRoads(_joinMenuRoads);
            EditorInterface.Singleton.GetBaseControl().AddChild(_joinMenuPopup);
        }

        _joinMenuPopup.Position = DisplayServer.MouseGetPosition();
        _joinMenuPopup.ResetSize();
        _joinMenuPopup.Popup();
    }

    private PopupMenu? _signMenu;
    private (RoadJunction Junction, string Slot)? _signMenuFor;

    /// <summary>How each choice for a sign's place reads in its menu.</summary>
    private static string SignChoiceText(string choice) => choice switch
    {
        "Auto" => "Auto (as the junction picks)",
        nameof(ArmControl.Lights) => "Traffic light",
        nameof(ArmControl.Stop) => "Stop sign",
        nameof(ArmControl.GiveWay) => "Give way sign",
        nameof(ArmControl.Priority) => "Nothing (priority)",
        RoadJunction.Hidden => "Hidden",
        _ => choice,
    };

    /// <summary>A menu at the mouse of what can stand in a junction's sign place, the current choice ticked.</summary>
    private void ShowSignMenu(RoadJunction junction, string slot)
    {
        if (_signMenu is null)
        {
            _signMenu = new PopupMenu();
            _signMenu.IdPressed += ChooseSign;
            EditorInterface.Singleton.GetBaseControl().AddChild(_signMenu);
        }

        _signMenuFor = (junction, slot);
        _signMenu.Clear();
        var current = junction.ChoiceFor(slot);
        var choices = RoadJunction.ChoicesFor(slot);
        for (var i = 0; i < choices.Count; i++)
        {
            _signMenu.AddRadioCheckItem(SignChoiceText(choices[i]), i);
            _signMenu.SetItemChecked(i, choices[i] == current);
        }

        _signMenu.ResetSize();
        _signMenu.Popup(new Rect2I(DisplayServer.MouseGetPosition(), Vector2I.Zero));
    }

    /// <summary>Puts the choice picked from the menu in the junction's sign place, as one step to undo.</summary>
    private void ChooseSign(long id)
    {
        if (_signMenuFor is not var (junction, slot) || !IsInstanceValid(junction))
            return;
        var choices = RoadJunction.ChoicesFor(slot);
        if (id < 0 || id >= choices.Count)
            return;
        var before = junction.SignChoices.Duplicate();
        var after = junction.SignChoices.Duplicate();
        if (choices[(int)id] == "Auto")
            after.Remove(slot);
        else
            after[slot] = choices[(int)id];
        var undo = GetUndoRedo();
        undo.CreateAction("Choose junction sign");
        undo.AddDoProperty(junction, RoadJunction.PropertyName.SignChoices, after);
        undo.AddUndoProperty(junction, RoadJunction.PropertyName.SignChoices, before);
        undo.CommitAction();
    }

    /// <summary>How near a road's first point, on the ground, a shift-click closes the road, in metres.</summary>
    private const float CloseReach = 10f;

    /// <summary>
    /// A shift-click near the first point of the road selected (or being drawn): the road closed into a
    /// ring, one step to undo, its last point staying where it is. Says whether it was that click.
    /// </summary>
    private bool CloseAtFirstPoint(Camera3D camera)
    {
        var road = _selected switch
        {
            Road selected => selected,
            RoadPoint { } point when point.GetParent() is Road parent => parent,
            _ => null,
        };
        if (road is null || !IsInstanceValid(road) || road.Closed || road.Points.Count == 0)
            return false;
        var first = road.Points[0];
        var near = !camera.IsPositionBehind(first.GlobalPosition)
            && camera.UnprojectPosition(first.GlobalPosition).DistanceTo(_mouse) <= PickRadius;
        if (!near && (RoadUnder(camera, _mouse)?.Hit ?? Ground(camera, _mouse)) is { } at)
            near = at.DistanceTo(first.GlobalPosition) <= CloseReach;
        if (!near)
            return false;
        if (road.Points.Count < 3 || road.StartJunction is not null || road.EndJunction is not null)
        {
            GD.PushWarning($"Close road: {road.Name} needs three points or more, and neither end on a junction.");
            return true;
        }

        var undo = GetUndoRedo();
        undo.CreateAction("Close road");
        undo.AddDoProperty(road, Road.PropertyName.Closed, true);
        undo.AddUndoProperty(road, Road.PropertyName.Closed, false);
        // Its first point no longer held facing the way it left: the ring's own curve turns it.
        if (first.FollowRotation)
        {
            undo.AddDoProperty(first, RoadPoint.PropertyName.FollowRotation, false);
            undo.AddUndoProperty(first, RoadPoint.PropertyName.FollowRotation, true);
        }

        undo.CommitAction();
        Network()?.QueueFit();
        Finish();
        return true;
    }

    /// <summary>Finishes the road being drawn: the next click starts another. Its origin goes to its middle.</summary>
    private void Finish()
    {
        _finished = true;
        var road = _selected switch
        {
            Road selected => selected,
            RoadPoint { } point when point.GetParent() is Road parent => parent,
            _ => null,
        };
        if (road is not null && IsInstanceValid(road))
            road.Centre();
        UpdateOverlays();
    }

    private int LeftClick(Camera3D camera, InputEventMouseButton click)
    {
        _mouse = click.Position;
        if (!click.Pressed)
        {
            if (_raising is not null)
            {
                FinishRaise();
                return (int)AfterGuiInput.Stop;
            }

            if (_dragging is null)
                return (int)AfterGuiInput.Pass;
            FinishDrag();
            return (int)AfterGuiInput.Stop;
        }

        // A raise whose release went elsewhere (outside the viewport) ends here.
        if (_raising is not null)
            FinishRaise();

        // The selected point's gizmo: its green arrow, raising it, runs straight up the screen from
        // it. A press there is the gizmo's, not a new point.
        if (_selected is RoadPoint chosen && IsInstanceValid(chosen) && !camera.IsPositionBehind(chosen.GlobalPosition))
        {
            var offset = _mouse - camera.UnprojectPosition(chosen.GlobalPosition);
            if (Mathf.Abs(offset.X) < GizmoArrowWidth && offset.Y < 0f && offset.Y > -GizmoArrowLength)
                return (int)AfterGuiInput.Pass;
        }

        var under = PointUnder(camera, _mouse);
        if (under is not null && click.CtrlPressed)
        {
            DeletePoint(under);
            return (int)AfterGuiInput.Stop;
        }

        if (click.ShiftPressed && !click.CtrlPressed && CloseAtFirstPoint(camera))
            return (int)AfterGuiInput.Stop;

        // Joining the road being drawn to another: at a junction, at another road's loose end,
        // or in the middle of another road.
        if (JunctionUnder(camera, _mouse) is { } junction && under is null)
        {
            Join(junction, null);
            return (int)AfterGuiInput.Stop;
        }

        // A cul-de-sac at a road's loose end.
        if ((_rampMenu?.Selected ?? 0) == CulDeSacItem && under?.GetParent() is Road ending && LooseEnd(ending, under) is { } atItsStart)
        {
            MakeCulDeSac(ending, atItsStart);
            return (int)AfterGuiInput.Stop;
        }

        var (drawing, _, _) = Target();
        if (under is not null && under.GetParent() is Road other && other != drawing && LooseEnd(other, under) is { } atStart)
        {
            // Two loose ends alike across become one road; otherwise they meet at a junction.
            if (!TryMerge(other, atStart))
                Join(null, (other, atStart));
            return (int)AfterGuiInput.Stop;
        }

        // Parking along the side of a road clicked: from one click to the next.
        if (under is null && (_rampMenu?.Selected ?? 0) is >= FirstParkingItem and < FirstParkingItem + 4 && RoadUnder(camera, _mouse) is { } parked)
        {
            PlaceParking(parked.Road, parked.Distance, parked.Hit);
            return (int)AfterGuiInput.Stop;
        }

        // A fork or merge picked while drawing, clicked on open ground: at the end of the road being drawn.
        if (under is null && (_rampMenu?.Selected ?? 0) is ForkItem or MergeItem && Target() is ({ } forkOn, var endIndex, _)
            && endIndex == forkOn.Points.Count && forkOn.Points.Count >= 2 && RoadUnder(camera, _mouse) is null && Ground(camera, _mouse) is { } forkTo)
        {
            // Held down, dragging up or down raises or lowers its new branch's far end, as for a point drawn (a viaduct, raised far enough).
            if (ForkAtEnd(forkOn, forkTo + Vector3.Up * AboveGround, (_rampMenu?.Selected ?? 0) == MergeItem) is { } far)
            {
                _raising = far;
                _raiseBase = far.GlobalPosition - Vector3.Up * AboveGround;
                _raiseFromY = _mouse.Y;
                var visible = 2f * camera.GlobalPosition.DistanceTo(_raiseBase) * Mathf.Tan(Mathf.DegToRad(camera.Fov) / 2f);
                _metresPerPixel = visible / Mathf.Max(EditorInterface.Singleton.GetEditorViewport3D().GetVisibleRect().Size.Y, 1f);
            }

            return (int)AfterGuiInput.Stop;
        }

        // A fork or merge put into a one-way road where clicked.
        if (under is null && (_rampMenu?.Selected ?? 0) is ForkItem or MergeItem && RoadUnder(camera, _mouse) is { } forked)
        {
            AddFork(forked.Road, forked.Distance, (_rampMenu?.Selected ?? 0) == MergeItem);
            return (int)AfterGuiInput.Stop;
        }

        var placingStructure = (_rampMenu?.Selected ?? 0) is 3 or 4;
        if (under is null && placingStructure && RoadUnder(camera, _mouse) is { } carried)
        {
            PlaceStructure(carried.Road, carried.Distance);
            return (int)AfterGuiInput.Stop;
        }

        var placingRamp = (_rampMenu?.Selected ?? 0) is 1 or 2 || click.ShiftPressed;
        if (under is null && placingRamp && RoadUnder(camera, _mouse) is { } ramped)
        {
            AddRamp(ramped.Road, ramped.Distance, ramped.Hit);
            _rampMenu?.Select(0); // one ramp per pick
            return (int)AfterGuiInput.Stop;
        }

        if (under is null && RoadUnder(camera, _mouse) is { } crossed)
        {
            if (crossed.Road != drawing)
            {
                JoinBySplitting(crossed.Road, crossed.Distance, crossed.At);
                return (int)AfterGuiInput.Stop;
            }

            // Crossing back over itself: joined to itself there, at a junction.
            if (JoinSelf(crossed.Road, crossed.Distance, crossed.At))
                return (int)AfterGuiInput.Stop;
        }

        // A point already selected is Godot's to move, with its gizmo's arrows (the green one raises it).
        if (under is not null && under == _selected)
            return (int)AfterGuiInput.Pass;

        if (under is not null)
        {
            _dragging = under;
            _dragFrom = under.GlobalPosition;
            _dragBaseY = _dragFrom.Y;
            _dragPressY = click.Position.Y;
            var reach = 2f * camera.GlobalPosition.DistanceTo(_dragFrom) * Mathf.Tan(Mathf.DegToRad(camera.Fov) / 2f);
            _metresPerPixel = reach / Mathf.Max(EditorInterface.Singleton.GetEditorViewport3D().GetVisibleRect().Size.Y, 1f);
            _dragHeight = under.GetParent() is Road dragged && dragged.GroundAt(_dragFrom.ToNumerics()) is { } floor
                ? Mathf.Max(_dragFrom.Y - floor, AboveGround)
                : AboveGround;
            EditorInterface.Singleton.EditNode(under);
            return (int)AfterGuiInput.Stop;
        }

        if (Ground(camera, _mouse) is { } at)
        {
            // Held down, dragging up or down raises or lowers it, by about as much as it moves on screen.
            if (AddPoint(at + Vector3.Up * AboveGround) is { } added)
            {
                _raising = added;
                _raiseBase = at;
                _raiseFromY = _mouse.Y;
                var visible = 2f * camera.GlobalPosition.DistanceTo(at) * Mathf.Tan(Mathf.DegToRad(camera.Fov) / 2f);
                _metresPerPixel = visible / Mathf.Max(EditorInterface.Singleton.GetEditorViewport3D().GetVisibleRect().Size.Y, 1f);
            }

            return (int)AfterGuiInput.Stop;
        }

        return (int)AfterGuiInput.Pass;
    }

    public override void _Forward3DDrawOverViewport(Control overlay)
    {
        if (_selected is null || !IsInstanceValid(_selected))
            return;
        var camera = EditorInterface.Singleton.GetEditorViewport3D().GetCamera3D();

        if (_lanesButton?.ButtonPressed == true)
            DrawLanes(overlay, camera);

        // The selected point's height, and how far it is from the ground, whatever moves it.
        if (_selected is RoadPoint selected && selected != _raising && selected.GetParent() is Road owner
            && !camera.IsPositionBehind(selected.GlobalPosition))
        {
            var position = selected.GlobalPosition;
            var label = $"height {position.Y:0.0} m";
            if (owner.GroundAt(position.ToNumerics()) is { } floor)
                label += $", {position.Y - floor - AboveGround:+0.0;-0.0;0.0} m from the ground";
            overlay.DrawString(ThemeDB.FallbackFont, camera.UnprojectPosition(position) + new Vector2(12, -8), label,
                HorizontalAlignment.Left, -1, 16, Colors.White);
        }

        // Which side of the selected road is its left and which its right, and whether a town lines each with buildings.
        if ((_selected as Road ?? _selected.GetParent() as Road) is { } sided && sided.Frames() is { Count: >= 2 } along)
        {
            var frame = RoadCurve.At(along, (along[0].Distance + along[^1].Distance) / 2f);
            var bands = sided.CrossSection.Bands();
            var (left, right) = sided.BuildingSides;
            foreach (var (offset, side, lined) in new[] { (bands[0].Left - 3f, "left", left), (bands[^1].Right + 3f, "right", right) })
            {
                var at = frame.Across(offset).ToGodot() + Vector3.Up;
                if (camera.IsPositionBehind(at))
                    continue;
                var label = $"{side}: {(lined ? "buildings" : "no buildings")}";
                var font = ThemeDB.FallbackFont;
                var size = font.GetStringSize(label, HorizontalAlignment.Left, -1, 14);
                var screen = camera.UnprojectPosition(at) - new Vector2(size.X / 2f, 0f);
                overlay.DrawRect(new Rect2(screen - new Vector2(5, size.Y - 2), size + new Vector2(10, 5)), new Color(0, 0, 0, 0.55f));
                overlay.DrawString(font, screen, label, HorizontalAlignment.Left, -1, 14, lined ? new Color(0.6f, 1f, 0.6f) : new Color(1f, 0.7f, 0.6f));
            }
        }

        if (!Drawing)
            return;

        foreach (var road in RoadsToShow())
        {
            var points = road.Points;
            for (var i = 0; i < points.Count; i++)
            {
                if (camera.IsPositionBehind(points[i].GlobalPosition))
                    continue;
                var at = camera.UnprojectPosition(points[i].GlobalPosition);
                var picked = points[i] == _selected || points[i] == _dragging;
                var colour = points[i] == _hoverPoint ? Colors.Orange : picked ? Colors.Yellow : Colors.White;
                overlay.DrawCircle(at, picked ? 7f : 5f, new Color(0, 0, 0, 0.6f));
                overlay.DrawCircle(at, picked ? 5f : 3.5f, colour);
            }
        }

        foreach (var junction in Network()?.Junctions ?? System.Array.Empty<RoadJunction>())
        {
            if (camera.IsPositionBehind(junction.GlobalPosition))
                continue;
            var at = camera.UnprojectPosition(junction.GlobalPosition);
            var colour = junction == _hoverJunction ? Colors.Orange : junction == _selected ? Colors.Yellow : new Color(0.5f, 0.85f, 1f);
            // A square turned on its corner: drawn turned, so nothing has to be triangulated.
            overlay.DrawSetTransform(at, Mathf.Pi / 4f);
            overlay.DrawRect(new Rect2(-8.3f, -8.3f, 16.6f, 16.6f), new Color(0, 0, 0, 0.6f));
            overlay.DrawRect(new Rect2(-6.4f, -6.4f, 12.8f, 12.8f), colour);
            overlay.DrawSetTransform(Vector2.Zero);
        }

        // How high the point being raised is above the ground, and whether that makes a viaduct.
        if (_raising is not null && IsInstanceValid(_raising) && !camera.IsPositionBehind(_raising.GlobalPosition))
        {
            var height = _raising.GlobalPosition.Y - _raiseBase.Y - AboveGround;
            var at = camera.UnprojectPosition(_raising.GlobalPosition);
            overlay.DrawLine(camera.UnprojectPosition(_raiseBase), at, new Color(1, 1, 1, 0.6f), 1.5f, true);
            var label = $"height {_raising.GlobalPosition.Y:0.0} m, {height:+0.0;-0.0;0.0} m from the ground" + (height >= ViaductHeight ? "  viaduct" : "");
            overlay.DrawString(ThemeDB.FallbackFont, at + new Vector2(12, -8), label, HorizontalAlignment.Left, -1, 16,
                height >= ViaductHeight ? new Color(1f, 0.85f, 0.4f) : Colors.White);
        }

        // Each named road's name, over its middle.
        foreach (var road in RoadsToShow())
        {
            if (string.IsNullOrWhiteSpace(road.StreetName))
                continue;
            var frames = road.Frames();
            if (frames.Count < 2)
                continue;
            var middle = RoadCurve.At(frames, frames[^1].Distance / 2f).Position.ToGodot() + Vector3.Up * 2f;
            if (camera.IsPositionBehind(middle))
                continue;
            var font = ThemeDB.FallbackFont;
            var size = font.GetStringSize(road.StreetName, HorizontalAlignment.Left, -1, 15);
            var at = camera.UnprojectPosition(middle) - new Vector2(size.X / 2f, 0f);
            overlay.DrawRect(new Rect2(at - new Vector2(6, size.Y - 2), size + new Vector2(12, 6)), new Color(0, 0, 0, 0.55f));
            overlay.DrawString(font, at, road.StreetName, HorizontalAlignment.Left, -1, 15, Colors.White);
        }

        // Where the next click would add a point, joined to the point it would follow.
        if (_raising is null && _dragging is null && _hoverPoint is null && _hover is { } hover && !camera.IsPositionBehind(hover))
        {
            var cursor = camera.UnprojectPosition(hover);
            if (!_finished && Anchor() is { } from && !camera.IsPositionBehind(from.GlobalPosition))
                overlay.DrawLine(camera.UnprojectPosition(from.GlobalPosition), cursor, new Color(1, 1, 0.4f, 0.8f), 2f, true);
            overlay.DrawArc(cursor, 6f, 0f, Mathf.Tau, 16, Colors.White, 1.5f, true);
        }
    }

    /// <summary>
    /// Draws the network's traffic lanes near the camera: each as a line down its middle with
    /// arrows the way traffic goes, coloured by what it is.
    /// </summary>
    private void DrawLanes(Control overlay, Camera3D camera)
    {
        if (Network() is not { } network)
            return;
        var eye = camera.GlobalPosition;
        foreach (var lane in network.Traffic.Lanes)
        {
            var colour = lane.Source.Contains(" turn") ? new Color(1f, 0.6f, 0.2f)
                : lane.Source.Contains(" lane ") && !lane.Source.Contains(" forward ") && !lane.Source.Contains(" backward ") ? new Color(1f, 0.4f, 0.8f)
                : new Color(0.3f, 0.75f, 1f);
            var line = new List<Vector2>();
            var along = 0f;
            var nextArrow = 10f;
            for (var i = 0; i < lane.Points.Count; i++)
            {
                var at = lane.Points[i].ToGodot() + Vector3.Up * 0.3f;
                if (i > 0)
                    along += (lane.Points[i] - lane.Points[i - 1]).Length();
                var near = at.DistanceTo(eye) < LaneViewDistance && !camera.IsPositionBehind(at);
                if (!near)
                {
                    if (line.Count > 1)
                        overlay.DrawPolyline(line.ToArray(), colour, 2f, true);
                    line.Clear();
                    continue;
                }

                var screen = camera.UnprojectPosition(at);
                line.Add(screen);
                // An arrow every so often, pointing the way traffic goes.
                if (along >= nextArrow && i + 1 < lane.Points.Count)
                {
                    nextArrow = along + 25f;
                    var ahead = camera.UnprojectPosition(lane.Points[i + 1].ToGodot() + Vector3.Up * 0.3f);
                    var way = (ahead - screen).Normalized();
                    if (way != Vector2.Zero)
                    {
                        var side = new Vector2(-way.Y, way.X);
                        overlay.DrawLine(screen, screen - way * 9f + side * 5f, colour, 2f, true);
                        overlay.DrawLine(screen, screen - way * 9f - side * 5f, colour, 2f, true);
                    }
                }
            }

            if (line.Count > 1)
                overlay.DrawPolyline(line.ToArray(), colour, 2f, true);
        }
    }

    // ------------------------------------------------------------------ changes

    /// <summary>Adds a road point there, as one step to undo; the point, if one was added.</summary>
    private RoadPoint? AddPoint(Vector3 at)
    {
        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        var point = new RoadPoint { Name = "RoadPoint" };

        var startFrom = StartFrom();
        var (road, index, newRoad) = Target();
        if (road is null)
        {
            if (Network() is not { } network)
                return null;
            road = NewRoad();
            undo.CreateAction("Add road");
            undo.AddDoMethod(network, Node.MethodName.AddChild, road, true);
            undo.AddDoMethod(road, Node.MethodName.SetOwner, root);
            undo.AddDoReference(road);
            undo.AddUndoMethod(network, Node.MethodName.RemoveChild, road);
            index = 0;
            newRoad = true;

            // From a selected junction, or past a road's end joined to one: the new road starts there, as a new arm of it.
            if (startFrom is { } from && IsInstanceValid(from))
            {
                var start = new RoadPoint { Name = "RoadPoint" };
                undo.AddDoMethod(road, Node.MethodName.AddChild, start, true);
                undo.AddDoMethod(start, Node.MethodName.SetOwner, root);
                undo.AddDoProperty(start, Node3D.PropertyName.GlobalPosition, from.GlobalPosition);
                undo.AddDoReference(start);
                undo.AddDoProperty(road, Road.PropertyName.StartJunction, from);
                undo.AddUndoMethod(from, RoadJunction.MethodName.QueueRebuild);
                index = 1;
            }
        }
        else
        {
            undo.CreateAction("Add road point");
        }

        undo.AddDoMethod(road, Node.MethodName.AddChild, point, true);
        undo.AddDoMethod(road, Node.MethodName.MoveChild, point, index);
        undo.AddDoMethod(point, Node.MethodName.SetOwner, root);
        undo.AddDoProperty(point, Node3D.PropertyName.GlobalPosition, at);
        if (!newRoad && index == 0)
        {
            var old = road.Points.Select(p => p.ToCurvePoint()).ToList();
            var grown = old.Prepend(new CurvePoint(at.ToNumerics())).ToList();
            ShiftAlong(undo, road, Length(grown, road.Closed) - Length(old, road.Closed));
        }
        undo.AddDoMethod(this, MethodName.Select, point);
        // Something picked in the build menu: built along the stretch drawn, as part of the same step.
        if (BuildsAlong && (newRoad ? startFrom is { } begun && IsInstanceValid(begun) : index == road.Points.Count))
            BuildAlong(undo, road, newRoad ? [startFrom!.GlobalPosition] : [.. road.Points.Select(p => p.GlobalPosition)], at, point, root);
        undo.AddDoReference(point);
        undo.AddUndoMethod(road, Node.MethodName.RemoveChild, point);
        undo.AddUndoMethod(this, MethodName.Select, _selected!);
        undo.CommitAction();
        _finished = false;
        if (newRoad)
            GD.Print($"Started {road.Name}: click on to add points (hold and drag up or down to raise one), right-click or Esc to finish it.");
        return point;
    }

    /// <summary>How long a road through these points is.</summary>
    private static float Length(IReadOnlyList<CurvePoint> points, bool closed) =>
        points.Count < 2 ? 0f : new RoadCurve(points, closed).Frames(1f)[^1].Distance;

    /// <summary>
    /// Moves everything built along a road (its ramps, viaducts and tunnels) on by this far, as part
    /// of an undo step: for when the road grows or shrinks before them, at its front.
    /// </summary>
    private static void ShiftAlong(EditorUndoRedoManager undo, Road road, float by)
    {
        if (Mathf.Abs(by) < 0.01f)
            return;
        foreach (var ramp in road.Ramps)
        {
            undo.AddDoProperty(ramp, RoadRamp.PropertyName.Start, ramp.Start + by);
            undo.AddUndoProperty(ramp, RoadRamp.PropertyName.Start, ramp.Start);
        }

        foreach (var structure in road.Structures)
        {
            undo.AddDoProperty(structure, RoadStructure.PropertyName.Start, structure.Start + by);
            undo.AddUndoProperty(structure, RoadStructure.PropertyName.Start, structure.Start);
        }
    }

    /// <summary>
    /// The selected junction, shift-dragged up or down the screen: raised or lowered, by about as
    /// much as the mouse moves (in steps of RaiseStep with ctrl held), its roads' points near it
    /// coming with it; one step to undo. Whether the event was its.
    /// </summary>
    private bool LiftJunction(Camera3D camera, InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, ShiftPressed: true } press
                when _liftingJunction is null && _selected is RoadJunction selected && IsInstanceValid(selected) && JunctionUnder(camera, press.Position) == selected:
                _liftingJunction = selected;
                _liftFrom = selected.GlobalPosition;
                _liftPressY = press.Position.Y;
                var reach = 2f * camera.GlobalPosition.DistanceTo(_liftFrom) * Mathf.Tan(Mathf.DegToRad(camera.Fov) / 2f);
                _metresPerPixel = reach / Mathf.Max(EditorInterface.Singleton.GetEditorViewport3D().GetVisibleRect().Size.Y, 1f);
                return true;
            case InputEventMouseMotion motion when _liftingJunction is { } lifting && IsInstanceValid(lifting):
                var y = _liftFrom.Y + (_liftPressY - motion.Position.Y) * _metresPerPixel;
                if (motion.CtrlPressed)
                    y = Mathf.Snapped(y, RaiseStep);
                lifting.GlobalPosition = _liftFrom with { Y = y };
                UpdateOverlays();
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _liftingJunction is { } lifted:
                _liftingJunction = null;
                if (!IsInstanceValid(lifted) || lifted.GlobalPosition.IsEqualApprox(_liftFrom))
                    return true;
                // Already where it was lifted to: recorded so undo takes it back (its roads' points coming back with it).
                var undo = EditorInterface.Singleton.GetEditorUndoRedo();
                undo.CreateAction(lifted.GlobalPosition.Y > _liftFrom.Y ? "Raise junction" : "Lower junction");
                undo.AddDoProperty(lifted, Node3D.PropertyName.GlobalPosition, lifted.GlobalPosition);
                undo.AddUndoProperty(lifted, Node3D.PropertyName.GlobalPosition, _liftFrom);
                undo.CommitAction(false);
                Network()?.QueueFit();
                return true;
        }

        return false;
    }

    /// <summary>How far the point being raised is above where it was placed, by how far the mouse has moved up from where it pressed.</summary>
    private float Raise(Vector2 mouse) => (_raiseFromY - mouse.Y) * _metresPerPixel;

    /// <summary>
    /// Done raising a point just placed: its height is one step to undo, and if it is high enough
    /// above the ground, the road from the point before up to it becomes a viaduct (or the viaduct
    /// ending at the point before reaches on to it).
    /// </summary>
    private void FinishRaise()
    {
        var point = _raising;
        _raising = null;
        UpdateOverlays();
        if (point is null || !IsInstanceValid(point) || point.GetParent() is not Road road)
            return;
        var placed = _raiseBase + Vector3.Up * AboveGround;
        var raised = point.GlobalPosition;
        if (Mathf.Abs(raised.Y - placed.Y) < 0.05f)
        {
            point.GlobalPosition = placed;
            Network()?.QueueFit();
            return;
        }

        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        undo.CreateAction(raised.Y > placed.Y ? "Raise road point" : "Lower road point");
        undo.AddDoProperty(point, Node3D.PropertyName.GlobalPosition, raised);
        undo.AddUndoProperty(point, Node3D.PropertyName.GlobalPosition, placed);

        var points = road.Points;
        var index = points.ToList().IndexOf(point);
        // At the front, raising it changes how long the road is before everything built along it.
        var shift = 0f;
        if (index == 0 && points.Count > 1)
        {
            var now = points.Select(p => p.ToCurvePoint()).ToList();
            var was = now.ToList();
            was[0] = was[0] with { Position = placed.ToNumerics() };
            shift = Length(now, road.Closed) - Length(was, road.Closed);
            ShiftAlong(undo, road, shift);
        }

        var neighbour = index > 0 ? index - 1 : index + 1;
        if (raised.Y - placed.Y >= ViaductHeight && neighbour < points.Count)
        {
            var frames = road.FullFrames();
            var from = RoadEdits.Along(frames, points[neighbour].GlobalPosition);
            var to = RoadEdits.Along(frames, raised);
            (from, to) = (Mathf.Min(from, to), Mathf.Max(from, to));
            // A viaduct already reaching the other end of this part carries on over it.
            var ending = road.Structures.FirstOrDefault(s => s.Kind == StructureKind.Viaduct && Mathf.Abs(s.Start + shift + s.Length - from) < 2f);
            var starting = road.Structures.FirstOrDefault(s => s.Kind == StructureKind.Viaduct && Mathf.Abs(s.Start + shift - to) < 2f);
            if (ending is not null)
            {
                undo.AddDoProperty(ending, RoadStructure.PropertyName.Length, to - (ending.Start + shift));
                undo.AddUndoProperty(ending, RoadStructure.PropertyName.Length, ending.Length);
            }
            else if (starting is not null)
            {
                undo.AddDoProperty(starting, RoadStructure.PropertyName.Start, from);
                undo.AddDoProperty(starting, RoadStructure.PropertyName.Length, starting.Start + shift + starting.Length - from);
                undo.AddUndoProperty(starting, RoadStructure.PropertyName.Length, starting.Length);
                undo.AddUndoProperty(starting, RoadStructure.PropertyName.Start, starting.Start);
            }
            else
            {
                var viaduct = new RoadStructure { Name = "Viaduct", Kind = StructureKind.Viaduct, Start = from, Length = to - from };
                undo.AddDoMethod(road, Node.MethodName.AddChild, viaduct, true);
                undo.AddDoMethod(viaduct, Node.MethodName.SetOwner, root);
                undo.AddDoReference(viaduct);
                undo.AddUndoMethod(road, Node.MethodName.RemoveChild, viaduct);
            }
        }

        undo.CommitAction();
        // The ground fitted to the new point, but not under a viaduct it raised.
        if (raised.Y - placed.Y < ViaductHeight)
            Network()?.QueueFit();
    }

    private void FinishDrag()
    {
        if (_dragging is not { } point)
            return;
        _dragging = null;
        var to = point.GlobalPosition;
        if (to.IsEqualApprox(_dragFrom))
            return;

        // The point is already where it was dragged to: record the move so undo can take it back.
        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        undo.CreateAction("Move road point");
        undo.AddDoProperty(point, Node3D.PropertyName.GlobalPosition, to);
        undo.AddUndoProperty(point, Node3D.PropertyName.GlobalPosition, _dragFrom);
        undo.CommitAction(false);
        (point.GetParent() as Road)?.Centre();
    }

    private void DeletePoint(RoadPoint point)
    {
        if (point.GetParent() is not Road road)
            return;
        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        undo.CreateAction("Delete road point");
        undo.AddDoMethod(road, Node.MethodName.RemoveChild, point);
        undo.AddDoMethod(this, MethodName.Select, road);
        undo.AddUndoMethod(road, Node.MethodName.AddChild, point, true);
        undo.AddUndoMethod(road, Node.MethodName.MoveChild, point, point.GetIndex());
        undo.AddUndoMethod(point, Node.MethodName.SetOwner, root);
        undo.AddUndoReference(point);
        undo.CommitAction();
        road.Centre();
    }

    /// <summary>
    /// Joins the road being drawn to a junction: an existing one, or a new one at another road's
    /// loose end. The road ends there and is finished; a new road starts there instead.
    /// </summary>
    private void Join(RoadJunction? existing, (Road Road, bool AtStart)? end)
    {
        if (Network() is not { } network)
            return;

        // Already there: the road being drawn ends on this junction, so it is finished rather than
        // given another point on top of that one.
        if (existing is not null && Target() is ({ } drawing, var index, _) && index > 0 && index <= drawing.Points.Count
            && drawing.Points[index - 1].GlobalPosition.DistanceTo(existing.GlobalPosition) < 0.01f)
        {
            _finished = true;
            UpdateOverlays();
            return;
        }

        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        undo.CreateAction("Join roads at a junction");

        var junction = existing;
        if (junction is null && end is { } loose)
        {
            var points = loose.Road.Points;
            var at = (loose.AtStart ? points[0] : points[^1]).GlobalPosition;
            junction = NewJunction(undo, network, root, at);
            var frames = loose.Road.FullFrames();
            junction.Viaduct = frames.Count > 1 && OnViaduct(loose.Road, loose.AtStart ? 0f : frames[^1].Distance);
            undo.AddDoProperty(loose.Road, loose.AtStart ? Road.PropertyName.StartJunction : Road.PropertyName.EndJunction, junction);
            undo.AddUndoProperty(loose.Road, loose.AtStart ? Road.PropertyName.StartJunction : Road.PropertyName.EndJunction, default(Variant));
        }

        if (junction is null)
            return;
        AttachDrawing(undo, network, root, junction);
        undo.CommitAction();
    }

    /// <summary>Whether a viaduct carries a road at this distance along it, so a junction there is one too.</summary>
    private static bool OnViaduct(Road road, float distance) =>
        road.StructureSpecs().Any(s => s.Kind == StructureKind.Viaduct && s.Covers(distance));

    /// <summary>Joins the road being drawn to another road in the middle, splitting that road at a new junction there.</summary>
    private void JoinBySplitting(Road road, float distance, Vector3 at)
    {
        if (Network() is not { } network)
            return;
        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        undo.CreateAction("Join roads, splitting one at a junction");
        var junction = NewJunction(undo, network, root, at);
        junction.Viaduct = OnViaduct(road, distance);
        var split = new RoadSplit { Road = road, Junction = junction, Distance = distance };
        // Undo runs its steps backwards: the split is undone before the junction goes.
        undo.AddDoMethod(this, MethodName.DoSplit, split);
        undo.AddUndoMethod(this, MethodName.UndoSplit, split);
        undo.AddDoReference(split);
        AttachDrawing(undo, network, root, junction);
        undo.CommitAction();
    }

    /// <summary>The placement menu's cul-de-sac entry.</summary>
    private const int CulDeSacItem = 5;

    /// <summary>Turns a road round at its loose end, in a cul-de-sac: a junction with it alone.</summary>
    private void MakeCulDeSac(Road road, bool atStart)
    {
        if (Network() is not { } network)
            return;
        var points = road.Points;
        var at = (atStart ? points[0] : points[^1]).GlobalPosition;
        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        undo.CreateAction("Add cul-de-sac");
        var junction = NewJunction(undo, network, root, at);
        junction.Name = "CulDeSac";
        var frames = road.FullFrames();
        junction.Viaduct = frames.Count > 1 && OnViaduct(road, atStart ? 0f : frames[^1].Distance);
        var end = atStart ? Road.PropertyName.StartJunction : Road.PropertyName.EndJunction;
        undo.AddDoProperty(road, end, junction);
        undo.AddUndoProperty(road, end, default(Variant));
        undo.AddDoMethod(this, MethodName.Select, junction);
        undo.AddUndoMethod(this, MethodName.Select, _selected!);
        undo.CommitAction();
        _rampMenu?.Select(0); // one per pick
        _finished = true;
        GD.Print($"Added a cul-de-sac at the {(atStart ? "start" : "end")} of {road.Name}: set its size with its Turning Radius.");
    }

    /// <summary>How far from the end being drawn a road can be joined back onto itself.</summary>
    private const float SelfJoinReach = 20f;

    /// <summary>
    /// Joins the road being drawn back onto itself further along it: split there at a new
    /// junction, with the end being drawn brought round to the junction too, closing the loop.
    /// Only from a loose end, and not right by it (where a click is only carrying the road on).
    /// </summary>
    private bool JoinSelf(Road road, float distance, Vector3 at)
    {
        var (_, index, _) = Target();
        var frames = road.FullFrames();
        if (road.Closed || frames.Count < 2 || Network() is not { } network)
            return false;
        var atEnd = index >= road.Points.Count;
        var atStart = index == 0;
        if (!atEnd && !atStart)
            return false;
        if ((atEnd ? road.EndJunction : road.StartJunction) is not null)
            return false;
        var drawnEnd = atEnd ? frames[^1].Distance : 0f;
        if (Mathf.Abs(distance - drawnEnd) < SelfJoinReach)
            return false;

        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        undo.CreateAction("Join a road back onto itself at a junction");
        var junction = NewJunction(undo, network, root, at);
        junction.Viaduct = OnViaduct(road, distance);
        var split = new RoadSplit { Road = road, Junction = junction, Distance = distance, ClosesEnd = atEnd, ClosesStart = !atEnd };
        undo.AddDoMethod(this, MethodName.DoSplit, split);
        undo.AddUndoMethod(this, MethodName.UndoSplit, split);
        undo.AddDoReference(split);
        undo.AddDoMethod(this, MethodName.Select, junction);
        undo.AddUndoMethod(this, MethodName.Select, _selected!);
        undo.CommitAction();
        _finished = true;
        return true;
    }

    /// <summary>
    /// The first click on a road marks where a viaduct or tunnel starts; the second, on the same
    /// road, where it ends, and builds it between them.
    /// </summary>
    /// <summary>The placement menu's first parking entry: parallel lane, lay-by, angled, perpendicular after it, in ParkingKind's order.</summary>
    private const int FirstParkingItem = 6;

    /// <summary>The placement menu's fork and merge entries.</summary>
    private const int ForkItem = 10;

    private const int MergeItem = 11;

    /// <summary>How long a new fork (or merge) is, and how far its new road runs on past it.</summary>
    private const float ForkLength = 10f;

    private const float BranchOn = 30f;

    /// <summary>
    /// A one-way road of two lanes or more parted where clicked (see RoadFork): cut there, the part before it the
    /// trunk and the part after it the branch going straight on, with a new road of one lane for the branch
    /// easing away to the right, started where the fork ends. A merge alike, the other way: the part before it
    /// the straight branch, the new road coming in beside it, the part after it the trunk. The straight branch's
    /// points over the fork's length are taken out of its way. One step to undo.
    /// </summary>
    private void AddFork(Road road, float distance, bool merge)
    {
        if (Network() is not { } network)
            return;
        var lanes = road.ForwardLanes + road.BackwardLanes;
        var what = merge ? "Merge" : "Fork";
        if (road.ForwardLanes > 0 && road.BackwardLanes > 0)
        {
            GD.PushWarning($"{what}: {road.Name} has lanes both ways; it has to be one way.");
            return;
        }

        if (lanes < 2)
        {
            GD.PushWarning($"{what}: {road.Name} has only one lane; it needs two or more to part.");
            return;
        }

        // One way the other way (its lanes backward): turned round first, as part of the same step, so it runs the way its traffic goes.
        var turn = road.ForwardLanes == 0;
        if (turn && road.Ramps.Count > 0)
        {
            GD.PushWarning($"{what}: {road.Name}'s lanes run against the way it was drawn, and it has ramps, so it can't be turned round to fit. Turn it round by hand first.");
            return;
        }

        var frames = road.FullFrames();
        if (turn && frames.Count >= 2)
        {
            // Measured from its other end, as it will run.
            distance = frames[^1].Distance - distance;
            frames = frames.Select(f => f with { Forward = -f.Forward, Right = -f.Right }).Reverse()
                .Select(f => f with { Distance = frames[^1].Distance - f.Distance }).ToList();
        }

        var room = merge ? distance : frames[^1].Distance - distance;
        if (frames.Count < 2 || distance <= 1f || room < ForkLength + 5f)
        {
            GD.PushWarning($"{(merge ? "Merge" : "Fork")}: it needs {ForkLength + 5f:0} m of {road.Name} {(merge ? "before" : "past")} where it is clicked.");
            return;
        }

        var frame = RoadCurve.At(frames, distance);
        var at = frame.Position.ToGodot();
        var forward = (frame.Forward.ToGodot() with { Y = 0f }).Normalized();
        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        undo.CreateAction(merge ? "Add merge" : "Add fork");
        if (turn)
        {
            // Its own undo: turning it round again.
            undo.AddDoMethod(this, MethodName.TurnRound, road);
            undo.AddUndoMethod(this, MethodName.TurnRound, road);
        }

        var fork = new RoadFork { Name = merge ? "Merge" : "Fork", LeftLanes = lanes - 1, Length = ForkLength };
        undo.AddDoMethod(network, Node.MethodName.AddChild, fork, true);
        undo.AddDoMethod(fork, Node.MethodName.SetOwner, root);
        undo.AddDoProperty(fork, Node3D.PropertyName.GlobalPosition, at);
        undo.AddDoReference(fork);
        undo.AddUndoMethod(network, Node.MethodName.RemoveChild, fork);
        var make = new ForkMake { Split = new RoadSplit { Road = road, Junction = fork, Distance = distance }, Merge = merge, At = at, Length = ForkLength };
        undo.AddDoMethod(this, MethodName.DoFork, make);
        undo.AddUndoMethod(this, MethodName.UndoFork, make);
        undo.AddDoReference(make);
        undo.AddDoReference(make.Split);

        // The new branch, easing away to the right: its end at the fork (the fork puts it in place) and a point on past it.
        var branch = NewRoad();
        branch.Name = merge ? "MergeRoad" : "ForkRoad";
        branch.ForwardLanes = 1;
        branch.BackwardLanes = 0;
        branch.LaneWidth = road.LaneWidth;
        branch.ShoulderWidth = road.ShoulderWidth;
        branch.ShoulderHeight = road.ShoulderHeight;
        branch.ShoulderAngle = road.ShoulderAngle;
        // Made like the road it comes off (once: free to change after).
        RoadFork.Adopt(branch, road);
        var right = new Vector3(-forward.Z, 0f, forward.X);
        var aside = right * (lanes * road.LaneWidth / 2f + 2f * road.ShoulderWidth + 1f);
        var near = merge ? at - forward * ForkLength + aside : at + forward * ForkLength + aside;
        var far = merge ? near - forward * BranchOn : near + forward * BranchOn;
        var points = merge ? new[] { far, near } : new[] { near, far };
        undo.AddDoMethod(network, Node.MethodName.AddChild, branch, true);
        undo.AddDoMethod(branch, Node.MethodName.SetOwner, root);
        undo.AddDoReference(branch);
        RoadPoint? free = null;
        foreach (var place in points)
        {
            var point = new RoadPoint { Name = "RoadPoint" };
            undo.AddDoMethod(branch, Node.MethodName.AddChild, point, true);
            undo.AddDoMethod(point, Node.MethodName.SetOwner, root);
            undo.AddDoProperty(point, Node3D.PropertyName.GlobalPosition, place);
            undo.AddDoReference(point);
            if (place == far)
                free = point;
        }

        undo.AddDoProperty(branch, merge ? Road.PropertyName.EndJunction : Road.PropertyName.StartJunction, fork);
        undo.AddUndoMethod(network, Node.MethodName.RemoveChild, branch);
        if (free is not null)
            undo.AddDoMethod(this, MethodName.Select, free);
        undo.AddUndoMethod(this, MethodName.Select, _selected!);
        undo.CommitAction();
        _rampMenu?.Select(0); // one per pick
        _finished = false;
        // The ground fitted to it, as to a road drawn (if the network fits as you draw), once it has built.
        network.QueueFit();
        GD.Print($"Added a {(merge ? "merge" : "fork")} to {road.Name}: its new road's far end is selected, to drag or draw on. "
                 + "Set its Length, Left Lanes and which way it parts on the fork; move the fork to slide it along the road.");
    }

    /// <summary>
    /// A road turned round, to run from its end to its start: its points the other way about (those it follows the
    /// turn of, turned round too), its lanes each way swapped, its ends' junctions and direction signs swapped, and
    /// what is built along it (structures, parking, barrier gaps) measured from its other end, on the other side.
    /// Its own undo (turning it round again). Not for a road with ramps.
    /// </summary>
    public void TurnRound(Road road)
    {
        var total = road.FullFrames() is { Count: >= 2 } frames ? frames[^1].Distance : 0f;
        var points = road.Points.ToList();
        foreach (var point in points.AsEnumerable().Reverse())
            road.MoveChild(point, road.GetChildCount() - 1);
        foreach (var point in points.Where(p => p.FollowRotation))
            point.GlobalBasis = point.GlobalBasis.Rotated(Vector3.Up, Mathf.Pi);
        (road.ForwardLanes, road.BackwardLanes) = (road.BackwardLanes, road.ForwardLanes);
        var (start, end) = (road.StartJunction, road.EndJunction);
        road.StartJunction = end;
        road.EndJunction = start;
        (road.DirectionSignAtStart, road.DirectionSignAtEnd) = (road.DirectionSignAtEnd, road.DirectionSignAtStart);
        (road.DirectionSignDistanceAtStart, road.DirectionSignDistanceAtEnd) = (road.DirectionSignDistanceAtEnd, road.DirectionSignDistanceAtStart);
        (road.DirectionTownsAtStart, road.DirectionTownsAtEnd) = (road.DirectionTownsAtEnd, road.DirectionTownsAtStart);
        foreach (var structure in road.Structures)
            structure.Start = Mathf.Max(total - structure.Start - structure.Length, 0f);
        foreach (var parking in road.Parking)
        {
            parking.Start = Mathf.Max(total - parking.Start - parking.Length, 0f);
            parking.Side = parking.Side == RoadSide.Left ? RoadSide.Right : RoadSide.Left;
        }

        foreach (var gap in road.BarrierGaps)
        {
            gap.Start = Mathf.Max(total - gap.Start - gap.Length, 0f);
            gap.Side = gap.Side switch { BarrierSide.Left => BarrierSide.Right, BarrierSide.Right => BarrierSide.Left, _ => gap.Side };
        }

        road.QueueRebuild();
    }

    /// <summary>A fork's road split, and the straight branch's points over its length taken out (kept to put back).</summary>
    public void DoFork(ForkMake make)
    {
        RoadEdits.Split(make.Split);
        var straight = make.Merge ? make.Split.Road : make.Split.Second;
        if (straight is null)
            return;
        make.Straight = straight;
        // A fork's straight branch, new from the split, made like the trunk in all else too.
        if (!make.Merge)
            RoadFork.Adopt(straight, make.Split.Road);
        make.Dropped.Clear();
        var points = straight.Points.ToList();
        // Not its end at the fork; nor so many that fewer than two are left.
        var nearEnd = make.Merge ? points[^1] : points[0];
        foreach (var point in points.Where(p => p != nearEnd && p.GlobalPosition.DistanceTo(make.At) < make.Length + 5f).ToList())
        {
            if (straight.Points.Count <= 2)
                break;
            make.Dropped.Add((point, point.GlobalPosition, point.GetIndex()));
            straight.RemoveChild(point);
        }
    }

    public void UndoFork(ForkMake make)
    {
        if (make.Straight is { } straight)
        {
            var root = EditorInterface.Singleton.GetEditedSceneRoot();
            foreach (var (point, at, index) in make.Dropped.AsEnumerable().Reverse())
            {
                straight.AddChild(point, true);
                straight.MoveChild(point, Mathf.Min(index, straight.GetChildCount() - 1));
                point.Owner = root;
                point.GlobalPosition = at;
            }
        }

        make.Dropped.Clear();
        RoadEdits.Unsplit(make.Split);
    }

    /// <summary>Where parking being placed starts: its road, how far along, and on which side.</summary>
    private (Road Road, float Distance, RoadSide Side)? _parkingStart;

    /// <summary>
    /// Parking along a road, on the side clicked: the first click its start, the next (on the same
    /// road) its end. Its kind as picked in the placement menu.
    /// </summary>
    private void PlaceParking(Road road, float distance, Vector3 hit)
    {
        var kind = (ParkingKind)((_rampMenu?.Selected ?? FirstParkingItem) - FirstParkingItem);
        var frames = road.FullFrames();
        if (frames.Count < 2)
            return;
        var frame = RoadCurve.At(frames, distance);
        var side = (hit.ToNumerics() - frame.Position) is var off && System.Numerics.Vector3.Dot(off, frame.Right) >= 0f ? RoadSide.Right : RoadSide.Left;
        if (_parkingStart is not { } start || start.Road != road || !IsInstanceValid(start.Road))
        {
            _parkingStart = (road, distance, side);
            GD.Print($"Parking starts {distance:0} m along {road.Name}, on its {side.ToString().ToLowerInvariant()}: click the road again where it ends.");
            return;
        }

        _parkingStart = null;
        var (from, to) = (Mathf.Min(start.Distance, distance), Mathf.Max(start.Distance, distance));
        if (to - from < 5f)
        {
            GD.Print("Parking too short: click further along the road for its end.");
            return;
        }

        var parking = new RoadParking { Name = "Parking", Kind = kind, Side = start.Side, Start = from, Length = to - from };
        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        undo.CreateAction("Add parking");
        undo.AddDoMethod(road, Node.MethodName.AddChild, parking, true);
        undo.AddDoMethod(parking, Node.MethodName.SetOwner, root);
        undo.AddDoReference(parking);
        undo.AddUndoMethod(road, Node.MethodName.RemoveChild, parking);
        undo.AddDoMethod(this, MethodName.Select, parking);
        undo.AddUndoMethod(this, MethodName.Select, _selected!);
        undo.CommitAction();
        _rampMenu?.Select(0); // one per pick
        GD.Print($"Added {kind} parking to {road.Name}'s {start.Side.ToString().ToLowerInvariant()}, {from:0} m to {to:0} m along it: {parking.Bays().Count} bays.");
    }

    private void PlaceStructure(Road road, float distance)
    {
        var kind = _rampMenu?.Selected == 4 ? StructureKind.Tunnel : StructureKind.Viaduct;
        if (_structureStart is not { } start || start.Road != road || !IsInstanceValid(start.Road))
        {
            _structureStart = (road, distance);
            GD.Print($"{kind} starts {distance:0} m along {road.Name}: click the road again where it ends.");
            return;
        }

        _structureStart = null;
        var (from, to) = (Mathf.Min(start.Distance, distance), Mathf.Max(start.Distance, distance));
        if (to - from < 5f)
        {
            GD.Print($"{kind} too short: click further along the road for its end.");
            return;
        }

        var structure = new RoadStructure { Name = kind.ToString(), Kind = kind, Start = from, Length = to - from };
        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        undo.CreateAction($"Add {kind.ToString().ToLowerInvariant()}");
        undo.AddDoMethod(road, Node.MethodName.AddChild, structure, true);
        undo.AddDoMethod(structure, Node.MethodName.SetOwner, root);
        undo.AddDoReference(structure);
        undo.AddUndoMethod(road, Node.MethodName.RemoveChild, structure);
        undo.AddDoMethod(this, MethodName.Select, structure);
        undo.AddUndoMethod(this, MethodName.Select, _selected!);
        undo.CommitAction();
        _rampMenu?.Select(0); // one per pick
        GD.Print($"Added a {kind.ToString().ToLowerInvariant()} to {road.Name}, {from:0} m to {to:0} m along it.");
    }

    /// <summary>
    /// A ramp's own road started (in an undo action being made): its first point at the ramp's far end, facing
    /// on from it; going on from it for an exit, coming in to it for an entrance. That point selected after.
    /// </summary>
    private void RampRoad(EditorUndoRedoManager undo, RoadNetwork network, Road road, RoadRamp ramp, RoadFrame end, Node root)
    {
        var branch = NewRoad();
        var section = ramp.Spec.Section;
        branch.ForwardLanes = section.ForwardLanes;
        branch.BackwardLanes = 0;
        branch.LaneWidth = section.LaneWidth;
        branch.ShoulderWidth = section.ShoulderWidth;
        branch.ShoulderHeight = road.ShoulderHeight;
        branch.ShoulderAngle = road.ShoulderAngle;
        branch.Name = ramp.Name + "Road";
        // Railed as the road it comes off is (a one-lane road would have none of its own accord).
        if (road.HasGuardRails)
            branch.GuardRails = BarrierMode.On;
        var point = new RoadPoint { Name = "RoadPoint", FollowRotation = true };
        undo.AddDoMethod(network, Node.MethodName.AddChild, branch, true);
        undo.AddDoMethod(branch, Node.MethodName.SetOwner, root);
        undo.AddDoMethod(branch, Node.MethodName.AddChild, point, true);
        undo.AddDoMethod(point, Node.MethodName.SetOwner, root);
        undo.AddDoProperty(point, Node3D.PropertyName.GlobalTransform,
            new Transform3D(Basis.LookingAt(end.Forward.ToGodot(), Vector3.Up), end.Position.ToGodot()));
        undo.AddDoProperty(branch, ramp.Kind == RampKind.Exit ? Road.PropertyName.StartJunction : Road.PropertyName.EndJunction, ramp);
        undo.AddDoReference(branch);
        undo.AddDoReference(point);
        undo.AddUndoMethod(network, Node.MethodName.RemoveChild, branch);
        undo.AddDoMethod(this, MethodName.Select, point);
    }

    private const string RampRoadItem = "Add Road to Selected Ramp";

    /// <summary>
    /// A road for each selected ramp that has none (its road deleted): started at its far end as when the ramp
    /// was added, to draw on from there. One step to undo.
    /// </summary>
    private void AddRoadsToSelectedRamps()
    {
        var ramps = EditorInterface.Singleton.GetSelection().GetSelectedNodes().OfType<RoadRamp>().ToList();
        if (ramps.Count == 0)
        {
            GD.PushWarning($"{RampRoadItem}: select the ramp first.");
            return;
        }

        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        undo.CreateAction(RampRoadItem);
        var added = 0;
        foreach (var ramp in ramps)
        {
            if (ramp.Road is not { } road || (road.Network ?? Network()) is not { } network)
                continue;
            if (network.Roads.Any(r => r.StartJunction == ramp || r.EndJunction == ramp))
            {
                GD.Print($"{ramp.Name} already has its road: left as it is.");
                continue;
            }

            RampRoad(undo, network, road, ramp, RoadRampShaper.BranchEnd(road.FullFrames(), ramp.Spec, road.CrossSection), root);
            added++;
        }

        if (_selected is { } selected)
            undo.AddUndoMethod(this, MethodName.Select, selected);
        undo.CommitAction();
        if (added > 0)
        {
            _finished = false;
            GD.Print($"{RampRoadItem}: started {added} road{(added == 1 ? "" : "s")}: click on to draw it, right-click or Esc to finish.");
        }
    }

    /// <summary>
    /// Builds a ramp into a road on the side of it clicked, its far end (where its own road meets
    /// it) at the click, and starts that road there: going on from it for an exit, coming in to it
    /// for an entrance, so clicks after draw it on outwards either way.
    /// </summary>
    private void AddRamp(Road road, float distance, Vector3 hit)
    {
        var frames = road.FullFrames();
        var frame = RoadCurve.At(frames, distance);
        var side = (hit - frame.Position.ToGodot()).Dot(frame.Right.ToGodot()) >= 0f ? RoadSide.Right : RoadSide.Left;
        var kind = _rampMenu?.Selected == 2 ? RampKind.Entrance : RampKind.Exit;
        var ramp = new RoadRamp
        {
            Name = kind == RampKind.Exit ? "Exit" : "Entrance", Kind = kind, Side = side,
            ShoulderHeight = road.ShoulderHeight, ShoulderAngle = road.ShoulderAngle,
            Lanes = (int)(_rampLanes?.Value ?? 1),
        };
        // Its taper, then the exit lane alongside, then the split.
        ramp.ExitLaneLength = (float)(_rampLength?.Value ?? 100);
        ramp.Length = ramp.Taper + ramp.ExitLaneLength + DivergeLength;
        var separatedAtStart = RoadRampShaper.SeparatedAtStart(ramp.Spec, road.CrossSection);
        var start = separatedAtStart ? distance : distance - ramp.Length;
        ramp.Start = Mathf.Clamp(start, 0f, Mathf.Max(frames[^1].Distance - ramp.Length, 0f));
        var end = RoadRampShaper.BranchEnd(frames, ramp.Spec, road.CrossSection);

        var network = road.Network ?? Network();
        if (network is null)
            return;
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        undo.CreateAction(kind == RampKind.Exit ? "Add exit ramp" : "Add entrance ramp");
        undo.AddDoMethod(road, Node.MethodName.AddChild, ramp, true);
        undo.AddDoMethod(ramp, Node.MethodName.SetOwner, root);
        undo.AddDoReference(ramp);
        undo.AddUndoMethod(road, Node.MethodName.RemoveChild, ramp);

        RampRoad(undo, network, road, ramp, end, root);
        undo.AddUndoMethod(this, MethodName.Select, _selected!);
        undo.CommitAction();
        _finished = false;
        GD.Print($"Added {ramp.Name} to {road.Name}: click on to draw its road, right-click or Esc to finish it.");
    }

    /// <summary>
    /// Merges another road onto the loose end of the road being drawn, where that end is loose and
    /// the two are alike across (the other turned round if it runs the other way); whether it did.
    /// </summary>
    private bool TryMerge(Road other, bool otherAtStart)
    {
        var (road, index, _) = Target();
        if (road is null || road.Points.Count == 0)
            return false;
        var atEnd = index >= road.Points.Count;
        if (!atEnd && index != 0)
            return false;
        if ((atEnd ? road.EndJunction : road.StartJunction) is not null)
            return false;
        // Carrying on from its end, the other road should start there; before its start, end there.
        var reversed = atEnd ? !otherAtStart : otherAtStart;
        if (!RoadEdits.CanMerge(road, other, reversed))
            return false;

        var merge = new RoadMerge { Kept = road, Merged = other, AtEnd = atEnd, Reversed = reversed };
        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        undo.CreateAction("Merge roads into one");
        undo.AddDoMethod(this, MethodName.DoMerge, merge);
        undo.AddUndoMethod(this, MethodName.UndoMerge, merge);
        undo.AddDoReference(merge);
        undo.AddDoMethod(this, MethodName.Select, road);
        undo.AddUndoMethod(this, MethodName.Select, _selected!);
        undo.CommitAction();
        _finished = true;
        GD.Print($"Merged {other.Name} into {road.Name}.");
        return true;
    }

    /// <summary>Merges two roads, from an undo step.</summary>
    public void DoMerge(RoadMerge merge)
    {
        // Not left selected while it is out of the scene (the Inspector then looks for it, and fails).
        EditorInterface.Singleton.GetSelection().RemoveNode(merge.Merged);
        RoadEdits.Merge(merge);
    }

    /// <summary>Takes merged roads apart, from an undo step.</summary>
    public void UndoMerge(RoadMerge merge) => RoadEdits.Unmerge(merge);

    /// <summary>Splits a road, from an undo step.</summary>
    public void DoSplit(RoadSplit split) => RoadEdits.Split(split);

    /// <summary>Puts a split road back, from an undo step.</summary>
    public void UndoSplit(RoadSplit split) => RoadEdits.Unsplit(split);

    private static RoadJunction NewJunction(EditorUndoRedoManager undo, Node network, Node root, Vector3 at)
    {
        var junction = new RoadJunction { Name = "Junction" };
        undo.AddDoMethod(network, Node.MethodName.AddChild, junction, true);
        undo.AddDoMethod(junction, Node.MethodName.SetOwner, root);
        undo.AddDoProperty(junction, Node3D.PropertyName.GlobalPosition, at);
        undo.AddDoReference(junction);
        undo.AddUndoMethod(network, Node.MethodName.RemoveChild, junction);
        return junction;
    }

    /// <summary>
    /// Ends the road being drawn at the junction, or starts a new road there if none is being
    /// drawn; a road ended so is finished.
    /// </summary>
    private void AttachDrawing(EditorUndoRedoManager undo, Node network, Node root, RoadJunction junction)
    {
        var (road, index, _) = Target();
        var point = new RoadPoint { Name = "RoadPoint" };
        var starting = road is null || road.Points.Count == 0;
        if (road is null)
        {
            road = NewRoad();
            undo.AddDoMethod(network, Node.MethodName.AddChild, road, true);
            undo.AddDoMethod(road, Node.MethodName.SetOwner, root);
            undo.AddDoReference(road);
            undo.AddUndoMethod(network, Node.MethodName.RemoveChild, road);
            index = 0;
        }
        else if (index < road.Points.Count)
        {
            index = road.Points.Count; // only a road's end can join a junction
        }

        undo.AddDoMethod(road, Node.MethodName.AddChild, point, true);
        undo.AddDoMethod(road, Node.MethodName.MoveChild, point, index);
        undo.AddDoMethod(point, Node.MethodName.SetOwner, root);
        undo.AddDoProperty(point, Node3D.PropertyName.GlobalPosition, junction.GlobalPosition);
        undo.AddDoReference(point);
        undo.AddUndoMethod(road, Node.MethodName.RemoveChild, point);

        var end = starting ? Road.PropertyName.StartJunction : Road.PropertyName.EndJunction;
        undo.AddDoProperty(road, end, junction);
        undo.AddUndoProperty(road, end, starting ? road.StartJunction ?? default(Variant) : road.EndJunction ?? default(Variant));
        undo.AddDoMethod(this, MethodName.Select, starting ? point : road);
        undo.AddUndoMethod(this, MethodName.Select, _selected!);
        _finished = !starting;
    }

    /// <summary>Whether a point is a loose end of its road (not at a junction), and which end.</summary>
    private static bool? LooseEnd(Road road, RoadPoint point)
    {
        var points = road.Points;
        if (road.Closed || points.Count < 2)
            return null;
        if (point == points[0] && road.StartJunction is null)
            return true;
        if (point == points[^1] && road.EndJunction is null)
            return false;
        return null;
    }

    private RoadJunction? JunctionUnder(Camera3D camera, Vector2 mouse)
    {
        RoadJunction? best = null;
        var bestDistance = PickRadius * 1.5f;
        foreach (var junction in Network()?.Junctions ?? System.Array.Empty<RoadJunction>())
        {
            if (camera.IsPositionBehind(junction.GlobalPosition))
                continue;
            var distance = camera.UnprojectPosition(junction.GlobalPosition).DistanceTo(mouse);
            if (distance < bestDistance)
            {
                best = junction;
                bestDistance = distance;
            }
        }

        if (best is not null)
            return best;

        // Or anywhere on a junction's surface.
        var from = camera.ProjectRayOrigin(mouse);
        var hit = camera.GetWorld3D().DirectSpaceState.IntersectRay(
            PhysicsRayQueryParameters3D.Create(from, from + camera.ProjectRayNormal(mouse) * camera.Far));
        return hit.Count > 0 && hit["collider"].AsGodotObject() is Node body && body.GetParent() is RoadJunction surface ? surface : null;
    }

    /// <summary>The road whose surface the mouse is over, and how far along it and where on its centre line that is.</summary>
    private (Road Road, float Distance, Vector3 At, Vector3 Hit)? RoadUnder(Camera3D camera, Vector2 mouse)
    {
        var from = camera.ProjectRayOrigin(mouse);
        var to = from + camera.ProjectRayNormal(mouse) * camera.Far;
        var hit = camera.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
        if (hit.Count == 0 || hit["collider"].AsGodotObject() is not Node body || body.GetParent() is not Road road)
            return null;

        var frames = road.FullFrames();
        if (frames.Count < 2)
            return null;
        var position = hit["position"].AsVector3();
        var distance = RoadEdits.Along(frames, position);
        var frame = RoadCurve.At(frames, distance);
        return (road, distance, frame.Position.ToGodot(), position);
    }

    /// <summary>Selects a node, from an undo step as well as directly.</summary>
    public void Select(Node node)
    {
        if (!IsInstanceValid(node) || !node.IsInsideTree())
            return;
        var selection = EditorInterface.Singleton.GetSelection();
        selection.Clear();
        selection.AddNode(node);
        EditorInterface.Singleton.EditNode(node);
    }

    // ------------------------------------------------------------------ where

    /// <summary>
    /// The road a click adds to, and where among its points: after the selected point, or at the
    /// end of the selected road; before its first point instead if that is selected and the road
    /// ends joined but starts loose, so it is drawn on back from its joined end. None (a new road)
    /// with a network selected or the road finished.
    /// </summary>
    private (Road? Road, int Index, bool NewRoad) Target()
    {
        // Past an end joined to a junction: not that road carried on through the junction (it ends
        // there), but a new road out of it (see StartFrom).
        var target = Extending();
        return target.Road is not null && JoinedEndAt(target.Road, target.Index) is not null ? (null, 0, true) : target;
    }

    /// <summary>The junction a new road starts from: the one selected, or the one the selected road's end being drawn on from is joined to.</summary>
    private RoadJunction? StartFrom()
    {
        if (_selected is RoadJunction junction)
            return junction;
        var (road, index, _) = Extending();
        return road is null ? null : JoinedEndAt(road, index);
    }

    /// <summary>The junction a road's end is joined to, if a point put in at this index would go past that end.</summary>
    private static RoadJunction? JoinedEndAt(Road road, int index) =>
        index >= road.Points.Count ? road.EndJunction as RoadJunction
        : index == 0 && road.Points.Count > 0 ? road.StartJunction as RoadJunction
        : null;

    /// <summary>Where a point would go on the selected road, its ends' junctions aside (see Target).</summary>
    private (Road? Road, int Index, bool NewRoad) Extending()
    {
        if (_finished)
            return (null, 0, true);
        return _selected switch
        {
            RoadPoint point when point.GetParent() is Road road && road.StartJunction is null && road.EndJunction is not null
                && road.Points.FirstOrDefault() == point => (road, point.GetIndex(), false),
            RoadPoint point when point.GetParent() is Road road => (road, point.GetIndex() + 1, false),
            Road road => (road, road.GetChildCount(), false),
            _ => (null, 0, true),
        };
    }

    /// <summary>The point a new one would follow, for the preview line.</summary>
    private Node3D? Anchor() => _selected switch
    {
        RoadPoint point => point,
        Road road => road.Points.LastOrDefault(),
        RoadJunction junction => junction,
        _ => null,
    };

    private RoadNetwork? Network()
    {
        for (var node = _selected; node is not null; node = node.GetParent())
        {
            if (node is RoadNetwork network)
                return network;
        }

        return null;
    }

    private IEnumerable<Road> RoadsToShow() => Network()?.Roads ?? (_selected switch
    {
        Road road => new[] { road },
        RoadPoint { } point when point.GetParent() is Road road => new[] { road },
        _ => System.Array.Empty<Road>(),
    });

    private Road NewRoad()
    {
        var section = Presets[_presetMenu?.Selected ?? 0].Section;
        return new Road
        {
            Name = "Road",
            ForwardLanes = section.ForwardLanes,
            BackwardLanes = section.BackwardLanes,
            LaneWidth = section.LaneWidth,
            ShoulderWidth = section.ShoulderWidth,
            MedianWidth = section.MedianWidth,
            Median = section.Median,
        };
    }

    private RoadPoint? PointUnder(Camera3D camera, Vector2 mouse)
    {
        RoadPoint? best = null;
        var bestDistance = PickRadius;
        foreach (var road in RoadsToShow())
        {
            foreach (var point in road.Points)
            {
                if (camera.IsPositionBehind(point.GlobalPosition))
                    continue;
                var distance = camera.UnprojectPosition(point.GlobalPosition).DistanceTo(mouse);
                if (distance < bestDistance)
                {
                    best = point;
                    bestDistance = distance;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Where the mouse is on the ground: the first solid thing its ray hits other than a road's
    /// own surface, or else where the network's terrain says its ground is.
    /// </summary>
    private Vector3? Ground(Camera3D camera, Vector2 mouse)
    {
        var from = camera.ProjectRayOrigin(mouse);
        var way = camera.ProjectRayNormal(mouse);

        if ((Network()?.Ground ?? RoadLinks.Ground?.Invoke(EditorInterface.Singleton.GetEditedSceneRoot()))?.Intersect(from, way) is { } hit)
            return hit;

        var space = camera.GetWorld3D().DirectSpaceState;
        var query = PhysicsRayQueryParameters3D.Create(from, from + way * camera.Far);
        var result = space.IntersectRay(query);
        return result.Count > 0 ? result["position"].AsVector3() : null;
    }
}
#endif
