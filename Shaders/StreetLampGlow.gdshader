// A street lamp's glowing underside: lit while the lamps are on.
shader_type spatial;
render_mode cull_disabled;

// How far the lamps are on, from 0 to 1: set by the road network (see RoadLinks.LampLevel).
uniform float street_lamps : hint_range(0.0, 1.0) = 1.0;

uniform vec3 colour : source_color = vec3(1.0, 0.987, 0.964);
uniform float energy : hint_range(0.0, 16.0) = 3.0;

void fragment() {
	ALBEDO = mix(vec3(0.25, 0.25, 0.24), colour, street_lamps);
	ROUGHNESS = 0.6;
	EMISSION = colour * energy * street_lamps;
}
