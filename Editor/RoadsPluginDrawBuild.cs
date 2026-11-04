#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantRoads.Editor;

/// <summary>
/// Drawing a road with something picked in the build menu: each new stretch drawn (from the road's end to
/// the point clicked) is built as that. A viaduct or tunnel carried over it (joined onto one ending where it
/// starts); a ramp along it, or parking along it, on the side the road turns towards there (the right going
/// straight on). A fork or merge goes at the road's end instead (see ForkAtEnd).
/// </summary>
public partial class RoadsPlugin
{
    /// <summary>Whether the build menu's pick is built along a stretch as it is drawn.</summary>
    private bool BuildsAlong => (_rampMenu?.Selected ?? 0) is 1 or 2 or 3 or 4 || (_rampMenu?.Selected ?? 0) is >= FirstParkingItem and < FirstParkingItem + 4;

    /// <summary>
    /// The picked thing built along the stretch from a road's end to a point being added after it, as part of
    /// that point's undo step: with the road's points before it, and the point.
    /// </summary>
    private void BuildAlong(EditorUndoRedoManager undo, Road road, IReadOnlyList<Vector3> before, Vector3 at, RoadPoint point, Node root)
    {
        if (before.Count == 0)
            return;
        var old = before.Select(p => new CurvePoint(p.ToNumerics())).ToList();
        var grown = old.Append(new CurvePoint(at.ToNumerics())).ToList();
        var from = old.Count >= 2 ? Length(old, false) : 0f;
        var to = Length(grown, false);
        if (to - from < 1f)
            return;
        // The side the road turns towards: of the stretch before, which side the new point lies; the right going straight on.
        var side = RoadSide.Right;
        if (before.Count >= 2)
        {
            var way = (before[^1] - before[^2]) with { Y = 0f };
            var right = new Vector3(-way.Z, 0f, way.X).Normalized();
            if ((at - before[^1]).Dot(right) < -0.5f)
                side = RoadSide.Left;
        }

        var pick = _rampMenu?.Selected ?? 0;
        switch (pick)
        {
            case 3 or 4:
            {
                var kind = pick == 4 ? StructureKind.Tunnel : StructureKind.Viaduct;
                // Carried on: the one of its kind ending where this stretch starts, made longer.
                if (road.Structures.FirstOrDefault(s => s.Kind == kind && Mathf.Abs(s.Start + s.Length - from) < 2f) is { } going)
                {
                    undo.AddDoProperty(going, RoadStructure.PropertyName.Length, to - going.Start);
                    undo.AddUndoProperty(going, RoadStructure.PropertyName.Length, going.Length);
                    break;
                }

                var structure = new RoadStructure { Name = kind.ToString(), Kind = kind, Start = from, Length = Mathf.Max(to - from, 5f) };
                undo.AddDoMethod(road, Node.MethodName.AddChild, structure, true);
                undo.AddDoMethod(structure, Node.MethodName.SetOwner, root);
                undo.AddDoReference(structure);
                undo.AddUndoMethod(road, Node.MethodName.RemoveChild, structure);
                break;
            }

            case 1 or 2:
            {
                var kind = pick == 2 ? RampKind.Entrance : RampKind.Exit;
                var ramp = new RoadRamp
                {
                    Name = kind == RampKind.Exit ? "Exit" : "Entrance", Kind = kind, Side = side,
                    ShoulderHeight = road.ShoulderHeight, ShoulderAngle = road.ShoulderAngle,
                    Lanes = (int)(_rampLanes?.Value ?? 1),
                };
                // Along the stretch: its taper, its lane alongside, and its split, as long as the stretch (or as short as a ramp can be).
                ramp.Length = Mathf.Max(to - from, ramp.Taper + DivergeLength);
                ramp.ExitLaneLength = Mathf.Max(ramp.Length - ramp.Taper - DivergeLength, 0f);
                ramp.Start = from;
                undo.AddDoMethod(road, Node.MethodName.AddChild, ramp, true);
                undo.AddDoMethod(ramp, Node.MethodName.SetOwner, root);
                undo.AddDoReference(ramp);
                undo.AddUndoMethod(road, Node.MethodName.RemoveChild, ramp);
                // Its own road, from its end (the ramp puts it in place once the road is built).
                var frames = new RoadCurve(grown, false).Frames(1f);
                if (Network() is { } network)
                    RampRoad(undo, network, road, ramp, RoadRampShaper.BranchEnd(frames, ramp.Spec, road.CrossSection), root);
                _rampMenu?.Select(0); // one ramp per pick
                break;
            }

            default:
            {
                var kind = (ParkingKind)(pick - FirstParkingItem);
                if (to - from < 5f)
                    break;
                var parking = new RoadParking { Name = "Parking", Kind = kind, Side = side, Start = from, Length = to - from };
                undo.AddDoMethod(road, Node.MethodName.AddChild, parking, true);
                undo.AddDoMethod(parking, Node.MethodName.SetOwner, root);
                undo.AddDoReference(parking);
                undo.AddUndoMethod(road, Node.MethodName.RemoveChild, parking);
                break;
            }
        }

        // Drawing goes on along the road, from the point added.
        undo.AddDoMethod(this, MethodName.Select, point);
    }

