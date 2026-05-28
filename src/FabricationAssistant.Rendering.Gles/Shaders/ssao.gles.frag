#version 310 es
precision highp float;
precision highp int;

// GLES port of src/FabricationAssistant.Rendering.OpenTK/Shaders/ssao.frag.glsl
// with #version downgraded to 310 es and the precision qualifiers added.
// Sampling, plane-weight + range-weight + bias + intensity + power + contrast
// pipeline match the desktop. Perspective views sample the depth attachment
// directly like OpenTK; packed linear depth in uNormalTexture.ba remains the
// orthographic fallback.
//
// Per-pixel rotation comes from Jorge Jimenez's Interleaved Gradient Noise
// (Call of Duty: Advanced Warfare, 2014) -- one fract() evaluation, no
// texture fetch, and the apparent spatial pattern is much finer than the
// classic 4x4 tile noise it replaces. Combined with the Hammersley-built
// uSamples kernel (CPU side), this lets 16 samples integrate smoothly enough
// that a high-radius bilateral blur is no longer required to hide tile bands.

in vec2 vTexCoord;

uniform sampler2D uNormalTexture;
uniform sampler2D uDepthTexture;
uniform vec2 uProjectionOffset;
uniform vec2 uProjectionDepth;       // (proj[2][2], proj[2][3]) of perspective
uniform vec2 uInvProjectionScale;    // (1/scaleX, 1/scaleY)
uniform vec2 uProjectionUvScale;     // (-scaleX/2, -scaleY/2) for forward proj
uniform vec2 uProjectionUvBias;      // (0.5 - offsetX/2, 0.5 - offsetY/2)
uniform vec2 uLinearDepthRange;
uniform bool uIsPerspective;
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

float DecodeViewDepth(vec2 packedDepth)
{
    return mix(uLinearDepthRange.x, uLinearDepthRange.y, DecodeDepth01(packedDepth));
}

vec3 ReconstructViewPosition(vec2 uv, float viewDepth)
{
    vec2 ndc = uv * 2.0 - 1.0;
    float viewZ = -max(viewDepth, 0.000001);
    if (!uIsPerspective)
    {
        return vec3(ndc.x * uInvProjectionScale.x, ndc.y * uInvProjectionScale.y, viewZ);
    }

    float viewX = -(ndc.x + uProjectionOffset.x) * viewZ * uInvProjectionScale.x;
    float viewY = -(ndc.y + uProjectionOffset.y) * viewZ * uInvProjectionScale.y;
    return vec3(viewX, viewY, viewZ);
}

vec3 ReconstructPerspectiveViewPosition(vec2 uv, float depth)
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

// Jimenez 2014. Constants are hand-tuned; do not "simplify" them.
float InterleavedGradientNoise(vec2 pixelCoord)
{
    return fract(52.9829189 * fract(dot(pixelCoord, vec2(0.06711056, 0.00583715))));
}

vec2 ProjectViewPositionToUv(vec3 viewPos)
{
    if (!uIsPerspective)
    {
        vec2 projectionScale = vec2(
            1.0 / max(abs(uInvProjectionScale.x), 0.000001),
            1.0 / max(abs(uInvProjectionScale.y), 0.000001));
        return viewPos.xy * projectionScale * 0.5 + vec2(0.5);
    }

    float invViewZ = 1.0 / min(viewPos.z, -0.000001);
    return uProjectionUvScale * (viewPos.xy * invViewZ) + uProjectionUvBias;
}

