using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// A road sign: a pole with a face on it (Sign.tscn, and a scene inheriting it for each kind of
/// sign). Its face looks along its +z, towards the traffic it is for. Signs showing words or a
/// number (a speed limit, a street name) show its <see cref="Text"/> on their Face/Label.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_roads/Icons/Road-Sign.svg")]
public partial class RoadSign : Node3D
{
    private string _text = "";
    private int _fontSize;

    /// <summary>
    /// How many letters its face takes at the size its scene sets: longer text is set smaller.
    /// Two for a round sign's number; more for a plate.
    /// </summary>
    [Export(PropertyHint.Range, "1,40,1")]
    public int FitsLetters { get; set; } = 2;

    [Export]
    public string Text
    {
        get => _text;
        set
        {
            _text = value;
            if (IsInsideTree())
                Refresh();
        }
    }

    public override void _Ready() => Refresh();

    private void Refresh()
    {
        if (GetNodeOrNull<Label3D>("Face/Label") is not { } label || string.IsNullOrEmpty(_text))
            return;
        // Its size as the scene sets it, the first time: longer words or numbers are set smaller, to fit the same face.
        if (_fontSize == 0)
            _fontSize = label.FontSize;
        label.Text = _text;
        // By its longest line: a sign of several lines has each on its own.
        var longest = System.Linq.Enumerable.Max(label.Text.Split('\n'), line => line.Length);
        var fits = longest <= FitsLetters ? 1f : FitsLetters / (float)longest;
        label.FontSize = Mathf.Max(8, Mathf.RoundToInt(_fontSize * fits));
    }
}
