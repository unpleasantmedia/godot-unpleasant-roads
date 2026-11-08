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
    /// <summary>A surface's arrays, as read out of its mesh.</summary>
    private sealed record SurfaceArrays(Vector3[] Vertices, Vector3[]? Normals, float[]? Tangents, Color[]? Colors, Vector2[]? Uvs, Vector2[]? Uv2s, int[] Order);

    /// <summary>
    /// While set (see <see cref="ReadOnce"/>), each mesh's surfaces read out of Godot only once: one
    /// piece placed many times (a lamp post, a barrier) would otherwise be read again for each place.
    /// </summary>
    private static Dictionary<(Mesh, int), SurfaceArrays?>? _read;

    /// <summary>Reads each mesh's surfaces once only until disposed: for a bake, while its meshes do not change.</summary>
    public static System.IDisposable ReadOnce()
    {
        _read = [];
        _signatures = [];
        return new Done();
    }

    private sealed class Done : System.IDisposable
    {
        public void Dispose()
        {
            _read = null;
            _signatures = null;
        }
    }

    private static Dictionary<(Mesh, int), ulong>? _signatures;

    /// <summary>
    /// A number standing for a surface's shape (its corners, normals, colours, uvs and triangles): the
    /// same for the same shape, from run to run. For a bake to tell which of its squares changed.
    /// </summary>
    public static ulong Signature(Mesh mesh, int surface)
    {
        if (_signatures is not null && _signatures.TryGetValue((mesh, surface), out var known))
            return known;
        var hash = 14695981039346656037UL;
        void Bytes(System.ReadOnlySpan<byte> bytes)
        {
            foreach (var b in bytes)
                hash = (hash ^ b) * 1099511628211UL;
            hash = (hash ^ 0xFF) * 1099511628211UL;
        }

        void Of<T>(T[]? values) where T : unmanaged
        {
            if (values is null)
                Bytes([0]);
            else
                Bytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes<T>(values));
        }

        if (Read(mesh, surface) is { } read)
        {
            Of(read.Vertices);
            Of(read.Normals);
            Of(read.Tangents);
            Of(read.Colors);
            Of(read.Uvs);
            Of(read.Uv2s);
            Of(read.Order);
        }

        if (_signatures is not null)
            _signatures[(mesh, surface)] = hash;
        return hash;
    }

    private static SurfaceArrays? Read(Mesh mesh, int surface)
    {
        if (_read is not null && _read.TryGetValue((mesh, surface), out var known))
            return known;
        var arrays = mesh.SurfaceGetArrays(surface);
        SurfaceArrays? read = null;
        if (arrays.Count > (int)Mesh.ArrayType.Index && arrays[(int)Mesh.ArrayType.Vertex].VariantType != Variant.Type.Nil)
        {
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var indices = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            read = new SurfaceArrays(
                vertices,
                arrays[(int)Mesh.ArrayType.Normal].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.Normal].AsVector3Array(),
                arrays[(int)Mesh.ArrayType.Tangent].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.Tangent].AsFloat32Array(),
                arrays[(int)Mesh.ArrayType.Color].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.Color].AsColorArray(),
                arrays[(int)Mesh.ArrayType.TexUV].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array(),
                arrays[(int)Mesh.ArrayType.TexUV2].VariantType == Variant.Type.Nil ? null : arrays[(int)Mesh.ArrayType.TexUV2].AsVector2Array(),
                indices ?? Enumerable.Range(0, vertices.Length).ToArray());
        }

        if (_read is not null)
            _read[(mesh, surface)] = read;
        return read;
    }

    public void Append(Mesh mesh, int surface, Transform3D into, float? detail, Color? colour = null)
    {
        if (Read(mesh, surface) is not { } read)
            return;
        var (vertices, normals, tangents, colors, uvs, uv2s, order) = (read.Vertices, read.Normals, read.Tangents, read.Colors, read.Uvs, read.Uv2s, read.Order);

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

    /// <summary>How big a surface can be and still be stored compressed, in metres (see AddTo).</summary>
    private const float CompressWithin = 500f;

    /// <summary>Added to a mesh as a surface of its own, with its simpler versions, in a material, named as given.</summary>
    public void AddTo(ArrayMesh mesh, Material? material, string name)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = _normals.ToArray();
        // Tangents only for a material that bends its light by a normal map (a shader's might).
        if (_anyTangents && material is not BaseMaterial3D { NormalEnabled: false })
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

        // Stored compressed (16-bit, within its bounds) where that is still fine enough: across at most
        // CompressWithin metres, a few millimetres, well under how high lines sit above the road.
        var low = _vertices.Aggregate(Vector3.Inf, (a, v) => a.Min(v));
        var high = _vertices.Aggregate(-Vector3.Inf, (a, v) => a.Max(v));
        var size = high - low;
        var compress = Mathf.Max(size.X, Mathf.Max(size.Y, size.Z)) <= CompressWithin;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, lods,
            compress ? Mesh.ArrayFormat.FlagCompressAttributes : 0);
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
