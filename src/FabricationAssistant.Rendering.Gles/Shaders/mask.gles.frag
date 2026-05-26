#version 310 es
precision highp float;

in vec3 vWorldPos;

const int MAX_SECTION_PLANES = 8;
uniform int uSectionPlaneCount;
uniform vec4 uSectionPlanes[MAX_SECTION_PLANES];

out float fragMask;

void main()
{
    for (int i = 0; i < MAX_SECTION_PLANES; i++)
    {
        if (i >= uSectionPlaneCount)
            break;
        vec4 plane = uSectionPlanes[i];
        if (dot(plane.xyz, vWorldPos) < plane.w)
            discard;
    }

    fragMask = 1.0;
}
