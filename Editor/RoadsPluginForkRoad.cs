#if TOOLS
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantRoads.Editor;

public partial class RoadsPlugin
{
    private const string ForkRoadItem = "Add Road to Selected Fork";

    private const string SwapBranchesItem = "Swap Selected Fork's Branches";

    /// <summary>
    /// For each selected fork or merge, its two branches swapped over: each one's end on it put where the other's was, so
    /// it takes them the other way round (the left one on the right). For one that took them the wrong way round, its
    /// branches' ends crossing. One step to undo.
    /// </summary>
    private void SwapSelectedForkBranches()
    {
        var forks = EditorInterface.Singleton.GetSelection().GetSelectedNodes().OfType<RoadFork>().ToList();
        if (forks.Count == 0)
        {
            GD.PushWarning($"{SwapBranchesItem}: select the fork or merge first.");
            return;
        }

        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        undo.CreateAction(SwapBranchesItem);
        foreach (var fork in forks)
        {
            if (fork.Trunk() is not { } trunk || fork.Network is not { } network)
            {
                GD.PushWarning($"{SwapBranchesItem}: {fork.Name} doesn't have its trunk and both branches.");
                continue;
            }

            // Each branch's end on it: a split's branches start there, a merge's end there.
            var ends = network.Roads.Where(r => r != trunk.Road && (trunk.Splits ? r.StartJunction == fork : r.EndJunction == fork))
                .Select(r => r.Points is { Count: > 0 } p ? (trunk.Splits ? p[0] : p[^1]) : null).OfType<RoadPoint>().ToList();
            if (ends.Count != 2)
                continue;
            var (a, b) = (ends[0], ends[1]);
            undo.AddDoProperty(a, Node3D.PropertyName.GlobalPosition, b.GlobalPosition);
            undo.AddDoProperty(b, Node3D.PropertyName.GlobalPosition, a.GlobalPosition);
            undo.AddUndoProperty(a, Node3D.PropertyName.GlobalPosition, a.GlobalPosition);
            undo.AddUndoProperty(b, Node3D.PropertyName.GlobalPosition, b.GlobalPosition);
            undo.AddDoMethod(fork, RoadFork.MethodName.QueueRebuild);
            undo.AddUndoMethod(fork, RoadFork.MethodName.QueueRebuild);
            GD.Print($"{SwapBranchesItem}: swapped {fork.Name}'s branches over.");
        }

        undo.CommitAction();
    }

    /// <summary>
    /// For each selected fork or merge missing one of its three roads (deleted): that road made again, one end on the
    /// fork (which puts it in place) and the other 30 m out, to drag or draw on. Its trunk, if that is what is missing
    /// (behind a fork, on past a merge); otherwise a branch, on the side where there is none. Made like the road still
    /// there. One step to undo.
    /// </summary>
    private void AddRoadsToSelectedForks()
    {
        var forks = EditorInterface.Singleton.GetSelection().GetSelectedNodes().OfType<RoadFork>().ToList();
        if (forks.Count == 0)
        {
            GD.PushWarning($"{ForkRoadItem}: select the fork or merge first.");
            return;
        }

        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        var undo = EditorInterface.Singleton.GetEditorUndoRedo();
        undo.CreateAction(ForkRoadItem);
        RoadPoint? free = null;
        foreach (var fork in forks)
        {
            if (fork.Network is not { } network)
                continue;
            var ending = network.Roads.Where(r => r.EndJunction == fork).ToList();
            var starting = network.Roads.Where(r => r.StartJunction == fork).ToList();
            if (ending.Count + starting.Count >= 3)
            {
                GD.Print($"{fork.Name} has all three of its roads: left as it is.");
                continue;
            }

            if (ending.Count + starting.Count == 0)
            {
                GD.PushWarning($"{ForkRoadItem}: {fork.Name} has no roads left to tell its way from; delete it and make it again.");
                continue;
            }

            // Which it is: a merge's branches end at it and its trunk starts there; a fork's the other way about. Otherwise its
            // name says (as the tools name it).
            var merge = ending.Count == 2 || (starting.Count != 2 && fork.Name.ToString().StartsWith("Merge"));
            var hasTrunk = merge ? starting.Count == 1 : ending.Count == 1;
            var like = (merge ? starting : ending).FirstOrDefault() ?? ending.Concat(starting).First();
            var at = fork.GlobalPosition;
            // The way along it, from its trunk out to where its branches part (as it faces).
            var way = (-fork.GlobalBasis.Z with { Y = 0f }).Normalized();
            var right = new Vector3(-way.Z, 0f, way.X);

            var road = NewRoad();
            RoadFork.Adopt(road, like);
            road.BackwardLanes = 0;
            Vector3[] points;
            if (!hasTrunk)
            {
                // The trunk: behind a fork (running into it), on past a merge (running out of it); as many lanes as both branches.
                road.Name = merge ? "MergeTrunk" : "ForkTrunk";
                road.ForwardLanes = Mathf.Max(ending.Concat(starting).Sum(r => r.ForwardLanes + r.BackwardLanes), 2);
                var back = at - way * BranchOn;
                points = merge ? [at, back] : [back, at];
            }
            else
            {
                // A branch, on the side with none: from the fork's far end (where the fork puts it) out, or in to it.
                var other = (merge ? ending : starting).FirstOrDefault();
                var side = other?.Points is { Count: > 0 } p && (p[merge ? 0 : ^1].GlobalPosition - at).Dot(right) > 0f ? -1f : 1f;
                road.Name = merge ? "MergeRoad" : "ForkRoad";
                road.ForwardLanes = 1;
                var far = at + way * (fork.Length + BranchOn) + right * side * (like.CrossSection.Width / 2f + 2f);
                points = merge ? [far, at] : [at, far];
            }

            undo.AddDoMethod(network, Node.MethodName.AddChild, road, true);
            undo.AddDoMethod(road, Node.MethodName.SetOwner, root);
            undo.AddDoReference(road);
            undo.AddUndoMethod(network, Node.MethodName.RemoveChild, road);
            for (var i = 0; i < points.Length; i++)
            {
                var point = new RoadPoint { Name = "RoadPoint" };
                undo.AddDoMethod(road, Node.MethodName.AddChild, point, true);
                undo.AddDoMethod(point, Node.MethodName.SetOwner, root);
                undo.AddDoProperty(point, Node3D.PropertyName.GlobalPosition, points[i]);
                undo.AddDoReference(point);
                // Its free end, to drag or draw on from: the one off the fork.
                var onFork = hasTrunk ? (merge ? i == points.Length - 1 : i == 0) : (merge ? i == 0 : i == points.Length - 1);
                if (!onFork)
                    free = point;
            }

            // Its end on the fork: a trunk ends at a fork and starts at a merge; a branch the other way about.
            var endsHere = hasTrunk ? merge : !merge;
            undo.AddDoProperty(road, endsHere ? Road.PropertyName.EndJunction : Road.PropertyName.StartJunction, fork);
            undo.AddDoMethod(fork, RoadFork.MethodName.QueueRebuild);
            GD.Print($"{ForkRoadItem}: gave {fork.Name} a new {(hasTrunk ? "branch" : "trunk")}, {road.Name}.");
        }

        if (free is not null)
            undo.AddDoMethod(this, MethodName.Select, free);
        if (_selected is { } selected)
            undo.AddUndoMethod(this, MethodName.Select, selected);
        undo.CommitAction();
        _finished = false;
    }
}
#endif
