using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// One surface of a baked building's mesh (one material), merged from many models' surfaces, with
/// simpler versions of itself for when it is further off (its levels of detail). Made our own way, not
/// by simplifying it (which closes windows: a wall with a hole in it is as flat as one without): no
/// corner is moved or merged; the simpler versions only leave out whole small details (a window's
/// lining, a trim, a rail) once each is too small to see, the walls, roofs and glass always kept.
/// </summary>
public sealed class RoadMergedSurface
{
    /// <summary>How big a detail each simpler version leaves out, and smaller, in metres.</summary>
    private static readonly float[] Levels = [0.03f, 0.06f, 0.12f, 0.25f, 0.5f];

    private readonly List<Vector3> _vertices = [];
    private readonly List<Vector3> _normals = [];
    private readonly List<float> _tangents = [];
    private readonly List<Color> _colors = [];
    private readonly List<Vector2> _uvs = [];
    private readonly List<Vector2> _uv2s = [];
    private bool _anyTangents, _anyColors, _anyUv2s;

    /// <summary>How big a detail each triangle is part of (float.MaxValue for one never left out), in order.</summary>
    private readonly List<float> _sizes = [];

    /// <summary>
    /// Its triangles' corners, three to a triangle, by their place among the corners above: each corner
    /// kept once, shared by every triangle that has it (as the graphics card would have it: stored once,
    /// worked out once), found again by everything about it.
    /// </summary>
    private readonly List<int> _indices = [];
    private readonly Dictionary<(Vector3, Vector3, Vector4, Color, Vector2, Vector2), int> _corners = [];

    /// <summary>
    /// A model's surface added, moved by a transform into the building's space: as a detail of a size
    /// (left out of the versions for further off than it is seen), or with none, always kept. With a
    /// colour given, every corner of it that colour (a window's glass, marked with its window).
    /// </summary>
    public void Append(Mesh mesh, int surface, Transform3D into, float? detail, Color? colour = null)
    {
        var arrays = mesh.SurfaceGetArrays(surface);
        if (arrays.Count <= (int)Mesh.ArrayType.Index || arrays[(int)Mesh.ArrayType.Vertex].VariantType == Variant.Type.Nil)
            return;
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var normals = arrays[(int)Mesh.ArrayType.Normal].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
        var tangents = arrays[(int)Mesh.ArrayType.Tangent].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.Tangent].AsFloat32Array();
        var colors = arrays[(int)Mesh.ArrayType.Color].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.Color].AsColorArray();
        var uvs = arrays[(int)Mesh.ArrayType.TexUV].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        var uv2s = arrays[(int)Mesh.ArrayType.TexUV2].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.TexUV2].AsVector2Array();
        var indices = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        var order = indices ?? Enumerable.Range(0, vertices.Length).ToArray();

        // Normals turned as the model is (stretched, by its inverse transposed), tangents as it is.
        var normalBasis = into.Basis.Inverse().Transposed();
        _anyTangents |= tangents is not null;
        _anyColors |= colour is not null || colors is not null;
        _anyUv2s |= uv2s is not null;
        foreach (var i in order)
        {
            var position = into * vertices[i];
            var normal = normals is null ? Vector3.Up : (normalBasis * normals[i]).Normalized();
            var tangent = new Vector4(1f, 0f, 0f, 1f);
            if (tangents is not null)
            {
                var turned = (into.Basis * new Vector3(tangents[4 * i], tangents[4 * i + 1], tangents[4 * i + 2])).Normalized();
                tangent = new Vector4(turned.X, turned.Y, turned.Z, tangents[4 * i + 3]);
            }

            var color = colour ?? (colors is not null ? colors[i] : Colors.White);
            var uv = uvs is not null ? uvs[i] : Vector2.Zero;
            var uv2 = uv2s is not null ? uv2s[i] : Vector2.Zero;
            var key = (Round(position), Round(normal), tangent, color, uv, uv2);
            if (!_corners.TryGetValue(key, out var index))
            {
                index = _vertices.Count;
                _corners[key] = index;
                _vertices.Add(position);
                _normals.Add(normal);
                _tangents.AddRange([tangent.X, tangent.Y, tangent.Z, tangent.W]);
                _colors.Add(color);
                _uvs.Add(uv);
                _uv2s.Add(uv2);
            }

            _indices.Add(index);
        }

        for (var k = 0; k < order.Length / 3; k++)
            _sizes.Add(detail ?? float.MaxValue);
    }

    public bool IsEmpty => _vertices.Count == 0;

    /// <summary>A place or direction rounded to a tenth of a millimetre, so corners a hair apart are one.</summary>
    private static Vector3 Round(Vector3 v) => new(Mathf.Round(v.X * 10000f) / 10000f, Mathf.Round(v.Y * 10000f) / 10000f, Mathf.Round(v.Z * 10000f) / 10000f);

    /// <summary>Added to a mesh as a surface of its own, with its simpler versions, in a material, named as given.</summary>
    public void AddTo(ArrayMesh mesh, Material? material, string name)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = _normals.ToArray();
        if (_anyTangents)
            arrays[(int)Mesh.ArrayType.Tangent] = _tangents.ToArray();
        if (_anyColors)
            arrays[(int)Mesh.ArrayType.Color] = _colors.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = _uvs.ToArray();
        if (_anyUv2s)
            arrays[(int)Mesh.ArrayType.TexUV2] = _uv2s.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = _indices.ToArray();

        // Each simpler version: every triangle but those of details no bigger than its level. Keyed by
        // how big what it leaves out is (Godot takes it when that is under a pixel or so on screen).
        var lods = new Godot.Collections.Dictionary();
        var kept = _sizes.Count;
        foreach (var level in Levels)
        {
            var indices = new List<int>();
            for (var k = 0; k < _sizes.Count; k++)
            {
                if (_sizes[k] > level)
                    indices.AddRange([_indices[3 * k], _indices[3 * k + 1], _indices[3 * k + 2]]);
            }

            // Only where it leaves out something more than the version before.
            if (indices.Count / 3 >= kept || indices.Count == 0)
                continue;
            kept = indices.Count / 3;
            lods[level] = indices.ToArray();
        }

        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, lods);
        var surface = mesh.GetSurfaceCount() - 1;
        mesh.SurfaceSetMaterial(surface, material);
        if (!string.IsNullOrEmpty(name))
            mesh.SurfaceSetName(surface, name);
    }

    /// <summary>
    /// The models that are details, left out of the simpler versions once small enough: linings, trims,
    /// plinths, sills, rails and posts (by their model's name). Walls, roofs, floors, foundations and
    /// glass are not.
    /// </summary>
    public static bool IsDetailModel(string name) =>
        name.Contains("Lining") || name.Contains("Trim") || name.Contains("Plinth") || name.Contains("Sill")
        || name.Contains("Rail") || name.Contains("Post");

    /// <summary>How big a detail is, seen: the middle of its three sizes (a long thin pipe is as big as it is thick).</summary>
    public static float SizeOf(Aabb box)
    {
        var sizes = new[] { Mathf.Abs(box.Size.X), Mathf.Abs(box.Size.Y), Mathf.Abs(box.Size.Z) };
        System.Array.Sort(sizes);
        return sizes[1];
    }
}
