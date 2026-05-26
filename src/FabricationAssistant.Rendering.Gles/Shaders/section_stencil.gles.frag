#version 310 es
precision highp float;

const int MAX_SECTION_PLANES = 8;

in vec3 vWorldPos;

uniform int uSectionPlaneCount;
uniform vec4 uSectionPlanes[MAX_SECTION_PLANES];

out vec4 outColor;

void main()
{
    for (int i = 0; i < MAX_SECTION_PLANES; ++i)
    {
        if (i < uSectionPlaneCount && dot(uSectionPlanes[i].xyz, vWorldPos) < uSectionPlanes[i].w)
            discard;
    }

    outColor = vec4(1.0);
}
