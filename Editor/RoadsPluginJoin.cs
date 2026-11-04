#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantRoads.Editor;

/// <summary>
/// The scene dock's right-click menu, with two or more roads selected: Join Roads (see
/// RoadsPlugin.JoinRoads).
/// </summary>
[Tool]
public partial class RoadJoinMenu : EditorContextMenuPlugin
{
    public RoadsPlugin? Plugin { get; set; }

    public override void _PopupMenu(string[] paths)
    {
        if (Roads(paths).Count >= 2)
            AddContextMenuItem("Join Roads", new Callable(this, MethodName.Join));
    }

    private void Join(Godot.Collections.Array selected)
    {
        var roads = selected.Select(v => v.Obj).OfType<Road>().ToList();
        if (roads.Count < 2)
            roads = Roads(selected.Select(v => v.ToString()).ToArray());
        Plugin?.JoinRoads(roads);
    }

    /// <summary>The roads among the nodes at these paths (in the scene being edited).</summary>
    private static List<Road> Roads(IEnumerable<string> paths)
    {
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        return root is null ? [] : paths.Select(p => root.GetNodeOrNull(p) ?? root.GetTree().Root.GetNodeOrNull(p)).OfType<Road>().Distinct().ToList();
    }
}

public partial class RoadsPlugin
{
    /// <summary>What a road's end is on: a junction, a ramp or a parking lot; none, a loose end.</summary>
    private static Node3D? EndOn(Road road, bool atStart) => (atStart ? road.StartJunction : road.EndJunction) as Node3D;

    /// <summary>How close roads have to come to a place to be joined there (an end of theirs, or the road itself), in metres.</summary>
    private const float JoinReach = 6f;

    /// <summary>How a road meets the place roads are joined at: one of its ends (which), or passing through it (how far along).</summary>
    private readonly record struct Meeting(Road Road, bool? AtStart, float Along);

    /// <summary>
    /// Roads selected together joined where they meet. Two meeting end to end, into one road (their
    /// ends merged, or the junction between them taken away). Otherwise at a junction there (the one
    /// some already end at, or a new one): each end there brought onto it, and each road passing
    /// through there split at it, both halves its arms. One step to undo.
    /// </summary>
    public void JoinRoads(List<Road> roads)
    {
        roads = [.. roads.Where(r => IsInstanceValid(r) && r.Points.Count >= 2).Distinct()];
        if (roads.Count < 2)
            return;
        if (MeetingPlace(roads) is not { } place)
        {
            GD.PushWarning($"Join Roads: {string.Join(", ", roads.Select(r => r.Name))} do not all come within {JoinReach} m of one place to join at.");
            return;
        }

        var (at, meetings) = place;
        var ends = meetings.Where(m => m.AtStart is not null).ToList();
        var on = ends.Select(m => EndOn(m.Road, m.AtStart!.Value)).OfType<Node3D>().Distinct().ToList();
        if (on.Any(n => n is not RoadJunction))
        {
            GD.PushWarning("Join Roads: an end is on a ramp or parking lot; only loose ends and junctions are joined.");
            return;
        }

        var shared = on.OfType<RoadJunction>().ToList();
        if (shared.Count > 1)
        {
            GD.PushWarning($"Join Roads: their ends are at different junctions ({string.Join(", ", shared.Select(j => j.Name))}); join those first.");
            return;
        }

        var junction = shared.FirstOrDefault();
        if (roads.Count == 2 && ends.Count == 2)
        {
            // Two at a junction of their own: it taken away, the two merged.
            if (junction is not null && ends.All(m => EndOn(m.Road, m.AtStart!.Value) == junction))
            {
                if (junction.Arms().Count != 2)
                {
                    GD.PushWarning($"Join Roads: {junction.Name} has other roads too; it stays a junction.");
                    return;
                }

                TidyJunctions([junction]);
                return;
            }

            // Two loose ends: the second carrying the first on.
            if (junction is null && MergeEnds((ends[0].Road, ends[0].AtStart!.Value), (ends[1].Road, ends[1].AtStart!.Value)))
                return;
        }

        JoinAt(junction, junction?.GlobalPosition ?? at, meetings);
    }

