using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>How the bays in a parking lot's bay area are laid out.</summary>
public enum BayLayout
{
    /// <summary>Side by side, square to their row: nose in from the aisle.</summary>
    SideBySide,

    /// <summary>Side by side, leaning at an angle to their row (see ParkingBayArea.Angle).</summary>
    Angled,

    /// <summary>End to end along their row, as along a kerb: a bay's depth along it, its width across.</summary>
    Parallel,
}

/// <summary>
/// An area of a <see cref="ParkingLot"/> drawn by hand to be filled with parking bays
/// (ParkingBayArea.tscn): inside the outline of its <see cref="ParkingBayCorner"/>s, rows of bays
/// as big as it says, back to back with no aisles (they go wherever it is not), along its own x axis from its own place. Moving or turning it moves or turns
/// its bays. A lot with any of these has only their bays, none of its own.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_roads/Icons/Parking-Bay.svg")]
public partial class ParkingBayArea : Node3D, IRoadOutlined
{
    private float _bayWidth = 2.5f;
    private float _bayDepth = 5f;
    private Transform3D? _notifiedAt;

    private BayLayout _layout = BayLayout.SideBySide;
    private float _angle = 60f;

    /// <summary>How its bays are laid out: side by side, angled, or end to end.</summary>
    [Export]
    public BayLayout Layout
    {
        get => _layout;
        set
        {
            _layout = value;
            NotifyPropertyListChanged();
            Refit();
        }
    }

    /// <summary>For angled bays: the angle between them and their row, 45 to 135 degrees (past 90 leaning the other way along it).</summary>
    [Export(PropertyHint.Range, "45,135,1,suffix:°")]
    public float Angle { get => _angle; set { _angle = Mathf.Clamp(value, 45f, 135f); Refit(); } }

    private int _baysX;
    private int _baysY;

    /// <summary>
    /// How many bays along each row. With this or <see cref="BaysY"/> set (the other taken as one while 0), the
    /// area is sized to fit just so many bays (its corners moved) whenever a bay setting changes; both 0 leaves
    /// its shape to its corners.
    /// </summary>
    [Export(PropertyHint.Range, "0,100,1")]
    public int BaysX { get => _baysX; set { _baysX = Math.Max(value, 0); NotifyPropertyListChanged(); Refit(); } }

    /// <summary>How many rows of bays, one behind another (see <see cref="BaysX"/>).</summary>
    [Export(PropertyHint.Range, "0,20,1")]
    public int BaysY { get => _baysY; set { _baysY = Math.Max(value, 0); NotifyPropertyListChanged(); Refit(); } }

    /// <summary>Whether it is sized by its bays (see <see cref="BaysX"/>), not by its corners: either count set (the other, unset, taken as one).</summary>
    private bool Sized => _baysX > 0 || _baysY > 0;

    public override void _ValidateProperty(Godot.Collections.Dictionary property)
    {
        if (property["name"].AsStringName() == PropertyName.Angle && _layout != BayLayout.Angled)
            property["usage"] = (int)PropertyUsageFlags.Storage;
        // Sized by its bays, they start at its corner: no shifting them.
        if (property["name"].AsStringName() == PropertyName.BayOffset && Sized)
            property["usage"] = (int)PropertyUsageFlags.Storage;
    }

    /// <summary>How wide each bay is (a car's width and room); for parallel bays, how deep across their row.</summary>
    [Export(PropertyHint.Range, "1,10,0.1,suffix:m")]
    public float BayWidth { get => _bayWidth; set { _bayWidth = Mathf.Max(value, 1f); Refit(); } }

    /// <summary>How deep each bay is, from the aisle in (a car's length and room); for parallel bays, how long along their row.</summary>
    [Export(PropertyHint.Range, "1,15,0.1,suffix:m")]
    public float BayDepth { get => _bayDepth; set { _bayDepth = Mathf.Max(value, 1f); Refit(); } }