    /// <summary>
    /// A fork (or merge) at the end of the road being drawn, a one-way road of two lanes or more: a fork, the road
    /// its trunk, a new road its straight branch to draw on, and another its other branch out to the point clicked;
    /// a merge, the road one branch coming in, another in from the point clicked, and a new road on from it, the
    /// trunk, to draw on. One step to undo.
    /// </summary>
    private RoadPoint? ForkAtEnd(Road road, Vector3 at, bool merge)
    {
        if (Network() is not { } network || road.Points is not { Count: >= 2 } points)
            return null;
        var lanes = road.ForwardLanes + road.BackwardLanes;
        var what = merge ? "Merge" : "Fork";
        if (road.BackwardLanes > 0)
        {
            GD.PushWarning(road.ForwardLanes > 0
                ? $"{what}: {road.Name} has lanes both ways; it has to be one way."
                : $"{what}: {road.Name}'s lanes run back towards where it was started; draw it the way its traffic goes (Forward Lanes) to put a {what.ToLowerInvariant()} at its end.");
            return null;
        }

        if (!merge && lanes < 2)
        {
            GD.PushWarning($"Fork: {road.Name} has only one lane; it needs two or more to part.");
            return null;
        }

        var end = points[^1].GlobalPosition;
        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        undo.CreateAction(merge ? "Add merge" : "Add fork");
        var fork = new RoadFork { Name = merge ? "Merge" : "Fork", LeftLanes = merge ? road.ForwardLanes : lanes - 1, Length = ForkLength };
        undo.AddDoMethod(network, Node.MethodName.AddChild, fork, true);
        undo.AddDoMethod(fork, Node.MethodName.SetOwner, root);
        undo.AddDoProperty(fork, Node3D.PropertyName.GlobalPosition, end);
        undo.AddDoReference(fork);
        undo.AddUndoMethod(network, Node.MethodName.RemoveChild, fork);
        undo.AddDoProperty(road, Road.PropertyName.EndJunction, fork);
        undo.AddUndoProperty(road, Road.PropertyName.EndJunction, road.EndJunction ?? default(Variant));

        RoadPoint? clicked = null;
        Vector3? farAt = null;
        Road Branch(string name, int forward, IEnumerable<Vector3> at)
        {
            var made = NewRoad();
            made.Name = name;
            made.ForwardLanes = forward;
            made.BackwardLanes = 0;
            made.LaneWidth = road.LaneWidth;
            made.ShoulderWidth = road.ShoulderWidth;
            made.ShoulderHeight = road.ShoulderHeight;
            made.ShoulderAngle = road.ShoulderAngle;
            // Made like the road drawn (once: free to change after).
            RoadFork.Adopt(made, road);
            undo.AddDoMethod(network, Node.MethodName.AddChild, made, true);
            undo.AddDoMethod(made, Node.MethodName.SetOwner, root);
            undo.AddDoReference(made);
            undo.AddUndoMethod(network, Node.MethodName.RemoveChild, made);
            foreach (var place in at)
            {
                var point = new RoadPoint { Name = "RoadPoint" };
                undo.AddDoMethod(made, Node.MethodName.AddChild, point, true);
                undo.AddDoMethod(point, Node.MethodName.SetOwner, root);
                undo.AddDoProperty(point, Node3D.PropertyName.GlobalPosition, place);
                undo.AddDoReference(point);
                if (farAt is { } f && place == f)
                    clicked = point;
            }

            return made;
        }

        // The road to draw on from the fork: a fork's straight branch, a merge's trunk. Its one point the fork puts in place.
        var onward = Branch(merge ? road.Name + "Merged" : road.Name + "Straight", merge ? road.ForwardLanes + 1 : lanes - 1, [end]);
        undo.AddDoProperty(onward, Road.PropertyName.StartJunction, fork);
        // The other branch: out to the point clicked from the fork, or in from it to the fork. Its far end past the
        // fork's own far end (a click within the fork's length would have it double back), as far aside as clicked.
        var forward = road.FullFrames() is { Count: >= 2 } frames ? (frames[^1].Forward.ToGodot() with { Y = 0f }).Normalized() : Vector3.Forward;
        var ahead = (at - end).Dot(forward);
        if (merge ? ahead > -(ForkLength + 20f) : ahead < ForkLength + 20f)
            at += forward * ((merge ? -(ForkLength + 30f) : ForkLength + 30f) - ahead);
        farAt = at;
        var other = Branch(merge ? "MergeRoad" : "ForkRoad", 1, merge ? [at, end] : [end, at]);
        undo.AddDoProperty(other, merge ? Road.PropertyName.EndJunction : Road.PropertyName.StartJunction, fork);
        undo.AddDoMethod(this, MethodName.SelectLastPointOf, onward);
        undo.AddUndoMethod(this, MethodName.Select, _selected!);
        undo.CommitAction();
        _rampMenu?.Select(0); // one per pick
        _finished = false;
        GD.Print($"Added a {(merge ? "merge" : "fork")} at {road.Name}'s end: click on to draw {onward.Name} on from it.");
        return clicked;
    }

    /// <summary>Selects a road's last point (to draw on from it).</summary>
    private void SelectLastPointOf(Road road)
    {
        if (road.Points is { Count: > 0 } points)
            Select(points[^1]);
    }
}
#endif
