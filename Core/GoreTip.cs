using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// A gore tip (Assets/Meshes/Roads/Gore-Tip.glb): the crash cushion at the nose of a gore, where barriers either side of it
/// begin, its striped face towards the traffic coming at it (its +Z), its rails back into the gore (its -Z). Put up by
/// roads at their ramps' gores, and by forks at theirs.
/// </summary>
public static class GoreTip
{
    private const string Model = "res://addons/unpleasant_roads/Meshes/Gore-Tip.glb";

    /// <summary>Its head, as modelled: how big, and where its middle stands (on its own axes).</summary>
    private static readonly Vector3 HeadSize = new(0.62f, 1.14f, 0.4f);

    private static readonly Vector3 HeadMiddle = new(0f, 0.57f, 0f);

    /// <summary>
    /// One put under a node, at a place (in the world) facing the way the traffic comes from (level), solid where its
    /// head is. Nothing, before the model is imported.
    /// </summary>
    public static void Put(Node3D under, Vector3 at, Vector3 towardsTraffic)
    {
        if (!ResourceLoader.Exists(Model) || GD.Load<PackedScene>(Model)?.Instantiate() is not Node3D tip)
            return;
        var facing = towardsTraffic with { Y = 0f };
        if (facing.LengthSquared() < 1e-6f)
            return;
        facing = facing.Normalized();
        // Its +Z the way traffic comes from: looking (its -Z) back into the gore.
        var place = new Transform3D(Basis.LookingAt(-facing, Vector3.Up), at);
        under.AddChild(tip, false, Node.InternalMode.Back);
        tip.GlobalTransform = place;
        RoadDrawDistance.LimitSmall(tip);
        var body = new StaticBody3D { Name = "Body" };
        tip.AddChild(body);
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = HeadSize }, Position = HeadMiddle });
    }
}