    /// <summary>
    /// How far its bay lines are shifted from where they would start: x along the rows (a bay's
    /// width round, so any shift fits), y across them (the rows moved in from the edge they start at).
    /// </summary>
    [Export(PropertyHint.None, "suffix:m")]
    public Vector2 BayOffset { get => _bayOffset; set { _bayOffset = value; Changed(); } }

    private Vector2 _bayOffset;

    /// <summary>How far its bay lines are turned from its own x axis, in degrees (seen from above).</summary>
    [Export(PropertyHint.Range, "-180,180,0.5,suffix:°")]
    public float BayAngle { get => _bayAngle; set { _bayAngle = value; Refit(); } }

    private float _bayAngle;

    /// <summary>Its corners, in the order its outline runs round them.</summary>
    public IReadOnlyList<ParkingBayCorner> Corners => GetNodeOrNull("Shape")?.GetChildren().OfType<ParkingBayCorner>().ToList() ?? [];

    private ParkingLot? Lot => GetParent() as ParkingLot;

    public override void _Ready()
    {
        SetNotifyTransform(true);
        _notifiedAt = GlobalTransform;
        ChildOrderChanged += ChildrenChanged;
        AddFirstCorners();
        if (GetNodeOrNull("Shape") is { } shape)
            shape.ChildOrderChanged += ChildrenChanged;
        Changed();
    }

    public override void _ExitTree() => Lot?.QueueRebuild();

    public override void _Notification(int what)
    {
        if (what == NotificationTransformChanged && this.Moved(ref _notifiedAt))
            Changed();
    }

    // A method, not a lambda, so the editor can reconnect it after rebuilding the scripts.
    private void ChildrenChanged() => Changed();

    /// <summary>Its outline or bays changed: shown again, and its lot's bays painted again.</summary>
    public void Changed()
    {
        if (_changeQueued || !IsInsideTree())
            return;
        // At the end of the frame, once: its corners may not all be in the tree yet.
        _changeQueued = true;
        Callable.From(Show).CallDeferred();
    }

    private bool _changeQueued;

    /// <summary>A bay setting changed: sized to its bays again (see <see cref="BaysX"/>), then shown.</summary>
    private void Refit()
    {
        // Not as it is loaded (its settings set before it is in the tree): only when changed afterwards.
        if (IsInsideTree())
            _fitDue = true;
        Changed();
    }

    private bool _fitDue;

    /// <summary>
    /// Its corners moved to fit its bays just so (see <see cref="BaysX"/>): a rectangle from its first corner,
    /// along its bays' rows as far as so many bays reach (angled ones leaning on past their last), and across
    /// as deep as so many rows. Four corners, any more taken away, any missing put in.
    /// </summary>
    private void Fit()
    {
        if (GetNodeOrNull<Node3D>("Shape") is not { } shape)
            return;
        // Along a row: each bay's step and how far the last leans on; across, each row's depth.
        var (step, depth, lean) = _layout switch
        {
            BayLayout.Parallel => (_bayDepth, _bayWidth, 0f),
            BayLayout.Angled => (_bayWidth / MathF.Sin(Mathf.DegToRad(_angle)), _bayDepth * MathF.Sin(Mathf.DegToRad(_angle)), _bayDepth * MathF.Cos(Mathf.DegToRad(_angle))),
            _ => (_bayWidth, _bayDepth, 0f),
        };
        // Leaning back, the first bay's far end lies before its start: the bays start in by as much.
        _bayOffset = new Vector2(lean < 0f ? -lean : 0f, 0f);
        const float spare = 0.02f;
        var length = Math.Max(_baysX, 1) * step + MathF.Abs(lean) + spare;
        var deep = Math.Max(_baysY, 1) * depth + spare;
        // Its rows' way, as its bays are drawn (its x turned by BayAngle), and across them.
        var along = Vector3.Right.Rotated(Vector3.Up, Mathf.DegToRad(_bayAngle));
        var across = new Vector3(-along.Z, 0f, along.X);
        var corners = Corners.ToList();
        var start = corners.Count > 0 ? corners[0].Position : Vector3.Zero;
        var at = new[] { start, start + along * length, start + along * length + across * deep, start + across * deep };

        var owner = corners.FirstOrDefault()?.Owner ?? shape.Owner ?? Owner;
        foreach (var extra in corners.Skip(4))
        {
            shape.RemoveChild(extra);
            extra.QueueFree();
        }

        for (var i = corners.Count; i < 4; i++)
        {
            var corner = new ParkingBayCorner { Name = $"Corner{i + 1}" };
            shape.AddChild(corner, true);
            corner.Owner = owner;
            corners.Add(corner);
        }

        for (var i = 0; i < 4; i++)
            corners[i].Position = at[i];
#if TOOLS
        if (Engine.IsEditorHint())
            EditorInterface.Singleton.MarkSceneAsUnsaved();
#endif
    }

