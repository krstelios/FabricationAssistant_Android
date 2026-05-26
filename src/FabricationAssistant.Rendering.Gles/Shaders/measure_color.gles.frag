#version 310 es
precision highp float;

in vec4 vColor;

uniform int uRoundPoints;

out vec4 outColor;

void main()
{
    if (uRoundPoints != 0)
    {
        vec2 d = gl_PointCoord * 2.0 - 1.0;
        float r2 = dot(d, d);
        if (r2 > 1.0)
            discard;
    }

    outColor = vColor;
}
