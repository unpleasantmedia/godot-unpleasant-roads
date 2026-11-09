using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>What a lamp post does when it is not one of those lit near the camera.</summary>
public enum UnlitLamps
{
    /// <summary>Its lamp still glows, though it lights nothing round it.</summary>
    Glow,

    /// <summary>Its lamp is dark.</summary>
    Dark,
}

/// <summary>
/// The lamp posts along its roads (each road puts up its own; see <see cref="Road.LampPosts"/>):
/// the mesh they are drawn with, and their light. A real light only on those nearest the camera:
/// at most <see cref="LampLightMax"/>, within <see cref="LampLightDistance"/>, made again a few
/// times a second as the camera moves; the rest as <see cref="UnlitLampsLook"/> says. The lights
/// are drawn straight through the RenderingServer: nothing in the tree changes as they move.
/// </summary>
public partial class RoadNetwork
{
    /// <summary>The lamp post's scene, whose model they are drawn with.</summary>
    private const string LampPostScene = "res://addons/unpleasant_roads/Scenes/Lamp-Post.tscn";

    /// <summary>Where a lamp post's light is, and its lamp's glowing underside, in its own space.</summary>
    private static readonly Vector3 LampLightAt = new(0f, 4.7f, 1.2f);
    private static readonly Vector3 LampGlowAt = new(0f, 4.825f, 1.2f);

    /// <summary>A lamp post's pole, solid: its shape, and where it is in the post's own space.</summary>
    public static readonly BoxShape3D LampPoleShape = new() { Size = new Vector3(0.24f, 5f, 0.24f) };
    public static readonly Transform3D LampPoleAt = new(Basis.Identity, new Vector3(0f, 2.5f, 0f));

    /// <summary>How often the lit lamps are picked again, in seconds.</summary>
    private const double LampLookEvery = 0.25;

    /// <summary>How far the camera goes before the lit lamps are picked again, and where it was when they last were.</summary>
    private const float LampRepick = 3f;
    private Vector3? _litFrom;

    /// <summary>How many of its tunnels' light bars, at most, give real light at once: those nearest the camera. The rest only glow.</summary>
    [ExportGroup("Tunnel Lights")]
    [Export(PropertyHint.Range, "0,128,1")]
    public int TunnelLightMax { get; set; } = 24;

    [Export(PropertyHint.Range, "0,500,1,suffix:m")]
    public float TunnelLightDistance { get; set; } = 80f;

    private readonly System.Collections.Generic.List<Rid> _tunnelLights = [];
    private readonly System.Collections.Generic.List<Rid> _tunnelLightInstances = [];
    private TunnelLight[]? _tunnelPlaces;

    /// <summary>Real light on the tunnels' bars nearest the camera, always on: they are dark inside by day too.</summary>
    private void LightTunnels(Vector3? camera)
    {
        _tunnelPlaces ??= Roads.Where(r => IsInstanceValid(r) && r.IsInsideTree()).SelectMany(r => r.TunnelLightPlaces).ToArray();
        var lit = camera is not { } at || !IsInsideTree() ? []
            : _tunnelPlaces
                .Select(p => (Place: p, Away: p.Place.Origin.DistanceSquaredTo(at)))
                .Where(p => p.Away <= TunnelDistanceNow * TunnelDistanceNow)
                .OrderBy(p => p.Away).Take(TunnelMaxNow).Select(p => p.Place).ToList();
        var scenario = GetWorld3D().Scenario;
        while (_tunnelLights.Count < lit.Count)
        {
            var light = RenderingServer.OmniLightCreate();
            RenderingServer.LightSetShadow(light, false);
            // Not into bounced light (SDFGI), which is slow to catch up and spreads it over the roof.
            RenderingServer.LightSetBakeMode(light, RenderingServer.LightBakeMode.Disabled);
            _tunnelLights.Add(light);
            _tunnelLightInstances.Add(RenderingServer.InstanceCreate2(light, scenario));
        }

        while (_tunnelLights.Count > lit.Count)
        {
            RenderingServer.FreeRid(_tunnelLightInstances[^1]);
            RenderingServer.FreeRid(_tunnelLights[^1]);
            _tunnelLightInstances.RemoveAt(_tunnelLightInstances.Count - 1);
            _tunnelLights.RemoveAt(_tunnelLights.Count - 1);
        }

        for (var i = 0; i < lit.Count; i++)
        {
            FadeLight(_tunnelLights[i]);
            RenderingServer.LightSetColor(_tunnelLights[i], lit[i].Colour);
            RenderingServer.LightSetParam(_tunnelLights[i], RenderingServer.LightParam.Range, lit[i].Range);
            RenderingServer.LightSetParam(_tunnelLights[i], RenderingServer.LightParam.Energy, lit[i].Energy);
            RenderingServer.InstanceSetTransform(_tunnelLightInstances[i], lit[i].Place);
        }
    }

