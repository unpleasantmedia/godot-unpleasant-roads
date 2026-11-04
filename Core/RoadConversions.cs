namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>Between Godot's vectors and the ones the engine-free road maths works in.</summary>
public static class RoadConversions
{
    public static System.Numerics.Vector3 ToNumerics(this Godot.Vector3 v) => new(v.X, v.Y, v.Z);

    public static Godot.Vector3 ToGodot(this System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    public static Godot.Vector2 ToGodot(this System.Numerics.Vector2 v) => new(v.X, v.Y);
}
