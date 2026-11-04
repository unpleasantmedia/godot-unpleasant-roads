using System;
using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>The ground as it was before roads were fitted to it, kept in a file of its own (see RoadNetwork).</summary>
[Tool]
[GlobalClass]
public partial class RoadGroundData : Resource
{
    [Export]
    public Vector3[] Points { get; set; } = Array.Empty<Vector3>();
}
