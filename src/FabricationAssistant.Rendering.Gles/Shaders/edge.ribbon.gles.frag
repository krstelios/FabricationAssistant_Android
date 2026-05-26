#version 310 es
precision highp float;

uniform vec4 uEdgeColor;

in float vDistancePixels;
in float vHalfWidthPixels;
in float vFeatherPixels;
in float vVisible;
in vec3 vWorldPos;

const int MAX_SECTION_PLANES = 8;
uniform int uSectionPlaneCount;
uniform vec4 uSectionPlanes[MAX_SECTION_PLANES];

out vec4 fragColor;

void main()
{
    if (vVisible < 0.5)
        discard;

    for (int i = 0; i < MAX_SECTION_PLANES; i++)
    {
        if (i >= uSectionPlaneCount)
            break;
        vec4 plane = uSectionPlanes[i];
        if (dot(plane.xyz, vWorldPos) < plane.w)
            discard;
    }

    float coverage = 1.0 - smoothstep(
        vHalfWidthPixels,
        vHalfWidthPixels + max(vFeatherPixels, 0.001),
        abs(vDistancePixels));

    if (coverage <= 0.001)
        discard;

    fragColor = vec4(uEdgeColor.rgb, uEdgeColor.a * coverage);
}
