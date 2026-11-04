using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// A town square (Square.tscn, put under the road network like a parking lot): a parking lot paved all
/// over as a sidewalk, at a sidewalk's height, with no bays. It meets the roads' sidewalks as a lot does:
/// along a road, its edge goes onto the back of the road's sidewalk, its top level with it.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_roads/Icons/Square.svg")]
public partial class Square : ParkingLot
{
    protected override bool AllSidewalk => true;

    /// <summary>Whether generating buildings may put plots and buildings on it; off, it keeps them off as a parking lot does.</summary>
    [Export]
    public bool BuildOn { get; set; }

    /// <summary>Whether a lot keeps generated plots and buildings off it: all but a square they may go on.</summary>
    public static bool KeepsBuildingsOff(ParkingLot lot) => lot is not Square { BuildOn: true };

    public override void _ValidateProperty(Godot.Collections.Dictionary property)
    {
        base._ValidateProperty(property);
        // No bays on a square: their settings not shown.
        var name = property["name"].AsStringName();
        if (name == PropertyName.Markings || name == PropertyName.BayWidth || name == PropertyName.BayDepth || name == PropertyName.BayOffset
            || name == PropertyName.BayAngle || name == PropertyName.AisleWidth)
            property["usage"] = (int)PropertyUsageFlags.Storage;
    }
}