void main()
{
    vec4 centerData = texture(uNormalTexture, vTexCoord);
    vec2 centerPacked = centerData.rg;
    float centerDepth01 = uIsPerspective ? texture(uDepthTexture, vTexCoord).r : DecodeDepth01(centerData.ba);
    if (centerDepth01 >= 0.999999)
    {
        FragColor = vec4(1.0);
        return;
    }

    vec3 centerNormal = DecodeNormal(centerPacked);
    float centerViewDepth = DecodeViewDepth(centerData.ba);
    vec3 centerViewPos = uIsPerspective
        ? ReconstructPerspectiveViewPosition(vTexCoord, centerDepth01)
        : ReconstructViewPosition(vTexCoord, centerViewDepth);
    float viewDistance = uIsPerspective ? max(-centerViewPos.z, 0.0) : centerViewDepth;
    float distanceFade = 1.0;
    if (uFadeEnd > uFadeStart)
    {
        distanceFade = 1.0 - smoothstep(uFadeStart, uFadeEnd, viewDistance);
    }
    if (uMaxDistance > 0.0)
    {
        distanceFade *= 1.0 - smoothstep(uMaxDistance, uMaxDistance + max(uRadius, 0.0001), viewDistance);
    }

    // Per-pixel rotation in the tangent plane around the surface normal.
    // IGN gives a finely interleaved pattern; cos/sin gives a unit vector.
    float ignAngle = InterleavedGradientNoise(gl_FragCoord.xy) * 6.2831853;
    vec3 randomVec = vec3(cos(ignAngle), sin(ignAngle), 0.0);
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

        vec4 sampleData = texture(uNormalTexture, sampleUv);
        vec2 sampleDepthPacked = sampleData.ba;
        float sampleDepth01 = uIsPerspective ? texture(uDepthTexture, sampleUv).r : DecodeDepth01(sampleDepthPacked);
        if (sampleDepth01 >= 0.999999) continue;

        float sampleDepth = DecodeViewDepth(sampleDepthPacked);
        vec3 sampleNormal = DecodeNormal(sampleData.rg);
        vec3 sampleSurfaceViewPos = uIsPerspective
            ? ReconstructPerspectiveViewPosition(sampleUv, sampleDepth01)
            : ReconstructViewPosition(sampleUv, sampleDepth);
        vec3 delta = sampleSurfaceViewPos - centerViewPos;
        float depthDelta = abs(centerViewPos.z - sampleSurfaceViewPos.z);
        float rangeWeight = smoothstep(0.0, 1.0, uRadius / max(depthDelta, 0.00001));
        float planeDistance = dot(delta, centerNormal);
        float planeWeight = smoothstep(uBias, uBias + uPlaneWeightRange, planeDistance);
        float isOccluding = sampleSurfaceViewPos.z >= sampleViewPos.z + uBias ? 1.0 : 0.0;
        float desktopOcclusion = isOccluding * rangeWeight * planeWeight;

        float contactOcclusion = 0.0;
        if (!uIsPerspective)
        {
            // Orthographic still uses the GLES packed linear depth path.
            // Keep the conservative contact fallback there, but leave
            // perspective on the desktop depth-texture algorithm.
            float foregroundDepth = max(sampleSurfaceViewPos.z - centerViewPos.z, 0.0);
            float depthContact = smoothstep(uBias, max(uRadius * 0.35, uBias + 0.00001), foregroundDepth);
            float normalBreak = smoothstep(0.08, 0.55, 1.0 - max(dot(centerNormal, sampleNormal), 0.0));
            float creaseContact = normalBreak * smoothstep(uBias, uBias + uPlaneWeightRange, abs(planeDistance));
            contactOcclusion = max(depthContact * 0.45, creaseContact * 0.32) * rangeWeight;
        }
        occlusion += max(desktopOcclusion, contactOcclusion);
    }

    float normalizedOcclusion = clamp((occlusion / float(sampleCount)) * max(uIntensity, 0.0), 0.0, 1.0);
    float ao = 1.0 - normalizedOcclusion;
    ao = pow(clamp(ao, 0.0, 1.0), max(uPower, 0.05));
    ao = clamp((ao - 0.5) * max(uContrast, 0.0) + 0.5, 0.0, 1.0);
    ao = mix(1.0, ao, clamp(distanceFade, 0.0, 1.0));
    FragColor = vec4(vec3(ao), 1.0);
}
