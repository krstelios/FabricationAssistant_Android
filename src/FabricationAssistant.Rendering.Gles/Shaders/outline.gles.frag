#version 310 es
precision highp float;

in vec2 vTexCoord;

uniform sampler2D uMask;
uniform vec2 uTexelSize;
uniform vec3 uOutlineColor;
uniform float uThicknessPx;

out vec4 fragColor;

float sampleMask(vec2 uv)
{
    return texture(uMask, uv).r;
}

void main()
{
    // 3x3 Sobel kernel scaled by the desired outline thickness. The kernel
    // probes outside / inside the selection mask; |gradient| > 0 means we
    // are on the silhouette.
    float t = max(uThicknessPx, 1.0);
    vec2 step = uTexelSize * t;

    float c = sampleMask(vTexCoord);

    // 4-tap cardinal Sobel keeps it cheap on tablet GPUs - corners pull
    // in the diagonals via the gradient magnitude.
    float l = sampleMask(vTexCoord + vec2(-step.x, 0.0));
    float r = sampleMask(vTexCoord + vec2( step.x, 0.0));
    float u = sampleMask(vTexCoord + vec2(0.0,  step.y));
    float d = sampleMask(vTexCoord + vec2(0.0, -step.y));

    float gx = r - l;
    float gy = u - d;
    float edge = clamp(sqrt(gx * gx + gy * gy) * 1.5, 0.0, 1.0);

    // Bias toward the outside of the mask so the outline doesn't eat into
    // the selected body - prefer pixels where the centre is darker than the
    // average of the neighbours.
    float centerVsNeighbours = clamp(((l + r + u + d) * 0.25) - c, 0.0, 1.0);
    edge *= 0.5 + centerVsNeighbours * 0.5;

    fragColor = vec4(uOutlineColor, edge);
}
