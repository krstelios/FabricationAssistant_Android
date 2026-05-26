#version 310 es
precision highp float;
precision highp int;

uniform uint uMeshIndex;

in vec3 vWorldPos;

const int MAX_SECTION_PLANES = 8;
uniform int uSectionPlaneCount;
uniform vec4 uSectionPlanes[MAX_SECTION_PLANES];

layout(location = 0) out uint fragId;

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

    fragId = uMeshIndex;
}
