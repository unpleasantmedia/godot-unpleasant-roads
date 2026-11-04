// The inside of a tunnel's walls and roof: lit only by what shines on it directly, as bounced
// light (SDFGI) comes through from outside, where its grid is too coarse to tell in from out.
shader_type spatial;
render_mode cull_disabled;

uniform vec3 colour : source_color = vec3(0.725, 0.725, 0.682);
uniform float roughness_amount : hint_range(0.0, 1.0) = 0.9;
// How much bounced light it takes: none at 0.
uniform float bounced_light : hint_range(0.0, 1.0) = 0.0;

void fragment() {
	ALBEDO = colour;
	ROUGHNESS = roughness_amount;
	AO = bounced_light;
	AO_LIGHT_AFFECT = 0.0;
	SPECULAR = 0.15;
}
