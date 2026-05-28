#version 310 es
precision highp float;

// Screen-space silhouette overlay. Reads the normal-depth pre-pass output
// (octahedral normal in RG, packed linear depth in BA) and detects edges
// where either the depth or the normal changes sharply between adjacent
// pixels. Replaces the per-mesh runtime silhouette test that used to live
// in edge.ribbon.gles.vert with a constant-cost fullscreen post-process.
//
// Algorithm: forward-difference Sobel on depth + first-derivative of the
// normal vector (Antichamber-style, see Acko / Acko.net "Occlusion with
// bells on" and the Godot screen-space-edge-detection community shaders).
// The center pixel's depth is the gate: skies / background (depth ~1)
// emit no edge so the silhouette pass never paints over open background.

in vec2 vTexCoord;

uniform sampler2D uNormalDepthTexture;
uniform vec2 uViewportInvSize;
uniform vec4 uEdgeColor;
uniform float uDepthEdgeThreshold;
uniform float uNormalEdgeThreshold;

out vec4 FragColor;

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
    vec2 uv = vTexCoord;
    vec2 px = uViewportInvSize;

    vec4 center = texture(uNormalDepthTexture, uv);
    float centerDepth = DecodeDepth01(center.ba);

    // Background: the normal-depth pass writes depth ~= 1.0 for empty pixels
    // (cleared with FAR depth). Skip them so the overlay never tints sky.
    if (centerDepth >= 0.999)
        discard;

    vec3 centerNormal = DecodeNormal(center.rg);

    // 4-neighbour forward differences (cardinal axes only - cheap and
    // sufficient for clean silhouettes when paired with the normal test).
    vec4 right = texture(uNormalDepthTexture, uv + vec2(px.x, 0.0));
    vec4 up = texture(uNormalDepthTexture, uv + vec2(0.0, px.y));
    vec4 left = texture(uNormalDepthTexture, uv - vec2(px.x, 0.0));
    vec4 down = texture(uNormalDepthTexture, uv - vec2(0.0, px.y));

    float depthRight = DecodeDepth01(right.ba);
    float depthUp = DecodeDepth01(up.ba);
    float depthLeft = DecodeDepth01(left.ba);
    float depthDown = DecodeDepth01(down.ba);

    // Depth signal: max absolute difference vs the four cardinal neighbours.
    float depthDiff = max(
        max(abs(centerDepth - depthRight), abs(centerDepth - depthUp)),
        max(abs(centerDepth - depthLeft), abs(centerDepth - depthDown)));

    vec3 normalRight = DecodeNormal(right.rg);
    vec3 normalUp = DecodeNormal(up.rg);
    vec3 normalLeft = DecodeNormal(left.rg);
    vec3 normalDown = DecodeNormal(down.rg);

    // Normal signal: max (1 - dot) across the same four neighbours. Use
    // max-not-sum so a single sharp crease still triggers.
    float normalDiff = max(
        max(1.0 - dot(centerNormal, normalRight), 1.0 - dot(centerNormal, normalUp)),
        max(1.0 - dot(centerNormal, normalLeft), 1.0 - dot(centerNormal, normalDown)));

    float depthEdge = smoothstep(uDepthEdgeThreshold * 0.5, uDepthEdgeThreshold, depthDiff);
    float normalEdge = smoothstep(uNormalEdgeThreshold * 0.5, uNormalEdgeThreshold, normalDiff);
    float edge = max(depthEdge, normalEdge);

    if (edge < 0.01)
        discard;

    FragColor = vec4(uEdgeColor.rgb, uEdgeColor.a * edge);
}
