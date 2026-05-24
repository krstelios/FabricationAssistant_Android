#version 310 es
precision highp float;

uniform vec4 uEdgeColor;

in float vDistancePixels;
in float vHalfWidthPixels;
in float vFeatherPixels;
in float vVisible;

out vec4 fragColor;

void main()
{
    if (vVisible < 0.5)
        discard;

    float coverage = 1.0 - smoothstep(
        vHalfWidthPixels,
        vHalfWidthPixels + max(vFeatherPixels, 0.001),
        abs(vDistancePixels));

    if (coverage <= 0.001)
        discard;

    fragColor = vec4(uEdgeColor.rgb, uEdgeColor.a * coverage);
}
