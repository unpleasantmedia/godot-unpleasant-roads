using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>Something with an outline drawn in the editor (a town, a district, a plot): shown again as the selection changes.</summary>
public interface IRoadOutlined
{
    /// <summary>Its outline shown or hidden, as what is selected now says.</summary>
    void ShowOutline();
}

/// <summary>
/// When the editor shows an outline: only while what it belongs to is being worked on (it, something
/// under it, or the node holding all of its kind), so the viewport is not covered in them all the time.
/// Never in the game.
/// </summary>
public static class RoadOutlines
{
    /// <summary>The group everything with an outline is in (see IRoadOutlined), to show again when the selection changes.</summary>
    public const string Group = "road_outlined";

    /// <summary>
    /// Whether an outline shows: in the editor, while its owner is selected, or something under it (or
    /// under just its part given, a town's Shape, say), or the node above it named as the holder of its kind.
    /// </summary>
    public static bool Shown(Node owner, string? holder = null, Node? only = null)
    {
#if TOOLS
        if (!Engine.IsEditorHint() || !owner.IsInsideTree())
            return false;
        var within = only ?? owner;
        foreach (var selected in EditorInterface.Singleton.GetSelection().GetSelectedNodes())
        {
            if (selected == within || within.IsAncestorOf(selected))
                return true;
            if (holder is not null && selected == owner.GetParent() && string.Equals(selected.Name, holder, System.StringComparison.OrdinalIgnoreCase))
                return true;
        }
#endif
        return false;
    }

    /// <summary>
    /// Whether something of a kind is being worked on: one selected, or something under one, or the
    /// node holding them (by its name).
    /// </summary>
    public static bool Editing<T>(string holder) where T : Node
    {
#if TOOLS
        if (!Engine.IsEditorHint())
            return false;
        foreach (var selected in EditorInterface.Singleton.GetSelection().GetSelectedNodes())
        {
            if (string.Equals(selected.Name, holder, System.StringComparison.OrdinalIgnoreCase))
                return true;
            for (Node? at = selected; at is not null; at = at.GetParent())
            {
                if (at is T)
                    return true;
            }
        }
#endif
        return false;
    }

    /// <summary>Every outline shown or hidden again, for what is selected now.</summary>
    public static void Refresh(SceneTree tree)
    {
        foreach (var node in tree.GetNodesInGroup(Group))
        {
            if (node is IRoadOutlined outlined && GodotObject.IsInstanceValid(node))
                outlined.ShowOutline();
        }
    }
}
