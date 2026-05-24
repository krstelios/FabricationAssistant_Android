#version 310 es
precision highp float;

in vec3 vWorldPos;

uniform vec3 uCameraPosWorld;
// uGridSpacing.x = minor line spacing in world units; .y = major line every N minors
uniform vec2 uGridSpacing;
uniform float uLineThickness;
// Fade radius in world units (grid fades from 1.0 at 0 to 0 at uFadeRadius)
uniform float uFadeRadius;
// Background color the grid fades into (matches the viewport clear color)
uniform vec3 uBackgroundColor;
uniform vec3 uMinorColor;
uniform vec3 uMajorColor;

out vec4 fragColor;

float gridFactor(vec2 coord, float spacing)
{
    // Distance to nearest gridline in units of spacing.
    vec2 g = abs(fract(coord / spacing - 0.5) - 0.5);
    // Convert to per-pixel screen-space line thickness via fwidth.
    vec2 fw = fwidth(coord) / spacing;
    // 'line' is small near a gridline (0 on the line), large between.
    vec2 line = g / max(fw, vec2(1e-4));
    float minLine = min(line.x, line.y);
    // 1 on the line, 0 between. Clamp so far-away pixels don't aliase.
    float halfThickness = max(uLineThickness * 0.5, 0.5);
    return 1.0 - smoothstep(halfThickness, halfThickness + 1.0, minLine);
}

void main()
{
    // App is Z-up; the floor plane lives in XY and we want grid lines in
    // world XY coordinates.
    vec2 xy = vWorldPos.xy;

    float minor = gridFactor(xy, uGridSpacing.x);
    float major = gridFactor(xy, uGridSpacing.x * uGridSpacing.y);

    // Radial distance fade so the grid blends into the background past
    // the model rather than sharply terminating at the plane edge.
    float dist = length(uCameraPosWorld.xy - xy);
    float fade = 1.0 - clamp(dist / uFadeRadius, 0.0, 1.0);
    fade = fade * fade; // ease-out so the falloff is gentle

    // Major lines win over minor where they overlap.
    vec3 lineColor = mix(uMinorColor, uMajorColor, major);
    float intensity = max(minor, major) * fade;

    // Fade toward the background color so the grid doesn't read as a hard
    // alpha hole. The viewport uses opaque rendering (no blending), so we
    // mix toward background in opaque RGB.
    vec3 color = mix(uBackgroundColor, lineColor, intensity);

    // Drop alpha < ~5% pixels so the depth pre-fill doesn't shadow meshes.
    if (intensity < 0.02) discard;

    fragColor = vec4(color, 1.0);
}
