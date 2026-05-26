#version 310 es
precision highp float;
precision highp int;

// GLES port of src/FabricationAssistant.Rendering.OpenTK/Shaders/ssao_blur.frag.glsl
// Bilateral separable Gaussian: 1D Gaussian weights (uGaussianWeights[25])
// modulated by per-sample depth + normal continuity so the blur respects
// edges in the AO buffer. Perspective views sample the depth attachment like
// desktop; packed linear depth remains the orthographic fallback.

in vec2 vTexCoord;

uniform sampler2D uAoTexture;
uniform sampler2D uNormalTexture;
uniform sampler2D uDepthTexture;
uniform vec2 uTexelSize;
uniform vec2 uDirection;
uniform int uRadius;
uniform float uSharpness;
uniform float uGaussianWeights[25];
uniform bool uIsPerspective;

layout(location = 0) out vec4 FragColor;

vec3 DecodeNormal(vec2 encodedValue)
{
    vec2 f = encodedValue * 2.0 - 1.0;
    vec3 normal = vec3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = max(-normal.z, 0.0);
    normal.xy += vec2(normal.x >= 0.0 ? -t : t, normal.y >= 0.0 ? -t : t);
    return normalize(normal);
}

float DecodeDepth01(vec2 packedDepth)
{
    return clamp(packedDepth.x + packedDepth.y / 255.0, 0.0, 1.0);
}

void main()
{
    vec4 centerData = texture(uNormalTexture, vTexCoord);
    vec2 centerPacked = centerData.rg;
    float centerDepth = uIsPerspective ? texture(uDepthTexture, vTexCoord).r : DecodeDepth01(centerData.ba);
    if (centerDepth >= 0.999999)
    {
        FragColor = vec4(1.0);
        return;
    }

    vec3 centerNormal = DecodeNormal(centerPacked);
    float total = 0.0;
    float weightSum = 0.0;

    for (int offset = -uRadius; offset <= uRadius; ++offset)
    {
        vec2 uv = clamp(vTexCoord + uDirection * uTexelSize * float(offset), vec2(0.0), vec2(1.0));
        vec4 sampleData = texture(uNormalTexture, uv);
        vec2 samplePacked = sampleData.rg;
        float sampleDepth = uIsPerspective ? texture(uDepthTexture, uv).r : DecodeDepth01(sampleData.ba);
        if (sampleDepth >= 0.999999) continue;

        vec3 sampleNormal = DecodeNormal(samplePacked);
        float gaussian = uGaussianWeights[abs(offset)];
        float depthWeight = exp(-abs(sampleDepth - centerDepth) * max(uSharpness, 0.0) * 250.0);
        float normalWeight = pow(max(dot(centerNormal, sampleNormal), 0.0), max(1.0, uSharpness * 0.5));
        float weight = gaussian * depthWeight * normalWeight;
        total += texture(uAoTexture, uv).r * weight;
        weightSum += weight;
    }

    float ao = weightSum > 0.000001 ? total / weightSum : texture(uAoTexture, vTexCoord).r;
    FragColor = vec4(vec3(ao), 1.0);
}
