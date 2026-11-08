# UnpleasantRoads

A road plugin for Godot 4.7 (.NET version). Draw roads on the ground in the 3D view; they're built as meshes with lines, banks and sidewalks, joined at junctions, and fitted to the ground.

- `RoadNetwork`: holds the roads, and bakes them into a few meshes for the game
- `Road` and `RoadPoint`: a road along its points, with its lanes, lines, sidewalks, guard rails, median barrier and street lamps
- `RoadJunction`: where roads meet, with traffic lights, stop or give-way signs per arm
- `RoadRamp` and `RoadFork`: slip roads that run alongside a road and peel away, and roads that split in two
- `RoadStructure`: a viaduct or tunnel along a stretch of road, with pillars or light bars
- `RoadSign`, `TrafficLight`, `HighwayGantry`: signs and signals along roads
- `ParkingLot`, `RoadParking`, `Square`: parking lots, parking bays along roads, and paved squares
- `RoadBarrierGap`: an opening in a road's guard rail or median barrier
- Lanes for traffic, with turns through junctions and ramps
- Every edit is one step to undo

## Installing

The plugin must end up at exactly `addons/unpleasant_roads`. As a submodule, use the `main` branch, which has the plugin's files at its root:

```bash
git submodule add https://github.com/unpleasantmedia/godot-unpleasant-roads.git addons/unpleasant_roads
```

Then build the C# project (the hammer at the top right) and enable **UnpleasantRoads** under **Project > Project Settings > Plugins**.

## Drawing roads

Add a `RoadNetwork` and select it. In the toolbar, pick what new roads are like across (lanes, sidewalks) and press **Draw Roads**. Click the ground to add points, drag a point to move it, and ctrl-click one to delete it. A right-click or **Esc** finishes the road.

**Show Lanes** shows the lanes traffic drives, with arrows the way it goes: roads' lanes blue, turns through junctions orange, ramps pink.

The **Project > Tools** menu has **Tidy Junctions**, **Remove Selected Junctions**, **Centre Road Origins**, **Add Road to Selected Ramp**, **Add Road to Selected Fork**, **Swap Selected Fork's Branches** and **Find Ground Through Roads**.

## Viaducts and tunnels

Add a `RoadStructure` under a road, set its **Kind**, and its **Start** and **Length** along the road. A viaduct stands on pillars every **Pillar Spacing** metres, down to the ground. A tunnel is **Clearance** high inside and opens the ground along it. Its light bars hang every **Light Spacing** metres, in the corners of its roof by turns (**Light Row**: Sides), in one row along the middle of its ceiling (Centre), or both, with their own **Light Colour**, **Energy** and **Range**.

## Baking

A network drawn as it's built is many nodes and meshes: fine to edit, slow in a big level. **Bake Roads**, in a `RoadNetwork`'s Inspector, merges its look into a mesh per square of ground, saved as files, beside a `BakedRoadsNode`. In the game, the network's own meshes are hidden and let go of, and the baked ones drawn instead. Small things (signs, lamp posts, guard rail posts) are kept in their own squares, drawn only from nearby. After the first bake, only squares whose roads changed are baked again. If the roads change after baking, the game shows them unbaked (slower) and warns you to bake again.

## Connecting your project

The plugin knows no terrain, barriers or time of day itself. Connect what your project has once, for example from a module initializer:

```csharp
RoadLinks.Ground = node => MyGroundFor(node);         // an IRoadGround: roads fit to it and flatten it under them
RoadLinks.MakeBarrier = (name, kind, flip, points) => MyFence(name, kind, flip, points); // guard rails and median barriers
RoadLinks.LampLevel = () => MyLampLevel();            // how far street lamps are on, 0 to 1
RoadLinks.BakedFolder = "res://Assets/Meshes/Roads/Baked";
```

Without a ground, roads keep the heights they're drawn at; without barriers, roads have none.

## Draw distances

Small things (signs, traffic lights, lamp posts) are drawn up to `RoadDrawDistance.Small`, and baked squares as far as the level. A game can set `RoadDrawDistance.ViewDistance` and `RoadDrawDistance.LevelViewDistance` from its settings.