    private int _lampLightMax = 24;
    private float _lampLightDistance = 120f;
    private float _lampLightRange = 14f;
    private float _lampLightEnergy = 1.5f;
    private UnlitLamps _unlitLamps = UnlitLamps.Glow;

    /// <summary>How many lamp posts, at most, give real light at once: those nearest the camera.</summary>
    [ExportGroup("Lamp Posts")]
    [Export(PropertyHint.Range, "0,128,1")]
    public int LampLightMax
    {
        get => _lampLightMax;
        set
        {
            _lampLightMax = Mathf.Max(value, 0);
            _lampsDue = true;
        }
    }

    /// <summary>How many of the lit lamp posts nearest the camera cast shadows (each costs about six shadow renders).</summary>
    [Export(PropertyHint.Range, "0,128,1")]
    public int LampShadowMax
    {
        get => _lampShadowMax;
        set
        {
            _lampShadowMax = Mathf.Max(value, 0);
            _lampsDue = true;
        }
    }

    private int _lampShadowMax = 8;

    /// <summary>How near the camera a lamp post has to be to give real light.</summary>
    [Export(PropertyHint.Range, "10,1000,5,suffix:m")]
    public float LampLightDistance
    {
        get => _lampLightDistance;
        set
        {
            _lampLightDistance = Mathf.Max(value, 0f);
            _lampsDue = true;
        }
    }

    /// <summary>How far a lit lamp post's light reaches.</summary>
    [Export(PropertyHint.Range, "1,60,0.5,suffix:m")]
    public float LampLightRange
    {
        get => _lampLightRange;
        set
        {
            _lampLightRange = Mathf.Max(value, 0.5f);
            _lampsDue = true;
        }
    }

    /// <summary>How bright a lit lamp post's light is.</summary>
    [Export(PropertyHint.Range, "0,16,0.05")]
    public float LampLightEnergy
    {
        get => _lampLightEnergy;
        set
        {
            _lampLightEnergy = Mathf.Max(value, 0f);
            _lampsDue = true;
        }
    }

    /// <summary>What the lamp posts not lit near the camera do: their lamps glow on, or go dark.</summary>
    [Export]
    public UnlitLamps UnlitLampsLook
    {
        get => _unlitLamps;
        set
        {
            _unlitLamps = value;
            _lampMesh = null;
            foreach (var road in Roads)
                road.RedrawLamps();
            _lampsDue = true;
        }
    }

    private Mesh? _lampMesh;
    private Mesh? _lampGlow;
    private double _untilLampLook;
    private bool _lampsDue = true;

    // The lights and glows in use, drawn through the RenderingServer (Godot lists: kept when the editor rebuilds the scripts).
    private Godot.Collections.Array<Rid> _lampLights = new();
    private Godot.Collections.Array<Rid> _lampLightInstances = new();
    private Godot.Collections.Array<Rid> _lampGlows = new();

    /// <summary>
    /// What its lamp posts are drawn with: the lamp post's model, its lamp dark when unlit lamps go
    /// dark (the lit ones get a glow put over it). Null until the model has been imported.
    /// </summary>
    public Mesh? LampMesh()
    {
        if (_lampMesh is not null)
            return _lampMesh;
        if (!ResourceLoader.Exists(LampPostScene) || GD.Load<PackedScene>(LampPostScene).Instantiate() is not Node post)
            return null;
        var model = post.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>().FirstOrDefault()?.Mesh;
        post.Free();
        if (model is null)
            return null;
        if (_unlitLamps == UnlitLamps.Glow)
            return _lampMesh = model;

        // Dark: a copy with its glowing surface given a dark lamp instead.
        var dark = (Mesh)model.Duplicate();
        var off = new StandardMaterial3D { AlbedoColor = new Color(0.25f, 0.25f, 0.24f), Roughness = 0.4f };
        for (var surface = 0; surface < dark.GetSurfaceCount(); surface++)
        {
            if (dark.SurfaceGetMaterial(surface) is BaseMaterial3D { EmissionEnabled: true })
                dark.SurfaceSetMaterial(surface, off);
        }

        return _lampMesh = dark;
    }

