#version 310 es
precision highp float;
precision highp int;

// Direct port of src/FabricationAssistant.Rendering.OpenTK/Shaders/ssao.frag.glsl
// with #version downgraded to 310 es and the precision qualifiers added.
// Sampling, plane-weight + range-weight + bias + intensity + power + contrast
// pipeline match the desktop exactly.

in vec2 vTexCoord;

uniform sampler2D uNormalTexture;
uniform sampler2D uNoiseTexture;
uniform sampler2D uDepthTexture;
uniform vec2 uProjectionOffset;
uniform vec2 uProjectionDepth;       // (proj[2][2], proj[2][3]) of perspective
uniform vec2 uInvProjectionScale;    // (1/scaleX, 1/scaleY)
uniform vec2 uProjectionUvScale;     // (-scaleX/2, -scaleY/2) for forward proj
uniform vec2 uProjectionUvBias;      // (0.5 - offsetX/2, 0.5 - offsetY/2)
uniform vec2 uNoiseScaleClamped;     // viewport / 4 (tile the 4x4 noise)
uniform vec4 uSamples[96];
uniform int uSampleCount;
uniform float uRadius;
uniform float uBias;
uniform float uPlaneWeightRange;
uniform float uIntensity;
uniform float uPower;
uniform float uContrast;
uniform float uMaxDistance;
uniform float uFadeStart;
uniform float uFadeEnd;

layout(location = 0) out float FragColor;

vec3 DecodeNormal(vec2 encodedValue)
{
    vec2 f = encodedValue * 2.0 - 1.0;
    vec3 normal = vec3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = max(-normal.z, 0.0);
    normal.xy += vec2(normal.x >= 0.0 ? -t : t, normal.y >= 0.0 ? -t : t);
    return normalize(normal);
}

vec3 ReconstructViewPosition(vec2 uv, float depth)
{
    vec2 ndc = uv * 2.0 - 1.0;
    float ndcZ = depth * 2.0 - 1.0;
    float denominator = -(ndcZ + uProjectionDepth.x);
    float viewZ = abs(denominator) > 0.000001
        ? uProjectionDepth.y / denominator
        : uProjectionDepth.y / 0.000001;
    float viewX = -(ndc.x + uProjectionOffset.x) * viewZ * uInvProjectionScale.x;
    float viewY = -(ndc.y + uProjectionOffset.y) * viewZ * uInvProjectionScale.y;
    return vec3(viewX, viewY, viewZ);
}

vec2 ProjectViewPositionToUv(vec3 viewPos)
{
    float invViewZ = 1.0 / min(viewPos.z, -0.000001);
    return uProjectionUvScale * (viewPos.xy * invViewZ) + uProjectionUvBias;
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
    vec3 centerViewPos = ReconstructViewPosition(vTexCoord, centerDepth);
    float viewDistance = max(-centerViewPos.z, 0.0);
    float distanceFade = 1.0;
    if (uFadeEnd > uFadeStart)
    {
        distanceFade = 1.0 - smoothstep(uFadeStart, uFadeEnd, viewDistance);
    }
    if (uMaxDistance > 0.0)
    {
        distanceFade *= 1.0 - smoothstep(uMaxDistance, uMaxDistance + max(uRadius, 0.0001), viewDistance);
    }

    vec3 randomVec = texture(uNoiseTexture, vTexCoord * uNoiseScaleClamped).xyz * 2.0 - 1.0;
    if (dot(randomVec, randomVec) < 0.00001)
    {
        randomVec = vec3(1.0, 0.0, 0.0);
    }
    else
    {
        randomVec = normalize(randomVec);
    }
    vec3 tangent = randomVec - centerNormal * dot(randomVec, centerNormal);
    if (dot(tangent, tangent) < 0.00001)
    {
        tangent = abs(centerNormal.z) < 0.95
            ? normalize(cross(centerNormal, vec3(0.0, 0.0, 1.0)))
            : normalize(cross(centerNormal, vec3(0.0, 1.0, 0.0)));
    }
    else
    {
        tangent = normalize(tangent);
    }
    vec3 bitangent = cross(centerNormal, tangent);
    mat3 tbn = mat3(tangent, bitangent, centerNormal);

    float occlusion = 0.0;
    int sampleCount = clamp(uSampleCount, 1, 96);
    for (int i = 0; i < sampleCount; ++i)
    {
        vec3 sampleVec = tbn * uSamples[i].xyz;
        vec3 sampleViewPos = centerViewPos + sampleVec * uRadius;
        vec2 sampleUv = ProjectViewPositionToUv(sampleViewPos);
        if (sampleUv.x <= 0.0 || sampleUv.x >= 1.0 || sampleUv.y <= 0.0 || sampleUv.y >= 1.0)
            continue;

        float sampleDepth = texture(uDepthTexture, sampleUv).r;
        if (sampleDepth >= 0.999999) continue;

        vec3 sampleSurfaceViewPos = ReconstructViewPosition(sampleUv, sampleDepth);
        vec3 delta = sampleSurfaceViewPos - centerViewPos;
        float depthDelta = abs(centerViewPos.z - sampleSurfaceViewPos.z);
        float rangeWeight = smoothstep(0.0, 1.0, uRadius / max(depthDelta, 0.00001));
        float planeDistance = dot(delta, centerNormal);
        float planeWeight = smoothstep(uBias, uBias + uPlaneWeightRange, planeDistance);
        float isOccluding = sampleSurfaceViewPos.z >= sampleViewPos.z + uBias ? 1.0 : 0.0;
        occlusion += isOccluding * rangeWeight * planeWeight;
    }

    float normalizedOcclusion = clamp((occlusion / float(sampleCount)) * max(uIntensity, 0.0), 0.0, 1.0);
    float ao = 1.0 - normalizedOcclusion;
    ao = pow(clamp(ao, 0.0, 1.0), max(uPower, 0.05));
    ao = clamp((ao - 0.5) * max(uContrast, 0.0) + 0.5, 0.0, 1.0);
    ao = mix(1.0, ao, clamp(distanceFade, 0.0, 1.0));
    FragColor = ao;
}
