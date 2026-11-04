using Godot;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>
/// A viaduct or tunnel carrying the road it is a child of, over Length metres from Start along it.
/// Along it the road has no skirts or banks and the terrain is not fitted to it: a viaduct stands
/// on pillars down to the ground, and fitting the terrain opens holes in it where a tunnel's tube
/// would meet it, so the tunnel can be driven into.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_roads/Icons/Road-Structure.svg")]
public partial class RoadStructure : Node3D
{
    private StructureKind _kind = StructureKind.Viaduct;
    private float _start;
    private float _length = 60f;
    private float _deckDepth = 1.2f;
    private float _barrierHeight = 1f;
    private float _barrierWidth = 0.4f;
    private float _pillarSpacing = 30f;
    private float _pillarWidth = 1.5f;
    private float _clearance = 6f;
    private float _wallThickness = 1.5f;
    private float _lightSpacing = 8f;
    private float _lightLength = 1.5f;
    private Color _lightColour = new(1f, 0.62f, 0.25f);
    private float _lightEnergy = 2f;
    private float _lightRange = 14f;

    [Export]
    public StructureKind Kind { get => _kind; set => Change(ref _kind, value); }

    /// <summary>How far along the road it begins.</summary>
    [Export(PropertyHint.Range, "0,10000,0.5,or_greater,suffix:m")]
    public float Start { get => _start; set => Change(ref _start, value); }

    [Export(PropertyHint.Range, "5,2000,0.5,or_greater,suffix:m")]
    public float Length { get => _length; set => Change(ref _length, value); }

    /// <summary>How thick a viaduct's deck is under the road.</summary>
    [ExportGroup("Viaduct")]
    [Export(PropertyHint.Range, "0.3,5,0.05,suffix:m")]
    public float DeckDepth { get => _deckDepth; set => Change(ref _deckDepth, value); }

    [Export(PropertyHint.Range, "0,3,0.05,suffix:m")]
    public float BarrierHeight { get => _barrierHeight; set => Change(ref _barrierHeight, value); }

    [Export(PropertyHint.Range, "0.1,2,0.05,suffix:m")]
    public float BarrierWidth { get => _barrierWidth; set => Change(ref _barrierWidth, value); }

    /// <summary>How far apart its pillars stand along it.</summary>
    [Export(PropertyHint.Range, "5,200,1,suffix:m")]
    public float PillarSpacing { get => _pillarSpacing; set => Change(ref _pillarSpacing, value); }

    [Export(PropertyHint.Range, "0.3,10,0.05,suffix:m")]
    public float PillarWidth { get => _pillarWidth; set => Change(ref _pillarWidth, value); }

    /// <summary>How high a tunnel is inside, from the road to its ceiling.</summary>
    [ExportGroup("Tunnel")]
    [Export(PropertyHint.Range, "3,20,0.1,suffix:m")]
    public float Clearance { get => _clearance; set => Change(ref _clearance, value); }

    [Export(PropertyHint.Range, "0.2,5,0.05,suffix:m")]
    public float WallThickness { get => _wallThickness; set => Change(ref _wallThickness, value); }

    /// <summary>How far apart its light bars are along each side, in metres, by turns on either side; nought for none.</summary>
    [Export(PropertyHint.Range, "0,50,0.5,suffix:m")]
    public float LightSpacing { get => _lightSpacing; set => Change(ref _lightSpacing, value); }

    /// <summary>How long each light bar is.</summary>
    [Export(PropertyHint.Range, "0.3,10,0.1,suffix:m")]
    public float LightLength { get => _lightLength; set => Change(ref _lightLength, value); }

    /// <summary>The colour of its light bars, and of the light they give.</summary>
    [Export(PropertyHint.ColorNoAlpha)]
    public Color LightColour { get => _lightColour; set => Change(ref _lightColour, value); }

    /// <summary>How strong the light from its bars is.</summary>
    [Export(PropertyHint.Range, "0,16,0.1")]
    public float LightEnergy { get => _lightEnergy; set => Change(ref _lightEnergy, value); }

    /// <summary>How far the light from its bars reaches.</summary>
    [Export(PropertyHint.Range, "1,60,0.5,suffix:m")]
    public float LightRange { get => _lightRange; set => Change(ref _lightRange, value); }

    public Road? Road => GetParent() as Road;

    public StructureSpec Spec => new(_kind, _start, _length, _deckDepth, _barrierHeight, _barrierWidth, _pillarSpacing, _pillarWidth, _clearance, _wallThickness, _lightSpacing, _lightLength, _lightColour.R, _lightColour.G, _lightColour.B, _lightEnergy, _lightRange);

    /// <summary>Sits where it begins, so selecting and framing it shows it; called when its road has been built again.</summary>
    public void Refresh(System.Collections.Generic.IReadOnlyList<RoadFrame>? roadFrames = null)
    {
        if (Road is not { } road)
            return;
        var frames = roadFrames ?? road.FullFrames();
        if (frames.Count < 2)
            return;
        var at = RoadCurve.At(frames, _start + _length / 2f).Position.ToGodot();
        if (!GlobalPosition.Near(at))
            GlobalPosition = at;
    }

    private void Change<T>(ref T field, T value)
    {
        field = value;
        Road?.QueueRebuild();
    }
}
