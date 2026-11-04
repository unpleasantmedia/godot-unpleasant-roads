namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>What a road is as a path (a plot's footpath or drive), if it is one: see Road.PathKind.</summary>
public enum RoadPathKind
{
    /// <summary>A road, not a path.</summary>
    None,
    PavingStones,
    Gravel,
    Concrete,

    /// <summary>Loose slabs, one after another, the ground showing between them.</summary>
    SteppingStones,
}
