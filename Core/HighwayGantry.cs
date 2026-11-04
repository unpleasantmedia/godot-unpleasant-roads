using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// An overhead sign gantry across a road (HighwayGantry.tscn): a post either side, a beam across
/// between them, and panels hung from it, facing along its +z towards the traffic it is for, its
/// +x to that traffic's right. <see cref="Span"/> is how far apart the posts stand; each panel
/// hangs at its offset across from the middle and shows its text, or is left off with none.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_roads/Icons/Highway-Gantry.svg")]
public partial class HighwayGantry : Node3D
{
    private float _span = 20f;
    private string _mainText = "";
    private float _mainAt;
    private string _exitText = "";
    private float _exitAt;

    [Export(PropertyHint.Range, "4,60,0.1,suffix:m")]
    public float Span { get => _span; set { _span = value; Refresh(); } }

    /// <summary>The main panel's text: the road's own name, say, over the lanes going on.</summary>
    [Export(PropertyHint.MultilineText)]
    public string MainText { get => _mainText; set { _mainText = value; Refresh(); } }

    [Export(PropertyHint.Range, "-30,30,0.1,suffix:m")]
    public float MainAt { get => _mainAt; set { _mainAt = value; Refresh(); } }

    /// <summary>The exit panel's text, over the lane that leads off.</summary>
    [Export(PropertyHint.MultilineText)]
    public string ExitText { get => _exitText; set { _exitText = value; Refresh(); } }

    [Export(PropertyHint.Range, "-30,30,0.1,suffix:m")]
    public float ExitAt { get => _exitAt; set { _exitAt = value; Refresh(); } }

    public override void _Ready() => Refresh();

    private void Refresh()
    {
        if (!IsInsideTree())
            return;
        if (GetNodeOrNull<Node3D>("LeftPost") is { } left)
            left.Position = left.Position with { X = -_span / 2f };
        if (GetNodeOrNull<Node3D>("RightPost") is { } right)
            right.Position = right.Position with { X = _span / 2f };
        if (GetNodeOrNull<Node3D>("Beam") is { } beam)
            beam.Scale = beam.Scale with { X = _span };
        Panel("MainPanel", _mainText, _mainAt);
        Panel("ExitPanel", _exitText, _exitAt);
    }

    private void Panel(string name, string text, float at)
    {
        if (GetNodeOrNull<Node3D>(name) is not { } panel)
            return;
        panel.Visible = !string.IsNullOrWhiteSpace(text);
        panel.Position = panel.Position with { X = at };
        if (panel.GetNodeOrNull<Label3D>("Label") is { } label)
            label.Text = text;
    }
}
