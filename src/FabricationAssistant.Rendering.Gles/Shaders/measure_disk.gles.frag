#version 310 es
precision highp float;

in vec4 vColor;
in vec2 vOffset;

uniform float uFillAlpha;
uniform float uRingAlpha;

out vec4 outColor;

void main()
{
    float dist = length(vOffset);
    if (dist > 1.0)
        discard;

    float ringIn = smoothstep(0.82, 0.88, dist);
    float edgeAA = 1.0 - smoothstep(0.96, 1.00, dist);
    float ring = ringIn * edgeAA;
    float fill = 1.0 - ringIn;
    float alpha = (fill * uFillAlpha + ring * uRingAlpha) * vColor.a;
    outColor = vec4(vColor.rgb, alpha);
}
