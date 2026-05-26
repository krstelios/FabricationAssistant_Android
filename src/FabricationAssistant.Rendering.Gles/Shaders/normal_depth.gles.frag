#version 310 es
precision highp float;

// Direct port of src/FabricationAssistant.Rendering.OpenTK/Shaders/normal_depth.frag.glsl
// Encodes the view-space normal into RG using the octahedral mapping
// (Cigolle et al). Linear view depth is packed into BA so SSAO can sample a
// regular color texture with useful precision on GLES devices.

in vec3 vViewNormal;
in float vViewDepth;
in vec3 vWorldPos;

uniform vec2 uLinearDepthRange;
const int MAX_SECTION_PLANES = 8;
uniform int uSectionPlaneCount;
uniform vec4 uSectionPlanes[MAX_SECTION_PLANES];

layout(location = 0) out vec4 FragColor;

vec2 EncodeOctNormal(vec3 normal)
{
    normal /= max(abs(normal.x) + abs(normal.y) + abs(normal.z), 1e-6);
    vec2 encoded = normal.xy;
    if (normal.z < 0.0)
    {
        vec2 wrapSign = vec2(encoded.x < 0.0 ? -1.0 : 1.0, encoded.y < 0.0 ? -1.0 : 1.0);
        encoded = (1.0 - abs(encoded.yx)) * wrapSign;
    }
    return encoded * 0.5 + 0.5;
}

vec2 EncodeDepth16(float depth)
{
    float d = clamp(depth, 0.0, 1.0);
    float high = floor(d * 255.0) / 255.0;
    float low = fract(d * 255.0);
    return vec2(high, low);
}

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

    vec3 normal = normalize(vViewNormal);
    float depthSpan = max(uLinearDepthRange.y - uLinearDepthRange.x, 0.000001);
    float normalizedDepth = (vViewDepth - uLinearDepthRange.x) / depthSpan;
    FragColor = vec4(EncodeOctNormal(normal), EncodeDepth16(normalizedDepth));
}
