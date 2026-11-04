using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>What grows on a parking lot's island.</summary>
public enum IslandPlanting
{
    Grass,

    /// <summary>Grass, and a tree in its middle.</summary>
    Tree,

    /// <summary>Grass, and low shrubs along it.</summary>
    Shrubs,
}

/// <summary>
/// A planted island in a parking lot (put under the lot): a kerb as high as a sidewalk's round a grass top,
/// inside the outline of the <see cref="ParkingIslandCorner"/>s under its Shape node, each corner rounded.
/// It stands on the lot's surface; the lot's bays keep off it.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_roads/Icons/Parking-Island.svg")]
public partial class ParkingIsland : Node3D
{
    private static string? TreeScene => RoadLinks.IslandTree;
    private static string? ShrubScene => RoadLinks.IslandShrub;

    /// <summary>How wide the kerb's top is, round the grass, in metres.</summary>
    private const float KerbWidth = 0.15f;

    /// <summary>How far apart shrubs stand along it, in metres.</summary>
    private const float ShrubSpacing = 1.6f;

    private IslandPlanting _planting = IslandPlanting.Tree;
    private float _cornerRadius = 1.2f;
    private Transform3D? _notifiedAt;
    private bool _changeQueued;
    private MeshInstance3D? _mesh;
    private StaticBody3D? _body;
    private Node3D? _plants;

    /// <summary>What grows on it.</summary>
    [Export]
    public IslandPlanting Planting { get => _planting; set { _planting = value; Changed(); } }

    /// <summary>How round its corners are, in metres (as far as its sides allow).</summary>
    [Export(PropertyHint.Range, "0,5,0.1,suffix:m")]
    public float CornerRadius { get => _cornerRadius; set { _cornerRadius = Mathf.Max(value, 0f); Changed(); } }

    /// <summary>Its corners, in the order its outline runs round them.</summary>
    public IReadOnlyList<ParkingIslandCorner> Corners => GetNodeOrNull("Shape")?.GetChildren().OfType<ParkingIslandCorner>().ToList() ?? [];

    /// <summary>Its outline, rounded, in the world (at its corners' heights): for the lot's bays to keep off.</summary>
    public IReadOnlyList<Vector3> Outline { get; private set; } = [];

    private ParkingLot? Lot => GetParent() as ParkingLot;

    public override void _Ready()
    {
        SetNotifyTransform(true);
        _notifiedAt = GlobalTransform;
        AddFirstCorners();
        if (GetNodeOrNull("Shape") is { } shape)
            shape.ChildOrderChanged += Changed;
        Changed();
    }

    public override void _ExitTree() => Lot?.QueueRebuild();

    public override void _Notification(int what)
    {
        if (what == NotificationTransformChanged && this.Moved(ref _notifiedAt))
            Changed();
    }

    /// <summary>Its shape or settings changed: built again at the end of the frame, and its lot's bays laid again.</summary>
    public void Changed()
    {
        if (_changeQueued || !IsInsideTree())
            return;
        _changeQueued = true;
        Callable.From(Build).CallDeferred();
    }

    /// <summary>One just added in the editor, with no corners yet: four, a bay wide and two deep, as part of the scene being edited.</summary>
    private void AddFirstCorners()
    {
        if (!Engine.IsEditorHint() || Corners.Count > 0 || GetTree().EditedSceneRoot is not { } root || root == this || !root.IsAncestorOf(this))
            return;
        var shape = new Node3D { Name = "Shape" };
        AddChild(shape, true);
        shape.Owner = root;
        var at = new[] { new Vector3(-1.25f, 0f, -5f), new Vector3(1.25f, 0f, -5f), new Vector3(1.25f, 0f, 5f), new Vector3(-1.25f, 0f, 5f) };
        for (var i = 0; i < at.Length; i++)
        {
            var corner = new ParkingIslandCorner { Name = $"Corner{i + 1}", Position = at[i] };
            shape.AddChild(corner, true);
            corner.Owner = root;
        }
    }