    /// <summary>When the lamp post's scene and what it is made from were last changed on disk, as last looked.</summary>
    private ulong _lampPostChanged;
    private double _untilLampPostCheck;

    /// <summary>In the editor, every couple of seconds: the lamp post (its scene, model or materials) changed and saved, the roads' lamp posts drawn with it anew.</summary>
    private void WatchLampPost(double delta)
    {
        _untilLampPostCheck -= delta;
        if (_untilLampPostCheck > 0)
            return;
        _untilLampPostCheck = 2.0;
        if (!ResourceLoader.Exists(LampPostScene))
            return;
        var files = ResourceLoader.GetDependencies(LampPostScene).Select(d => d.Split("::")[^1]).Append(LampPostScene);
        var changed = files.Where(FileAccess.FileExists).Select(f => FileAccess.GetModifiedTime(f)).DefaultIfEmpty(0UL).Max();
        // Imported models: their import too.
        changed = files.Select(f => f + ".import").Where(FileAccess.FileExists).Select(f => FileAccess.GetModifiedTime(f)).Append(changed).Max();
        if (_lampPostChanged == 0)
        {
            _lampPostChanged = changed;
            return;
        }

        if (changed == _lampPostChanged)
            return;
        _lampPostChanged = changed;
        // Read afresh, not from the cache, and every road's lamp posts drawn with it.
        ResourceLoader.Load(LampPostScene, "", ResourceLoader.CacheMode.Replace);
        _lampMesh = null;
        foreach (var road in Roads)
            road.RedrawLamps();
        LampsChanged();
    }

    /// <summary>
    /// Whether something drawn in the network is part of its look that is baked (see BakedRoadsNode): a
    /// mesh or many of one (road surfaces, junctions, paths, parking lots, signs, gantries, lamp posts);
    /// not a traffic light (its lamps change), text (street names), or anything already baked.
    /// </summary>
    public static bool IsBakeable(GeometryInstance3D drawn, Node network)
    {
        if (drawn is not (MeshInstance3D { Mesh: not null } or MultiMeshInstance3D { Multimesh.Mesh: not null }))
            return false;
        for (Node? node = drawn; node is not null && node != network; node = node.GetParent())
        {
            if (node is TrafficLight or BakedRoadsNode)
                return false;
        }

        return true;
    }

    /// <summary>Whether its own look is hidden, a baked one shown in its place (see BakedRoadsNode).</summary>
    private bool _baked;

    /// <summary>Whether a baked look is shown in place of its own (in play, with a bake that is current).</summary>
    public bool ShowsBaked => _baked;

    /// <summary>
    /// Its own look hidden, for the baked one in its place (in play): all of it there now, and anything
    /// of it made after (a road built again, its lamp posts put up later).
    /// </summary>
    public void HideBaked()
    {
        if (_baked)
            return;
        _baked = true;
        foreach (var drawn in FindChildren("*", "GeometryInstance3D", true, false).OfType<GeometryInstance3D>())
        {
            if (IsBakeable(drawn, this))
                Drop(drawn);
        }

        GetTree().NodeAdded += BakedAdded;
    }

    private void BakedAdded(Node node)
    {
        // Its mesh may be given it once it is in: looked at once it is.
        if (node is GeometryInstance3D drawn && IsAncestorOf(drawn))
            Callable.From(() =>
            {
                if (IsInstanceValid(drawn) && IsBakeable(drawn, this))
                    Drop(drawn);
            }).CallDeferred();
    }

    /// <summary>
    /// A piece the baked look stands in for: hidden, and its mesh let go of, for its memory (on the GPU
    /// too). The node is kept, for the road that made it; one built again is given a mesh anew.
    /// </summary>
    private static void Drop(GeometryInstance3D drawn)
    {
        drawn.Visible = false;
        // Let go of here at once too: a C# object for it (made as it was looked at) holds on to it
        // until the garbage collector gets round to it, which let go of hundreds at once a moment
        // into the game, holding up a frame.
        if (drawn is MeshInstance3D model && model.Mesh is { } mesh)
        {
            model.Mesh = null;
            mesh.Dispose();
        }
        else if (drawn is MultiMeshInstance3D many && many.Multimesh is { } multi)
        {
            many.Multimesh = null;
            multi.Dispose();
        }
    }

