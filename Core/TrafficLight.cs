using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// A traffic light (TrafficLight.tscn): a pole with a head of three lamps facing along its +z,
/// towards the traffic it is for. Each lamp has a dim face and a lit one (Head/Red/Lit and so on);
/// the lit one shows for its <see cref="State"/>.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_roads/Icons/Traffic-Light.svg")]
public partial class TrafficLight : Node3D
{
    private Light _state = Light.Red;

    [Export]
    public Light State
    {
        get => _state;
        set
        {
            if (_state == value && _ready)
                return;
            _state = value;
            if (IsInsideTree())
                Refresh();
        }
    }

    /// <summary>Whether it has been made ready (kept here: asking Godot, each time it is set, cost).</summary>
    private bool _ready;

    public override void _Ready()
    {
        _ready = true;
        Refresh();
    }

    private void Refresh()
    {
        foreach (var (lamp, light) in new[] { ("Red", Light.Red), ("Amber", Light.Amber), ("Green", Light.Green) })
        {
            if (GetNodeOrNull<Node3D>($"Head/{lamp}/Lit") is { } lit)
                lit.Visible = _state == light;
        }
    }
}
