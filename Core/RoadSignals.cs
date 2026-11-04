using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace UnpleasantMedia.UnpleasantRoads;

/// <summary>How a junction is controlled: worked out from its roads, by traffic lights, by signs, or not at all.</summary>
public enum JunctionControl
{
    Auto,
    TrafficLights,
    Signs,
    None,
}

/// <summary>What traffic coming in along one road must do at a junction.</summary>
public enum ArmControl
{
    /// <summary>It has priority: it goes, and the others give way to it.</summary>
    Priority,

    /// <summary>It gives way to traffic with priority before going.</summary>
    GiveWay,

    /// <summary>It stops, then gives way, before going.</summary>
    Stop,

    /// <summary>It goes by the traffic lights.</summary>
    Lights,
}

/// <summary>Whether a road has a direction sign before a junction at one of its ends.</summary>
public enum DirectionSign
{
    /// <summary>Where the junction would put one anyway: shown where it has towns to name, outside towns.</summary>
    Auto,

    /// <summary>One, shown inside a town too.</summary>
    On,

    /// <summary>None.</summary>
    Off,
}

/// <summary>A traffic light's colour.</summary>
public enum Light
{
    Green,
    Amber,
    Red,
}

/// <summary>
/// A junction's traffic light plan: its phases in turn, each the arms (by index) with green
/// together, for Green seconds, then Amber seconds of amber, then AllRed seconds of red all
/// round before the next. The plan repeats; Offset shifts where in it a junction starts.
/// </summary>
public sealed record SignalPlan(IReadOnlyList<IReadOnlyList<int>> Phases, float Green, float Amber, float AllRed, float Offset = 0f)
{
    public float PhaseLength => Green + Amber + AllRed;

    public float Cycle => Phases.Count * PhaseLength;

    /// <summary>The colour an arm's light shows this many seconds in.</summary>
    public Light LightFor(int arm, float time)
    {
        if (Phases.Count == 0 || Cycle <= 0f)
            return Light.Green;
        var into = ((time + Offset) % Cycle + Cycle) % Cycle;
        var phase = (int)(into / PhaseLength);
        if (!Phases[Math.Min(phase, Phases.Count - 1)].Contains(arm))
            return Light.Red;
        var within = into - phase * PhaseLength;
        return within < Green ? Light.Green : within < Green + Amber ? Light.Amber : Light.Red;
    }
}

/// <summary>
/// Works out how a junction is controlled, what its traffic lights do, and where its signs and
/// lights stand.
/// </summary>
public static class RoadSignals
{
    /// <summary>How far out past a road's edge a sign or light stands, in metres.</summary>
    public const float Kerb = 1f;

    /// <summary>Where a sign stands on a viaduct or in a tunnel: on the barrier's (or wall's) line, rather than out over the edge or in the lane.</summary>
    public const float CarriedKerb = -0.2f;

    /// <summary>How far the ground beside a road may drop below it before a sign stands on the road's edge rather than out on the ground, in metres.</summary>
    public const float BigDrop = 0.5f;

    /// <summary>
    /// How far past a road's edge a sign stands at a place along it: as asked, unless the ground
    /// there drops well below the road (an embankment, say), when it stands on the road's (or its
    /// sidewalk's) edge instead, as on a viaduct.
    /// </summary>
    /// <param name="edge">The edge's offset across the road.</param>
    /// <param name="outward">Which way across is out past it: 1 to the right, -1 to the left.</param>
    public static float KerbBeside(IRoadGround? ground, RoadFrame frame, float edge, float outward, float kerb)
    {
        if (ground is null || kerb <= 0f)
            return kerb;
        var at = frame.Across(edge + outward * kerb);
        var height = ground.HeightAt(new Godot.Vector3(at.X, at.Y, at.Z));
        return !float.IsNaN(height) && height < at.Y - BigDrop ? CarriedKerb : kerb;
    }

    /// <summary>
    /// What traffic coming in along each arm must do. Auto: traffic lights where four or more roads
    /// meet; where three do, the main road (the most important arm, by lanes then speed, and the
    /// most important more or less straight across from it) has priority and the rest give way;
    /// two roads or a cul-de-sac need nothing. Signs is as Auto's three-road case for any number of roads.
    /// </summary>
    public static IReadOnlyList<ArmControl> Controls(IReadOnlyList<JunctionArm> arms, JunctionControl control, IReadOnlyList<float> speeds)
    {
        if (control == JunctionControl.None || arms.Count <= 2)
            return arms.Select(_ => ArmControl.Priority).ToList();
        if (control == JunctionControl.TrafficLights || (control == JunctionControl.Auto && arms.Count >= 4))
            return arms.Select(_ => ArmControl.Lights).ToList();

        // The main road: the most important arm (the most lanes, then the fastest), and the most
        // important of those more or less straight across from it, if any. Traffic off a one-lane
        // ramp road gives way to the road it meets.
        float Importance(int arm) => arms[arm].Section.Lanes * 1000f + speeds[arm];
        var first = Enumerable.Range(0, arms.Count).OrderByDescending(Importance).First();
        var across = Enumerable.Range(0, arms.Count)
            .Where(b => b != first && -Vector3.Dot(Flat(arms[first].Direction), Flat(arms[b].Direction)) > 0.3f)
            .OrderByDescending(Importance).Cast<int?>().FirstOrDefault();
        return arms.Select((_, i) => i == first || i == across ? ArmControl.Priority : ArmControl.GiveWay).ToList();
    }