    /// <summary>No longer hiding what is made of its look (it is leaving the tree).</summary>
    private void StopHidingBaked()
    {
        if (_baked && IsInsideTree())
            GetTree().NodeAdded -= BakedAdded;
    }

    /// <summary>A road's lamp posts changed: the lit ones picked again soon.</summary>
    public void LampsChanged()
    {
        _lampsDue = true;
        _lampPlaces = null;
        _tunnelPlaces = null;
    }

    /// <summary>
    /// Every road's lamp posts, where they stand, read once from the roads (read each time, through
    /// Godot's arrays, they cost more than all the rest of the lamps); again whenever any change.
    /// </summary>
    private Transform3D[]? _lampPlaces;
    private System.Collections.Generic.IReadOnlyList<Road>? _lampPlacesOf;

    /// <summary>The material of the lamps' glowing undersides, and how far on it was last set.</summary>
    private static ShaderMaterial? _glowMaterial;
    private static float _glowLevel = -1f;

    private bool _masksGiven;

    /// <summary>The most lights of each kind at once, when the player's distance decides instead.</summary>
    private const int LightsAtMost = 256;

    private float TunnelDistanceNow => RoadLinks.LightDistance?.Invoke()?.Distance ?? TunnelLightDistance;
    private float LampDistanceNow => RoadLinks.LightDistance?.Invoke()?.Distance ?? _lampLightDistance;
    private int TunnelMaxNow => RoadLinks.LightDistance?.Invoke() is null ? TunnelLightMax : LightsAtMost;
    private int LampMaxNow => RoadLinks.LightDistance?.Invoke() is null ? _lampLightMax : LightsAtMost;

    /// <summary>A light fading out towards the player's distance, if they have set one, so it does not pop.</summary>
    private static void FadeLight(Rid light)
    {
        if (RoadLinks.LightDistance?.Invoke() is { } set)
            RenderingServer.LightSetDistanceFade(light, true, set.Distance - set.Fade, set.Distance - set.Fade, set.Fade);
    }

    /// <summary>The distance last used, to pick the lit ones again when the player changes it.</summary>
    private float? _distanceUsed;

    /// <summary>The lamps' undersides lit as far as the lamps are on.</summary>
    private static void ShowLampLevel()
    {
        var level = RoadLinks.LampLevel?.Invoke() ?? 1f;
        if (Mathf.IsEqualApprox(level, _glowLevel))
            return;
        _glowLevel = level;
        _glowMaterial ??= GD.Load<ShaderMaterial>("res://addons/unpleasant_roads/Materials/Street-Lamp-Glow.tres");
        _glowMaterial?.SetShaderParameter("street_lamps", level);
    }

    public override void _Process(double delta)
    {
        ShowLampLevel();
        if (RoadLinks.LightDistance?.Invoke()?.Distance is { } distance && distance != _distanceUsed)
        {
            _distanceUsed = distance;
            _lampsDue = true;
        }

        // Once each time it is made, which rebuilding the scripts does: the terrain's masks went with them,
        // and nothing else tells its roads to give them again.
        if (!_masksGiven)
        {
            _masksGiven = true;
            foreach (var road in Roads)
                road.ShowTunnelMask();
        }

        if (Engine.IsEditorHint())
            WatchLampPost(delta);
        // Its junctions' traffic lights, all from here (see RoadJunction.RunLights).
        else
        {
            foreach (var junction in Junctions)
            {
                if (junction.HasLights && IsInstanceValid(junction))
                    junction.RunLights(delta);
            }
        }

        _untilLampLook -= delta;
        if (_untilLampLook > 0 && !_lampsDue)
            return;
        _untilLampLook = LampLookEvery;
        // Picked again only once the camera has moved a few metres, or the lamps changed.
        if (!_lampsDue && LampCamera() is { } camera && _litFrom is { } was && was.DistanceSquaredTo(camera) < LampRepick * LampRepick)
            return;
        _lampsDue = false;
        _litFrom = LampCamera();
        LightLamps();
    }