    private void Build()
    {
        _changeQueued = false;
        if (!IsInsideTree())
            return;
        _mesh ??= AddInternal(new MeshInstance3D { Name = "Mesh" });
        _body ??= AddInternal(new StaticBody3D { Name = "Body" });
        _plants ??= AddInternal(new Node3D { Name = "Plants" });
        foreach (var old in _body.GetChildren().Concat(_plants.GetChildren()))
        {
            old.GetParent().RemoveChild(old);
            old.QueueFree();
        }

        var corners = Corners.Select(c => c.GlobalPosition).ToList();
        if (corners.Count < 3)
        {
            _mesh.Mesh = null;
            Outline = [];
            Lot?.QueueRebuild();
            return;
        }

        var lot = Lot;
        float Ground(Vector2 p, float fallback) => lot?.HeightAt(p.X, p.Y) ?? fallback;
        var flat = Rounded(corners.Select(c => new Vector2(c.X, c.Z)).ToArray(), _cornerRadius);
        var fallback = corners.Average(c => c.Y);
        Outline = flat.Select(p => new Vector3(p.X, Ground(p, fallback), p.Y)).ToList();

        // Its top level, a kerb's height over the lot's highest point under it; its sides down to the lowest, a little under.
        var top = Outline.Max(p => p.Y) + RoadCrossSection.CurbHeight;
        var bottom = Outline.Min(p => p.Y) - 0.1f;
        var toLocal = GlobalTransform.AffineInverse();
        var tool = new SurfaceTool();
        var mesh = new ArrayMesh();

        // The kerb: its sides, and its top all over (the grass laid just above, inside it).
        tool.Begin(Mesh.PrimitiveType.Triangles);
        var clockwise = Area(flat) < 0f;
        for (var i = 0; i < flat.Length; i++)
        {
            var (a, b) = (flat[i], flat[(i + 1) % flat.Length]);
            var side = b - a;
            if (side.LengthSquared() < 1e-6f)
                continue;
            var outward = (clockwise ? new Vector2(-side.Y, side.X) : new Vector2(side.Y, -side.X)).Normalized();
            Quad(tool, toLocal, new Vector3(a.X, bottom, a.Y), new Vector3(b.X, bottom, b.Y), new Vector3(b.X, top, b.Y), new Vector3(a.X, top, a.Y),
                new Vector3(outward.X, 0f, outward.Y));
        }

        Cap(tool, toLocal, flat, top);
        tool.SetMaterial(Road.DefaultMaterial("Sidewalk"));
        tool.Commit(mesh);

        var grass = Geometry2D.OffsetPolygon(flat, -KerbWidth).OrderByDescending(p => MathF.Abs(Area(p))).FirstOrDefault();
        if (grass is { Length: >= 3 })
        {
            tool.Begin(Mesh.PrimitiveType.Triangles);
            Cap(tool, toLocal, grass, top + 0.01f);
            tool.SetMaterial(Road.DefaultMaterial("Grass"));
            tool.Commit(mesh);
        }

        _mesh.Mesh = mesh;
        if (mesh.GetSurfaceCount() > 0 && mesh.GetFaces() is { Length: > 0 } faces)
            _body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = faces } });

        Plant(grass ?? flat, top + 0.01f);
        // Its gizmo, for clicking it in the editor, round it as it is now.
        UpdateGizmos();
        Lot?.QueueRebuild();
    }

    /// <summary>Its planting put on its grass: a tree in the middle, or shrubs along its length.</summary>
    private void Plant(Vector2[] grass, float top)
    {
        if (_plants is null || _planting == IslandPlanting.Grass || grass.Length < 3)
            return;
        var middle = grass.Aggregate(Vector2.Zero, (a, b) => a + b) / grass.Length;
        if (_planting == IslandPlanting.Tree)
        {
            Put(TreeScene, middle, top, 0f);
            return;
        }

        // Along its longest way: from end to end, a shrub every so often, inside it.
        var longest = Enumerable.Range(0, grass.Length).Select(i => grass[(i + 1) % grass.Length] - grass[i]).MaxBy(s => s.LengthSquared()).Normalized();
        var (lo, hi) = (grass.Min(p => (p - middle).Dot(longest)), grass.Max(p => (p - middle).Dot(longest)));
        var count = Math.Max(1, (int)MathF.Floor((hi - lo) / ShrubSpacing));
        var random = new Random((int)(middle.X * 73856093f) ^ (int)(middle.Y * 19349663f));
        for (var k = 0; k < count; k++)
        {
            var at = middle + longest * (lo + (hi - lo) * (k + 0.5f) / count);
            if (Geometry2D.IsPointInPolygon(at, grass))
                Put(ShrubScene, at, top, (float)random.NextDouble() * Mathf.Tau);
        }
    }

    private void Put(string? scene, Vector2 at, float height, float turn)
    {
        if (_plants is null || scene is null || !ResourceLoader.Exists(scene) || ResourceLoader.Load<PackedScene>(scene).Instantiate() is not Node3D plant)
            return;
        _plants.AddChild(plant);
        plant.GlobalTransform = new Transform3D(Basis.FromEuler(new Vector3(0f, turn, 0f)), new Vector3(at.X, height, at.Y));
    }

    /// <summary>A flat polygon's top, at a height, facing up.</summary>
    private static void Cap(SurfaceTool tool, Transform3D toLocal, Vector2[] outline, float height)
    {
        var indices = Geometry2D.TriangulatePolygon(outline);
        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            var (a, b, c) = (outline[indices[i]], outline[indices[i + 1]], outline[indices[i + 2]]);
            var tri = new[] { a, b, c };
            // Wound to face up (Godot's front faces wind clockwise, seen from the front).
            if ((b - a).Cross(c - a) < 0f)
                tri = [a, c, b];
            foreach (var p in tri)
            {
                tool.SetNormal(Vector3.Up);
                tool.SetUV(p / RoadMeshBuilder.TextureSize);
                tool.AddVertex(toLocal * new Vector3(p.X, height, p.Y));
            }
        }
    }

    /// <summary>A quad from four points in the world (round in order), facing a way.</summary>
    private static void Quad(SurfaceTool tool, Transform3D toLocal, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 facing)
    {
        var quad = new[] { a, b, c, d };
        if ((b - a).Cross(c - a).Dot(facing) > 0f)
            quad = [d, c, b, a];
        foreach (var i in new[] { 0, 1, 2, 0, 2, 3 })
        {
            tool.SetNormal(facing);
            var p = quad[i];
            tool.SetUV(new Vector2(p.X + p.Z, p.Y) / RoadMeshBuilder.TextureSize);
            tool.AddVertex(toLocal * p);
        }
    }

    /// <summary>
    /// An outline with each corner rounded by up to a radius (as far as half its sides either side allow),
    /// each round in short steps.
    /// </summary>
    private static Vector2[] Rounded(Vector2[] outline, float radius)
    {
        if (radius <= 0f)
            return outline;
        var n = outline.Length;
        var points = new List<Vector2>();
        for (var i = 0; i < n; i++)
        {
            var (before, at, after) = (outline[(i + n - 1) % n], outline[i], outline[(i + 1) % n]);
            var (u, v) = (before - at, after - at);
            var (lu, lv) = (u.Length(), v.Length());
            if (lu < 1e-3f || lv < 1e-3f)
            {
                points.Add(at);
                continue;
            }

            (u, v) = (u / lu, v / lv);
            var angle = MathF.Acos(Mathf.Clamp(u.Dot(v), -1f, 1f));
            if (angle < 0.01f || angle > MathF.PI - 0.01f)
            {
                points.Add(at);
                continue;
            }

            // How far along each side the round starts: as far as the radius needs, at most half of each side.
            var reach = MathF.Min(radius / MathF.Tan(angle / 2f), 0.5f * MathF.Min(lu, lv));
            var r = reach * MathF.Tan(angle / 2f);
            var centre = at + (u + v).Normalized() * (r / MathF.Sin(angle / 2f));
            var (from, to) = (at + u * reach, at + v * reach);
            var (a0, a1) = ((from - centre).Angle(), (to - centre).Angle());
            var sweep = Mathf.Wrap(a1 - a0, -MathF.PI, MathF.PI);
            var steps = Math.Max(2, (int)MathF.Ceiling(MathF.Abs(sweep) / (MathF.PI / 12f)));
            for (var k = 0; k <= steps; k++)
                points.Add(centre + Vector2.FromAngle(a0 + sweep * k / steps) * r);
        }

        return points.ToArray();
    }

    private static float Area(Vector2[] polygon)
    {
        var sum = 0f;
        for (var i = 0; i < polygon.Length; i++)
            sum += polygon[i].Cross(polygon[(i + 1) % polygon.Length]);
        return sum / 2f;
    }

    private T AddInternal<T>(T node) where T : Node
    {
        AddChild(node, false, InternalMode.Front);
        return node;
    }
}