    /// <summary>
    /// The place the roads all meet, and how each meets it: tried at every end of every road, and
    /// wherever two of them cross; the one each road comes nearest (its end, else itself), all within reach.
    /// </summary>
    private static (Vector3 At, List<Meeting> Meetings)? MeetingPlace(List<Road> roads)
    {
        Vector3 EndAt(Road road, bool atStart) => EndOn(road, atStart) is { } j ? j.GlobalPosition : (atStart ? road.Points[0] : road.Points[^1]).GlobalPosition;
        var lines = roads.ToDictionary(r => r, r => r.FullFrames().Select(f => (At: f.Position.ToGodot(), f.Distance)).ToList());

        var places = roads.SelectMany(r => new[] { EndAt(r, true), EndAt(r, false) }).ToList();
        for (var i = 0; i < roads.Count; i++)
        {
            for (var j = i + 1; j < roads.Count; j++)
                places.AddRange(Crossings(lines[roads[i]], lines[roads[j]]));
        }

        (Vector3 At, List<Meeting> Meetings)? best = null;
        var bestSpread = float.MaxValue;
        foreach (var place in places)
        {
            var meetings = new List<Meeting>();
            var spread = 0f;
            foreach (var road in roads)
            {
                var (start, end) = (Flat(EndAt(road, true)).DistanceTo(Flat(place)), Flat(EndAt(road, false)).DistanceTo(Flat(place)));
                if (Mathf.Min(start, end) <= JoinReach)
                {
                    meetings.Add(new Meeting(road, start <= end, 0f));
                    spread = Mathf.Max(spread, Mathf.Min(start, end));
                    continue;
                }

                // Through it: the nearest it comes, not near either end.
                var (along, off) = Nearest(lines[road], place);
                if (off > JoinReach)
                {
                    spread = float.MaxValue;
                    break;
                }

                meetings.Add(new Meeting(road, null, along));
                spread = Mathf.Max(spread, off);
            }

            if (spread < bestSpread)
                (best, bestSpread) = ((place, meetings), spread);
        }

        return best;
    }

    private static Vector2 Flat(Vector3 at) => new(at.X, at.Z);

    /// <summary>How far along a road's line the point on it nearest a place is, and how far the place is from it (seen from above).</summary>
    private static (float Along, float Off) Nearest(List<(Vector3 At, float Distance)> line, Vector3 place)
    {
        var best = (Along: 0f, Off: float.MaxValue);
        for (var i = 0; i + 1 < line.Count; i++)
        {
            var (a, b) = (Flat(line[i].At), Flat(line[i + 1].At));
            var run = b - a;
            var t = run.LengthSquared() < 1e-6f ? 0f : Mathf.Clamp((Flat(place) - a).Dot(run) / run.LengthSquared(), 0f, 1f);
            var off = (a + run * t).DistanceTo(Flat(place));
            if (off < best.Off)
                best = (Mathf.Lerp(line[i].Distance, line[i + 1].Distance, t), off);
        }

        return best;
    }

    /// <summary>Where two roads' lines cross, seen from above, at the height between them there.</summary>
    private static IEnumerable<Vector3> Crossings(List<(Vector3 At, float Distance)> one, List<(Vector3 At, float Distance)> other)
    {
        for (var i = 0; i + 1 < one.Count; i++)
        {
            for (var j = 0; j + 1 < other.Count; j++)
            {
                var (a, b, c, d) = (one[i].At, one[i + 1].At, other[j].At, other[j + 1].At);
                var hit = Geometry2D.SegmentIntersectsSegment(Flat(a), Flat(b), Flat(c), Flat(d));
                if (hit.VariantType != Variant.Type.Vector2)
                    continue;
                var cross = hit.AsVector2();
                var y = (a.Y + b.Y + c.Y + d.Y) / 4f;
                yield return new Vector3(cross.X, y, cross.Y);
            }
        }
    }