    /// <summary>A real light (and, if the rest are dark, a glow) on each of the lamp posts nearest the camera.</summary>
    private void LightLamps()
    {
        var camera = LampCamera();
        LightTunnels(camera);
        if (_lampPlaces is null || _lampPlacesOf != Roads)
        {
            _lampPlacesOf = Roads;
            _lampPlaces = Roads.Where(r => IsInstanceValid(r) && r.IsInsideTree()).SelectMany(r => r.LampPlaces).ToArray();
        }

        var lit = camera is not { } at || !IsInsideTree() ? new System.Collections.Generic.List<Transform3D>()
            : _lampPlaces
                .Select(p => (Place: p, Away: p.Origin.DistanceSquaredTo(at)))
                .Where(p => p.Away <= LampDistanceNow * LampDistanceNow)
                .OrderBy(p => p.Away).Take(LampMaxNow).Select(p => p.Place).ToList();

        // As many lights (and glows) as are wanted, no more.
        var scenario = GetWorld3D().Scenario;
        while (_lampLights.Count < lit.Count)
        {
            var light = RenderingServer.OmniLightCreate();
            RenderingServer.LightSetShadow(light, false);
            _lampLights.Add(light);
            _lampLightInstances.Add(RenderingServer.InstanceCreate2(light, scenario));
        }

        while (_lampLights.Count > lit.Count)
        {
            RenderingServer.FreeRid(_lampLightInstances[^1]);
            RenderingServer.FreeRid(_lampLights[^1]);
            _lampLightInstances.RemoveAt(_lampLightInstances.Count - 1);
            _lampLights.RemoveAt(_lampLights.Count - 1);
        }

        var glows = _unlitLamps == UnlitLamps.Dark ? lit.Count : 0;
        if (glows > 0)
            _lampGlow ??= new BoxMesh { Size = new Vector3(0.226f, 0.026f, 0.426f), Material = GD.Load<Material>("res://addons/unpleasant_roads/Materials/Street-Lamp-Glow.tres") };
        while (_lampGlows.Count < glows)
        {
            var glow = RenderingServer.InstanceCreate2(_lampGlow!.GetRid(), scenario);
            RenderingServer.InstanceGeometrySetCastShadowsSetting(glow, RenderingServer.ShadowCastingSetting.Off);
            _lampGlows.Add(glow);
        }

        while (_lampGlows.Count > glows)
        {
            RenderingServer.FreeRid(_lampGlows[^1]);
            _lampGlows.RemoveAt(_lampGlows.Count - 1);
        }

        var level = RoadLinks.LampLevel?.Invoke() ?? 1f;
        for (var i = 0; i < lit.Count; i++)
        {
            FadeLight(_lampLights[i]);
            RenderingServer.LightSetColor(_lampLights[i], new Color(1f, 0.9f, 0.75f));
            // The nearest ones cast shadows (they are nearest first).
            RenderingServer.LightSetShadow(_lampLights[i], i < _lampShadowMax);
            RenderingServer.LightSetParam(_lampLights[i], RenderingServer.LightParam.Range, _lampLightRange);
            // Lit only while the street lamps are on (see RoadLinks.LampLevel).
            RenderingServer.LightSetParam(_lampLights[i], RenderingServer.LightParam.Energy, _lampLightEnergy * level);
            RenderingServer.InstanceSetTransform(_lampLightInstances[i], new Transform3D(Basis.Identity, lit[i] * LampLightAt));
            if (i < glows)
                RenderingServer.InstanceSetTransform(_lampGlows[i], new Transform3D(lit[i].Basis, lit[i] * LampGlowAt));
        }
    }

    /// <summary>Its lights and glows put out and let go of (as it leaves the tree).</summary>
    private void PutOutLamps()
    {
        foreach (var rid in _lampLightInstances.Concat(_lampLights).Concat(_lampGlows).Concat(_tunnelLightInstances).Concat(_tunnelLights))
            RenderingServer.FreeRid(rid);
        _tunnelLightInstances.Clear();
        _tunnelLights.Clear();
        _lampLightInstances.Clear();
        _lampLights.Clear();
        _lampGlows.Clear();
        _lampsDue = true;
    }

    /// <summary>Where the camera is: the editor's view in the editor, the game's in the game.</summary>
    private Vector3? LampCamera()
    {
#if TOOLS
        if (Engine.IsEditorHint())
            return EditorInterface.Singleton.GetEditorViewport3D(0)?.GetCamera3D()?.GlobalPosition;
#endif
        return GetViewport()?.GetCamera3D()?.GlobalPosition;
    }
}
