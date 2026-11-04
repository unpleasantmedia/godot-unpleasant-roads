using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// A road network's look baked into a few meshes (see <see cref="Bake"/>, the Bake Roads tool): every
/// road's, junction's, path's and parking lot's surfaces, their signs, gantries and lamp posts, merged by
/// material in squares of the ground, so they cost a few draw calls rather than one or more each. The
/// network stays as it is, to be edited and to be baked again: in play its own look is hidden (see
/// RoadNetwork.HideBaked), and this one shown; in the editor, the network's own shows, and this does not.
/// Its collision, traffic lights, street names and lit lamps are the network's still.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_roads/Icons/Road-Baked.svg")]
public partial class BakedRoadsNode : Node3D
{
    /// <summary>Where baked roads' meshes are saved.</summary>
    public static string MeshFolder => RoadLinks.BakedFolder ?? "res://BakedRoads";

    /// <summary>How big the squares are that the roads' big parts (surfaces, junctions) and small things (signs, lamp posts) are merged in, in metres.</summary>
    private const float BigSquare = 250f;
    private const float SmallSquare = 100f;

    /// <summary>What a surface with no material of its own is drawn in, baked: as Godot draws one.</summary>
    private static readonly Material Unmaterialled = new StandardMaterial3D();

    /// <summary>The network it is the look of.</summary>
    [Export]
    public NodePath Network { get; set; } = new();

    /// <summary>The network as it was when baked (see RoadNetwork.Fingerprint): changed since, this is out of date.</summary>
    [Export]
    public string Fingerprint { get; set; } = "";

    public override void _Ready()
    {
        // Only in play: in the editor the network's own look is shown, to be edited.
        Visible = !Engine.IsEditorHint();
        if (Engine.IsEditorHint() || GetNodeOrNull<RoadNetwork>(Network) is not { } network)
            return;
        // Out of date (the roads changed since it was baked): the network's own look shown instead.
        if (network.Fingerprint() != Fingerprint)
        {
            Visible = false;
            GD.PushWarning($"{Name}: the roads have changed since they were baked; showing them unbaked (slower). Run Tools → Bake Roads and save the level.");
            return;
        }

        network.HideBaked();
    }

    /// <summary>
    /// A road network's look baked: merged as above into meshes saved in <see cref="MeshFolder"/>, under a
    /// new BakedRoadsNode beside it (in place of one it had). Returns that node, or null with nothing to bake.
    /// </summary>
    public static BakedRoadsNode? Bake(RoadNetwork network, Node owner)
    {
        if (network.GetParent() is not Node3D holder)
            return null;
        var name = $"{owner.SceneFilePath.GetFile().GetBaseName()}-{network.Name}";
        var toNetwork = network.GlobalTransform.AffineInverse();

        // Each drawn thing's surfaces, by its square, whether it is small, whether it casts a shadow, and its material.
        var merged = new Dictionary<(Vector2I Square, bool Small, GeometryInstance3D.ShadowCastingSetting Shadow), Dictionary<Material, RoadMergedSurface>>();
        void Add(Mesh mesh, Transform3D into, bool small, GeometryInstance3D.ShadowCastingSetting shadow, System.Func<int, Material?> materialOf)
        {
            var middle = (into * mesh.GetAabb()).GetCenter();
            var size = small ? SmallSquare : BigSquare;
            var square = new Vector2I(Mathf.FloorToInt(middle.X / size), Mathf.FloorToInt(middle.Z / size));
            if (!merged.TryGetValue((square, small, shadow), out var byMaterial))
                merged[(square, small, shadow)] = byMaterial = new Dictionary<Material, RoadMergedSurface>();
            for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
            {
                // Triangles only (not an editor outline drawn in lines).
                var triangles = mesh is ArrayMesh made ? made.SurfaceGetPrimitiveType(surface) == Mesh.PrimitiveType.Triangles : mesh is not ImmediateMesh;
                if (!triangles)
                    continue;
                // (One with none drawn in Godot's own default, as it was.)
                var material = materialOf(surface) ?? Unmaterialled;
                if (!byMaterial.TryGetValue(material, out var into2))
                    byMaterial[material] = into2 = new RoadMergedSurface();
                into2.Append(mesh, surface, into, null);
            }
        }

        foreach (var drawn in network.FindChildren("*", "GeometryInstance3D", true, false).OfType<GeometryInstance3D>())
        {
            if (!RoadNetwork.IsBakeable(drawn, network) || !drawn.IsVisibleInTree())
                continue;
            var small = drawn.VisibilityRangeEnd > 0f;
            var place = toNetwork * drawn.GlobalTransform;
            if (drawn is MeshInstance3D { Mesh: { } mesh } model)
                Add(mesh, place, small, model.CastShadow, s => model.MaterialOverride ?? model.GetSurfaceOverrideMaterial(s) ?? mesh.SurfaceGetMaterial(s));
            else if (drawn is MultiMeshInstance3D { Multimesh: { Mesh: { } piece } multi } many)
            {
                for (var i = 0; i < multi.InstanceCount; i++)
                    Add(piece, place * multi.GetInstanceTransform(i), small, many.CastShadow, s => many.MaterialOverride ?? piece.SurfaceGetMaterial(s));
            }
        }

        if (merged.Count == 0)
            return null;

        // Its meshes saved afresh (any from before taken away), under a node of its own beside the network.
        DirAccess.MakeDirRecursiveAbsolute(MeshFolder);
        using (var folder = DirAccess.Open(MeshFolder))
        {
            foreach (var old in folder?.GetFiles().Where(f => f.StartsWith(name + "-")) ?? [])
                folder!.Remove(old);
        }

        // Any baked before for this network (its path is kept from itself, so it is looked up from there).
        foreach (var old in holder.GetChildren().OfType<BakedRoadsNode>().Where(b => b.GetNodeOrNull(b.Network) == network).ToList())
        {
            holder.RemoveChild(old);
            old.QueueFree();
        }

        var baked = new BakedRoadsNode { Name = $"{network.Name}Baked", Transform = network.Transform };
        holder.AddChild(baked);
        baked.Owner = owner;
        baked.Network = baked.GetPathTo(network);
        baked.Fingerprint = network.Fingerprint();
        var count = 0;
        foreach (var ((square, small, shadow), byMaterial) in merged)
        {
            var mesh = new ArrayMesh();
            foreach (var (material, surface) in byMaterial.Where(e => !e.Value.IsEmpty))
                surface.AddTo(mesh, material, "");
            if (mesh.GetSurfaceCount() == 0)
                continue;
            var path = $"{MeshFolder}/{name}-{++count}.res";
            if (ResourceSaver.Save(mesh, path) != Error.Ok)
            {
                GD.PushError($"Bake Roads: could not save {path}.");
                continue;
            }

            // The mesh as loaded from its file, the one copy the editor keeps for that path: the scene
            // then refers to the file. (A mesh only given the path is left with none once the editor
            // loads the file again on its own, having seen it change; the scene then holds it whole, as text.)
            var saved = ResourceLoader.Load<ArrayMesh>(path, null, ResourceLoader.CacheMode.Replace) ?? mesh;
            var chunk = new MeshInstance3D { Name = $"{(small ? "Small" : "Roads")}{square.X}_{square.Y}", Mesh = saved, CastShadow = shadow };
            // Small things (signs, lamp posts) not drawn from far off, as they were not (see DrawDistance).
            if (small)
                RoadDrawDistance.LimitSmall(chunk);
            baked.AddChild(chunk);
            chunk.Owner = owner;
        }

        // Not shown in the editor (the network's own look is): see _Ready.
        baked.Visible = false;
        return baked;
    }
}
