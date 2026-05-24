#version 310 es
precision highp float;
precision highp int;

// Direct port of src/FabricationAssistant.Rendering.OpenTK/Shaders/ssao_blur.frag.glsl
// Bilateral separable Gaussian: 1D Gaussian weights (uGaussianWeights[25])
// modulated by per-sample depth + normal continuity so the blur respects
// edges in the AO buffer.

in vec2 vTexCoord;

uniform sampler2D uAoTexture;
uniform sampler2D uNormalTexture;
uniform sampler2D uDepthTexture;
uniform vec2 uTexelSize;
uniform vec2 uDirection;
uniform int uRadius;
uniform float uSharpness;
uniform float uGaussianWeights[25];

layout(location = 0) out float FragColor;

vec3 DecodeNormal(vec2 encodedValue)
{
    vec2 f = encodedValue * 2.0 - 1.0;
    vec3 normal = vec3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = max(-normal.z, 0.0);
    normal.xy += vec2(normal.x >= 0.0 ? -t : t, normal.y >= 0.0 ? -t : t);
    return normalize(normal);
}

void main()
{
    vec2 centerPacked = texture(uNormalTexture, vTexCoord).rg;
    float centerDepth = texture(uDepthTexture, vTexCoord).r;
    if (centerDepth >= 0.999999)
    {
        FragColor = 1.0;
        return;
    }

    vec3 centerNormal = DecodeNormal(centerPacked);
    float total = 0.0;
    float weightSum = 0.0;

    for (int offset = -uRadius; offset <= uRadius; ++offset)
    {
        vec2 uv = clamp(vTexCoord + uDirection * uTexelSize * float(offset), vec2(0.0), vec2(1.0));
        vec2 samplePacked = texture(uNormalTexture, uv).rg;
        float sampleDepth = texture(uDepthTexture, uv).r;
        if (sampleDepth >= 0.999999) continue;

        vec3 sampleNormal = DecodeNormal(samplePacked);
        float gaussian = uGaussianWeights[abs(offset)];
        float depthWeight = exp(-abs(sampleDepth - centerDepth) * max(uSharpness, 0.0) * 250.0);
        float normalWeight = pow(max(dot(centerNormal, sampleNormal), 0.0), max(1.0, uSharpness * 0.5));
        float weight = gaussian * depthWeight * normalWeight;
        total += texture(uAoTexture, uv).r * weight;
        weightSum += weight;
    }

    FragColor = weightSum > 0.000001 ? total / weightSum : texture(uAoTexture, vTexCoord).r;
}