    /// <summary>
    /// A light plan for a junction's arms: arms coming in from straight across each other go
    /// together, the rest each on their own; arms nothing comes in along have no phase.
    /// </summary>
    public static SignalPlan Plan(IReadOnlyList<JunctionArm> arms, float green, float amber, float allRed, float offset = 0f)
    {
        var phases = new List<IReadOnlyList<int>>();
        var placed = new HashSet<int>();
        for (var a = 0; a < arms.Count; a++)
        {
            if (placed.Contains(a) || !HasIncoming(arms[a]))
                continue;
            var phase = new List<int> { a };
            placed.Add(a);
            for (var b = a + 1; b < arms.Count; b++)
            {
                if (!placed.Contains(b) && HasIncoming(arms[b]) && -Vector3.Dot(Flat(arms[a].Direction), Flat(arms[b].Direction)) > 0.7f)
                {
                    phase.Add(b);
                    placed.Add(b);
                    break;
                }
            }

            phases.Add(phase);
        }

        return new SignalPlan(phases, green, amber, allRed, offset);
    }

    /// <summary>Whether any traffic comes in to the junction along an arm.</summary>
    public static bool HasIncoming(JunctionArm arm) => arm.OutwardBands().Any(b => b.Incoming);

    /// <summary>
    /// Where the sign or light for traffic coming in along an arm stands: at its road's cut end,
    /// on that traffic's right, just past the road's edge; and the way that traffic travels (the
    /// sign faces back against it, towards the drivers).
    /// </summary>
    /// <remarks>Kerb is how far past the road's edge: less than nothing stands it on the road's shoulder (on a viaduct, say).</remarks>
    public static (Vector3 At, Vector3 Travelling) Place(RoadFrame end, JunctionArm arm, float kerb = Kerb, IRoadGround? ground = null)
    {
        // Looking out, incoming traffic's right is the road's left.
        // Next to its sidewalk, if it has one, not on it; on its edge over a drop.
        var (left, _) = arm.OuterEdges();
        kerb = KerbBeside(ground, end, left, -1f, kerb);
        return (end.Across(left - kerb), -Flat(end.Forward));
    }

    /// <summary>
    /// Which way a road off a junction lies for traffic coming in another way, as a direction
    /// sign shows it: "^" straight on, "<" to the left, ">" to the right.
    /// </summary>
    public static string Arrow(Vector3 travelling, Vector3 leaving)
    {
        var (t, l) = (Flat(travelling), Flat(leaving));
        if (Vector3.Dot(t, l) > 0.7f)
            return "^";
        // Seen from above (x, z) with traffic keeping right: a left turn swings from -z towards -x.
        var cross = t.X * l.Z - t.Z * l.X;
        return cross < 0f ? "<" : ">";
    }

    /// <summary>A distance as a sign shows it: metres rounded to tens under a kilometre, kilometres to a tenth above.</summary>
    public static string Distance(float metres) =>
        metres < 1000f
            ? $"{MathF.Max(10f, MathF.Round(metres / 10f, MidpointRounding.AwayFromZero) * 10f):0} m"
            : $"{MathF.Round(metres / 100f, MidpointRounding.AwayFromZero) / 10f:0.0} km".Replace(',', '.');

    /// <summary>How many places a direction sign names at most.</summary>
    public const int BoardLines = 4;

    /// <summary>
    /// A direction sign's lines naming places: each under the arrow of the way that reaches it
    /// soonest, with how far it is to drive there; the most important first, then the nearest,
    /// no more than fit; set out straight on, then left, then right.
    /// </summary>
    public static string PlacesText(IEnumerable<(string Name, int Importance, string Arrow, float Distance)> places)
    {
        var chosen = places.OrderByDescending(p => p.Importance).ThenBy(p => p.Distance).Take(BoardLines);
        return string.Join("\n", chosen
            .OrderBy(p => p.Arrow == "^" ? 0 : p.Arrow == "<" ? 1 : 2).ThenBy(p => p.Distance)
            .Select(p => $"{p.Arrow} {p.Name}  {Distance(p.Distance)}"));
    }

    private static Vector3 Flat(Vector3 v)
    {
        var flat = new Vector3(v.X, 0f, v.Z);
        return flat.LengthSquared() > 1e-8f ? Vector3.Normalize(flat) : Vector3.UnitZ;
    }
}
