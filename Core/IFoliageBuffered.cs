namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// A part of a road network with a foliage buffer: how far past its edges no foliage is painted (its
/// own, if it has Own Foliage Buffer on, else its network's Foliage Buffer). For a project to keep
/// foliage off; see RoadLinks.FootprintChanged, told on again when it changes.
/// </summary>
public interface IFoliageBuffered
{
    /// <summary>How far past its edges no foliage is painted, in metres: its own, or its network's.</summary>
    float FoliageBufferUsed { get; }

    /// <summary>Its buffer changed (its own, or its network's): where it lies told on again.</summary>
    void FoliageBufferChanged();
}
