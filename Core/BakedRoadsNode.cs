using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// A road network's look baked into a few meshes (see <see cref="Bake"/>, the Bake Roads button on a RoadNetwork): every
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
            GD.PushWarning($"{Name}: the roads have changed since they were baked; showing them unbaked (slower). Press Bake Roads in the road network's Inspector and save the level.");
            return;
        }

        network.HideBaked();
        // Drawn to the game's view distances: its squares of road no further than the level is, its
        // small things no further than objects are (squares baked before there were settings too).
        foreach (var square in GetChildren().OfType<GeometryInstance3D>())
        {
            // One reaching far past its square (a road's tunnels and viaducts are one mesh, put in the
            // square of its middle, which can be far from all of them) is always drawn: judged by its
            // middle, it would go when that is out of reach, though its parts are near.
            if (square.GetAabb().Size is var size && Mathf.Max(size.X, size.Z) > 2f * BigSquare)
                continue;
            if (square.IsInGroup(RoadDrawDistance.NearGroup))
                RoadDrawDistance.LimitNear(square);
            else if (square.VisibilityRangeEnd > 0f)
                RoadDrawDistance.LimitSmall(square);
            else
                RoadDrawDistance.LimitLevel(square);
        }
    }

    /// <summary>
    /// What was in each baked square when it was baked (its pieces' shapes, places and materials, as
    /// one number), by its key (see <see cref="KeyOf"/>): a square whose pieces are the same now is
    /// not baked again.
    /// </summary>
    [Export]
    public Godot.Collections.Dictionary<string, string> SquarePrints { get; set; } = new();

    /// <summary>The metadata on each baked mesh's node naming its square, as its key.</summary>
    private const string SquareMeta = "baked_square";

    /// <summary>
    /// How baked meshes are made, in each square's print: changed when that changes (as when they
    /// came to be stored compressed), for every square to be baked again the next time.
    /// </summary>
    private const string BakeFormat = "compressed-1|";

    /// <summary>A square's key: x and z, small (1) or big (0), and how it casts shadows; each has a mesh of its own.</summary>
    private static string KeyOf(Vector3I square, GeometryInstance3D.ShadowCastingSetting shadow) => $"{square.X},{square.Y},{square.Z},{(int)shadow}";

    /// <summary>A piece of the network to be baked: its mesh, where it is, what it is drawn with, and the square it falls in.</summary>
    private sealed record Piece(Mesh Mesh, Transform3D Place, bool Small, GeometryInstance3D.ShadowCastingSetting Shadow, System.Func<int, Material?> MaterialOf, Vector3I Square);

    /// <summary>
    /// A road network's look baked: merged as above into meshes saved in <see cref="MeshFolder"/>, a file
    /// for each square, under a BakedRoadsNode beside it. Baked before, only the squares whose pieces
    /// changed (in shape, place or material; come or gone) are baked again, every other square's mesh
    /// kept as it was; the first time, all of them. Returns the node, or null with nothing to bake.
    /// </summary>
    public static BakedRoadsNode? Bake(RoadNetwork network, Node owner)
    {
        if (network.GetParent() is not Node3D holder)
            return null;
        var name = $"{owner.SceneFilePath.GetFile().GetBaseName()}-{network.Name}";
        var toNetwork = network.GlobalTransform.AffineInverse();
        // Each mesh read once, however many times it is placed.
        using var readOnce = RoadMergedSurface.ReadOnce();
        var existing = holder.GetChildren().OfType<BakedRoadsNode>().FirstOrDefault(b => b.GetNodeOrNull(b.Network) == network);
        var all = existing is null || existing.SquarePrints.Count == 0;

        // Every piece and its square, without merging any, to find which squares are to be baked.
        var pieces = new List<Piece>();
        Vector3I SquareOf(Mesh mesh, Transform3D into, bool small)
        {
            var middle = (into * mesh.GetAabb()).GetCenter();
            var size = small ? SmallSquare : BigSquare;
            return new Vector3I(Mathf.FloorToInt(middle.X / size), Mathf.FloorToInt(middle.Z / size), small ? 1 : 0);
        }

        // Things drawn only near (see RoadDrawDistance.Near) in squares of their own, kept that near.
        Vector3I Near(Vector3I square, GeometryInstance3D drawn) => drawn.IsInGroup(RoadDrawDistance.NearGroup) ? square with { Z = 2 } : square;

        foreach (var drawn in network.FindChildren("*", "GeometryInstance3D", true, false).OfType<GeometryInstance3D>())
        {
            if (!RoadNetwork.IsBakeable(drawn, network) || !drawn.IsVisibleInTree())
                continue;
            var small = drawn.VisibilityRangeEnd > 0f;
            var place = toNetwork * drawn.GlobalTransform;
            if (drawn is MeshInstance3D { Mesh: { } mesh } model)
                pieces.Add(new Piece(mesh, place, small, model.CastShadow, s => model.MaterialOverride ?? model.GetSurfaceOverrideMaterial(s) ?? mesh.SurfaceGetMaterial(s), Near(SquareOf(mesh, place, small), model)));
            else if (drawn is MultiMeshInstance3D { Multimesh: { Mesh: { } piece } multi } many)
            {
                for (var i = 0; i < multi.InstanceCount; i++)
                {
                    var at = place * multi.GetInstanceTransform(i);
                    pieces.Add(new Piece(piece, at, small, many.CastShadow, s => many.MaterialOverride ?? piece.SurfaceGetMaterial(s), Near(SquareOf(piece, at, small), many)));
                }
            }
        }

        // What is in each square now, as one number, to be told from what was.
        var materials = new Dictionary<Material, string>();
        string MaterialPrint(Material? material) =>
            material is null ? "none"
            : materials.TryGetValue(material, out var known) ? known
            : materials[material] = material.ResourcePath is { Length: > 0 } file && !file.Contains("::") ? file : RoadNetwork.ResourceFingerprint(material);
        var prints = new Dictionary<string, string>();
        foreach (var square in pieces.GroupBy(p => KeyOf(p.Square, p.Shadow)))
        {
            var text = new System.Text.StringBuilder();
            foreach (var piece in square)
            {
                text.Append(piece.Place).Append('|');
                for (var surface = 0; surface < piece.Mesh.GetSurfaceCount(); surface++)
                    text.Append(RoadMergedSurface.Signature(piece.Mesh, surface)).Append('/').Append(MaterialPrint(piece.MaterialOf(surface))).Append(';');
            }

            prints[square.Key] = (BakeFormat + text.ToString()).Md5Text();
        }

        var dirty = all ? prints.Keys.ToHashSet() : prints.Keys.Union(existing!.SquarePrints.Keys)
            .Where(k => !prints.TryGetValue(k, out var now) || !existing.SquarePrints.TryGetValue(k, out var then) || now != then).ToHashSet();
        if (!all && dirty.Count == 0)
        {
            existing!.Fingerprint = network.Fingerprint();
            GD.Print($"Bake Roads: no square of {network.Name} has changed since it was baked.");
            return existing;
        }

        // The pieces of the squares to bake, merged by square, whether small, shadow, and material.
        var merged = new Dictionary<(Vector3I Square, GeometryInstance3D.ShadowCastingSetting Shadow), Dictionary<Material, RoadMergedSurface>>();
        foreach (var piece in pieces.Where(p => dirty.Contains(KeyOf(p.Square, p.Shadow))))
        {
            if (!merged.TryGetValue((piece.Square, piece.Shadow), out var byMaterial))
                merged[(piece.Square, piece.Shadow)] = byMaterial = new Dictionary<Material, RoadMergedSurface>();
            for (var surface = 0; surface < piece.Mesh.GetSurfaceCount(); surface++)
            {
                // Triangles only (not an editor outline drawn in lines).
                var triangles = piece.Mesh is ArrayMesh made ? made.SurfaceGetPrimitiveType(surface) == Mesh.PrimitiveType.Triangles : piece.Mesh is not ImmediateMesh;
                if (!triangles)
                    continue;
                // (One with none drawn in Godot's own default, as it was.)
                var material = piece.MaterialOf(surface) ?? Unmaterialled;
                if (!byMaterial.TryGetValue(material, out var into))
                    byMaterial[material] = into = new RoadMergedSurface();
                into.Append(piece.Mesh, surface, piece.Place, null);
            }
        }

        DirAccess.MakeDirRecursiveAbsolute(MeshFolder);
        BakedRoadsNode baked;
        if (all)
        {
            // All of it afresh: the meshes from before taken away, and the node they were under.
            using (var folder = DirAccess.Open(MeshFolder))
            {
                foreach (var old in folder?.GetFiles().Where(f => f.StartsWith(name + "-")) ?? [])
                    folder!.Remove(old);
            }

            foreach (var old in holder.GetChildren().OfType<BakedRoadsNode>().Where(b => b.GetNodeOrNull(b.Network) == network).ToList())
            {
                holder.RemoveChild(old);
                old.QueueFree();
            }

            baked = new BakedRoadsNode { Name = $"{network.Name}Baked", Transform = network.Transform };
            holder.AddChild(baked);
            baked.Owner = owner;
            baked.Network = baked.GetPathTo(network);
        }
        else
        {
            // Only the squares baked again: theirs from before taken away, file and all.
            baked = existing!;
            foreach (var chunk in baked.GetChildren().OfType<MeshInstance3D>().ToList())
            {
                if (!dirty.Contains(chunk.GetMeta(SquareMeta, "").AsString()))
                    continue;
                if (chunk.Mesh?.ResourcePath is { Length: > 0 } file && FileAccess.FileExists(file))
                    DirAccess.RemoveAbsolute(file);
                baked.RemoveChild(chunk);
                chunk.QueueFree();
            }
        }

        foreach (var ((square, shadow), byMaterial) in merged)
        {
            var mesh = new ArrayMesh();
            foreach (var (material, surface) in byMaterial.Where(e => !e.Value.IsEmpty))
                surface.AddTo(mesh, material, "");
            if (mesh.GetSurfaceCount() == 0)
                continue;
            var near = square.Z == 2;
            var small = square.Z >= 1;
            var path = $"{MeshFolder}/{name}-{(near ? "N" : small ? "S" : "R")}{square.X}_{square.Y}-{(int)shadow}.res";
            if (ResourceSaver.Save(mesh, path) != Error.Ok)
            {
                GD.PushError($"Bake Roads: could not save {path}.");
                continue;
            }

            // The mesh as loaded from its file, the one copy the editor keeps for that path: the scene
            // then refers to the file. (A mesh only given the path is left with none once the editor
            // loads the file again on its own, having seen it change; the scene then holds it whole, as text.)
            var saved = ResourceLoader.Load<ArrayMesh>(path, null, ResourceLoader.CacheMode.Replace) ?? mesh;
            var chunk = new MeshInstance3D { Name = $"{(near ? "Near" : small ? "Small" : "Roads")}{square.X}_{square.Y}", Mesh = saved, CastShadow = shadow };
            chunk.SetMeta(SquareMeta, KeyOf(square, shadow));
            // Small things (signs, lamp posts) not drawn from far off, as they were not (see DrawDistance).
            if (near)
                RoadDrawDistance.LimitNear(chunk);
            else if (small)
                RoadDrawDistance.LimitSmall(chunk);
            baked.AddChild(chunk, true);
            chunk.Owner = owner;
        }

        baked.Fingerprint = network.Fingerprint();
        baked.SquarePrints = new Godot.Collections.Dictionary<string, string>(prints);
        // Not shown in the editor (the network's own look is): see _Ready.
        baked.Visible = false;
        GD.Print(all ? $"Bake Roads: baked all of {network.Name}." : $"Bake Roads: {dirty.Count} squares of {network.Name} changed; baked them again.");
        return baked;
    }
}