    private new void Show()
    {
        _changeQueued = false;
        if (!IsInsideTree())
            return;
        if (_fitDue)
        {
            _fitDue = false;
            if (Sized)
                Fit();
        }

        ShowCorners();
        Lot?.QueueRebuild();
    }

    /// <summary>
    /// One just added in the editor, with no corners yet: four, a square two bays deep round its
    /// place, as part of the scene being edited, to drag where they go.
    /// </summary>
    private void AddFirstCorners()
    {
        if (!Engine.IsEditorHint() || Corners.Count > 0 || GetTree().EditedSceneRoot is not { } root || root == this || !root.IsAncestorOf(this))
            return;
        // Under its Shape node, a node of the scene it is in (like the corners), so they are saved with it.
        var shape = new Node3D { Name = "Shape" };
        AddChild(shape, true);
        shape.Owner = root;
        var half = _bayDepth;
        var at = new[] { new Vector3(-half, 0f, -half), new Vector3(half, 0f, -half), new Vector3(half, 0f, half), new Vector3(-half, 0f, half) };
        for (var i = 0; i < at.Length; i++)
        {
            var corner = new ParkingBayCorner { Name = $"Corner{i + 1}", Position = at[i] };
            shape.AddChild(corner, true);
            corner.Owner = root;
        }
    }

    /// <summary>A line round its corners, in their order, on its Edge node: shown only in the editor, over everything.</summary>
    private void ShowCorners()
    {
        if ((GetNodeOrNull<MeshInstance3D>("Edge") ?? AddEdge()) is not { } edge)
            return;
        // Only while it is being worked on (see Outlines).
        AddToGroup(RoadOutlines.Group);
        edge.Visible = (GetParent() is { } lot && RoadOutlines.Shown(lot, "ParkingLots"));
        // Its gizmo, for clicking it in the editor, round its corners as they are now.
        UpdateGizmos();
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
            mesh.SurfaceAddVertex(toLocal * (corner.GlobalPosition + Vector3.Up * 0.25f));
        mesh.SurfaceEnd();
        edge.Mesh = mesh;
    }

    /// <summary>For an area made without its scene's Edge (added as a bare node): the Edge from ParkingBayArea.tscn, as its own.</summary>
    private MeshInstance3D? AddEdge()
    {
        const string sceneFile = "res://addons/unpleasant_roads/Scenes/ParkingBayArea.tscn";
        if (!ResourceLoader.Exists(sceneFile) || ResourceLoader.Load<PackedScene>(sceneFile).Instantiate() is not { } area)
            return null;
        var edge = area.GetNodeOrNull<MeshInstance3D>("Edge");
        if (edge is not null)
        {
            area.RemoveChild(edge);
            edge.Owner = null;
            AddChild(edge, false, InternalMode.Front);
        }

        area.Free();
        return edge;
    }

    /// <summary>Its outline shown in the editor only while its parking lot's is shown (see Outlines).</summary>
    public void ShowOutline()
    {
        if (GetNodeOrNull<MeshInstance3D>("Edge") is { } edge)
            edge.Visible = (GetParent() is { } lot && RoadOutlines.Shown(lot, "ParkingLots"));
    }
}