    /// <summary>Two loose ends merged into one road, if they are alike across: whether they were.</summary>
    private bool MergeEnds((Road Road, bool AtStart) kept, (Road Road, bool AtStart) merged)
    {
        var atEnd = !kept.AtStart;
        var reversed = atEnd ? !merged.AtStart : merged.AtStart;
        if (!RoadEdits.CanMerge(kept.Road, merged.Road, reversed))
        {
            GD.Print($"Join Roads: {kept.Road.Name} and {merged.Road.Name} are not alike across ({kept.Road.CrossSection} / {merged.Road.CrossSection}): joined at a junction instead.");
            return false;
        }

        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        undo.CreateAction("Join roads into one");
        // Ends on top of each other: the kept road's end point given up for the other's, so there are not two in one place.
        var keptEnd = atEnd ? kept.Road.Points[^1] : kept.Road.Points[0];
        var mergedEnd = merged.AtStart ? merged.Road.Points[0] : merged.Road.Points[^1];
        if (keptEnd.GlobalPosition.DistanceTo(mergedEnd.GlobalPosition) < 0.5f && kept.Road.Points.Count > 2)
        {
            undo.AddDoMethod(kept.Road, Node.MethodName.RemoveChild, keptEnd);
            undo.AddUndoMethod(kept.Road, Node.MethodName.AddChild, keptEnd, true);
            undo.AddUndoMethod(kept.Road, Node.MethodName.MoveChild, keptEnd, keptEnd.GetIndex());
            undo.AddUndoMethod(keptEnd, Node.MethodName.SetOwner, root);
            undo.AddUndoReference(keptEnd);
        }

        var merge = new RoadMerge { Kept = kept.Road, Merged = merged.Road, AtEnd = atEnd, Reversed = reversed };
        undo.AddDoMethod(this, MethodName.DoMerge, merge);
        undo.AddUndoMethod(this, MethodName.UndoMerge, merge);
        undo.AddDoReference(merge);
        undo.CommitAction();
        GD.Print($"Joined {merged.Road.Name} onto {kept.Road.Name}: one road.");
        return true;
    }

    /// <summary>
    /// The roads joined at a junction (the one given, or a new one at the place): each end there
    /// brought onto it, each road passing through split at it.
    /// </summary>
    private void JoinAt(RoadJunction? junction, Vector3 at, List<Meeting> meetings)
    {
        if ((Network() ?? meetings[0].Road.GetParent() as RoadNetwork) is not { } network)
            return;
        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        undo.CreateAction("Join roads at a junction");
        junction ??= NewJunction(undo, network, root, at);
        foreach (var meeting in meetings)
        {
            if (meeting.AtStart is not { } atStart)
            {
                // Through it: split there, both halves arms of the junction.
                var split = new RoadSplit { Road = meeting.Road, Junction = junction, Distance = meeting.Along };
                undo.AddDoMethod(this, MethodName.DoSplit, split);
                undo.AddUndoMethod(this, MethodName.UndoSplit, split);
                undo.AddDoReference(split);
                continue;
            }

            if (EndOn(meeting.Road, atStart) == junction)
                continue;
            var point = atStart ? meeting.Road.Points[0] : meeting.Road.Points[^1];
            undo.AddDoProperty(point, Node3D.PropertyName.GlobalPosition, at);
            undo.AddUndoProperty(point, Node3D.PropertyName.GlobalPosition, point.GlobalPosition);
            var property = atStart ? Road.PropertyName.StartJunction : Road.PropertyName.EndJunction;
            undo.AddDoProperty(meeting.Road, property, junction);
            undo.AddUndoProperty(meeting.Road, property, EndOn(meeting.Road, atStart) ?? default(Variant));
        }

        undo.AddDoMethod(this, MethodName.Select, junction);
        undo.AddUndoMethod(this, MethodName.Select, _selected ?? (Node)meetings[0].Road);
        undo.CommitAction();
        GD.Print($"Joined {string.Join(", ", meetings.Select(m => m.AtStart is null ? $"{m.Road.Name} (split)" : m.Road.Name.ToString()))} at {junction.Name}.");
    }
}
#endif
