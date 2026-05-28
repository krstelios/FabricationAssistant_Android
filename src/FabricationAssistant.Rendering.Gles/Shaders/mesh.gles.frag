#version 310 es
precision highp float;
precision highp int;

// Direct port of src/FabricationAssistant.Rendering.OpenTK/Shaders/mesh.frag.glsl
// with #version downgraded to 310 es. Lighting logic and uniform names match
// the desktop exactly so SceneAppearance values produce equivalent shading.

in vec3 vWorldPos;
in vec3 vNormal;

uniform vec3 uCameraPos;
uniform vec3 uCameraForwardDir;     // pre-normalized; -direction is the headlight ray
uniform vec4 uColor;
uniform vec3 uWorldUpDir;
uniform vec3 uKeyLightDir;
uniform vec3 uFillLightDir;
uniform vec3 uBounceLightDir;
uniform float uSurfaceOpacity;
uniform float uBaseColorLift;
uniform float uAmbientStrength;
uniform float uHeadlightStrength;
uniform float uKeyLightStrength;
uniform float uFillLightStrength;
uniform float uBounceLightStrength;
uniform float uHemisphereStrength;
uniform float uSpecularStrength;
uniform float uSpecularPower;
uniform float uContourStrength;
uniform float uContourPower;
uniform vec3 uTintColor;
uniform float uTintStrength;
uniform bool uAmbientOcclusionEnabled;
uniform sampler2D uAmbientOcclusionTexture;     // R8, rendered at half resolution
uniform sampler2D uAmbientOcclusionDepthTexture; // packed view-space depth (BA) from the normal-depth prepass
uniform vec2 uViewportInvSize;

// Plan 2E inline selection highlight - kept while the post-process outline
// pass is enabled too (the host writes uSelectedMeshIndex = 0 when the
// outline post-process is the only selection visual).
uniform int uMeshIndex;
uniform int uSelectedMeshIndex;
uniform int uHoveredMeshIndex;
uniform vec3 uHighlightColor;
uniform vec3 uHoverColor;
uniform float uHoverTintStrength;

const int MAX_SECTION_PLANES = 8;
uniform int uSectionPlaneCount;
uniform vec4 uSectionPlanes[MAX_SECTION_PLANES];

out vec4 FragColor;

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

    vec3 normal = normalize(vNormal);
    // A section cut or a double-sided mesh exposes geometry back faces.
    // Match the desktop shader: flip those normals so they shade as real
    // surfaces instead of collapsing to dark contour/ambient bands.
    if (!gl_FrontFacing) normal = -normal;

    vec3 viewDir = normalize(uCameraPos - vWorldPos);
    vec3 worldUpDir = uWorldUpDir;
    vec3 keyDir = uKeyLightDir;
    vec3 fillDir = uFillLightDir;
    vec3 bounceDir = uBounceLightDir;

    float viewAlignment = clamp(abs(dot(normal, viewDir)), 0.0, 1.0);

    // Directional headlight: parallel rays along -uCameraForwardDir so the
    // diffuse term depends only on surface orientation, not on per-pixel
    // proximity to the camera. Specular still uses viewDir for correct
    // reflection geometry.
    float headlight = max(dot(normal, -uCameraForwardDir), 0.0);
    float keyDiffuse = max(dot(normal, keyDir), 0.0);
    float fillDiffuse = max(dot(normal, fillDir), 0.0);
    float bounceDiffuse = max(dot(normal, bounceDir), 0.0);

    vec3 keyHalf = normalize(keyDir + viewDir);
    vec3 fillHalf = normalize(fillDir + viewDir);
    float keySpecular = pow(max(dot(normal, keyHalf), 0.0), uSpecularPower);
    float fillSpecular = pow(max(dot(normal, fillHalf), 0.0), max(4.0, uSpecularPower * 0.6));
    float specular = (keySpecular + fillSpecular * 0.30) * uSpecularStrength;

    float hemi = clamp(dot(normal, worldUpDir) * 0.5 + 0.5, 0.0, 1.0);
    float silhouetteMask = smoothstep(0.38, 0.86, 1.0 - viewAlignment);
    float contourDarkening = pow(silhouetteMask, max(uContourPower, 0.001));

    float ao = 1.0;
    if (uAmbientOcclusionEnabled)
    {
        vec2 aoUv = gl_FragCoord.xy * uViewportInvSize;

        // Depth-aware bilateral upsample of the half-resolution AO texture.
        // textureGather collapses the 4 nearest texels into 1 fetch (GLES 3.1
        // core); we also gather their depths (high byte, .b) to reject samples
        // whose surface differs from this pixel - prevents the silhouette halo
        // that a plain bilinear upsample would produce around object edges.
        vec4 ao4 = textureGather(uAmbientOcclusionTexture, aoUv, 0);
        vec4 depth4 = textureGather(uAmbientOcclusionDepthTexture, aoUv, 2);
        float refDepth = texture(uAmbientOcclusionDepthTexture, aoUv).b;

        // textureGather order: .x=(i0,j1), .y=(i1,j1), .z=(i1,j0), .w=(i0,j0).
        // Compute the 4 bilinear weights from the fractional sub-texel position.
        vec2 aoSize = vec2(textureSize(uAmbientOcclusionTexture, 0));
        vec2 f = fract(aoUv * aoSize - 0.5);
        vec4 bilinearWeight = vec4(
            (1.0 - f.x) * f.y,         // top-left
            f.x * f.y,                 // top-right
            f.x * (1.0 - f.y),         // bottom-right
            (1.0 - f.x) * (1.0 - f.y)  // bottom-left
        );

        // Rational depth-similarity falloff. 8-bit depth precision is enough
        // to separate foreground from background; exact tuning isn't critical.
        const float depthRejectK = 100.0;
        vec4 depthDelta = abs(depth4 - refDepth) * depthRejectK;
        vec4 depthWeight = 1.0 / (1.0 + depthDelta * depthDelta);

        vec4 weights = bilinearWeight * depthWeight;
        float weightSum = weights.x + weights.y + weights.z + weights.w;
        ao = weightSum > 1e-5
            ? dot(ao4, weights) / weightSum
            : dot(ao4, vec4(0.25));
        ao = clamp(ao, 0.0, 1.0);
    }

    float ambientIndirect =
        uAmbientStrength +
        uBounceLightStrength * bounceDiffuse +
        uHemisphereStrength * hemi;
    float directLighting =
        uHeadlightStrength * headlight +
        uKeyLightStrength * keyDiffuse +
        uFillLightStrength * fillDiffuse;
    float lighting = ambientIndirect * ao + directLighting;

    vec3 baseColor = mix(uColor.rgb, vec3(0.95), uBaseColorLift);
    vec3 color = baseColor * lighting + vec3(specular);

    // Tight contour darkening keeps the silhouette from washing out large
    // flat faces.
    float shadowAmount = 1.0 - exp(-3.2 * uContourStrength * contourDarkening);
    color *= 1.0 - clamp(shadowAmount, 0.0, 0.72);

    color = mix(color, uTintColor, clamp(uTintStrength, 0.0, 1.0));

    if (uHoveredMeshIndex != 0 && uMeshIndex == uHoveredMeshIndex)
    {
        color = mix(color, uHoverColor, clamp(uHoverTintStrength, 0.0, 1.0));
    }

    // Inline selection highlight stays on so selected bodies remain visible
    // even when the post-process outline is subtle on mobile displays.
    if (uSelectedMeshIndex != 0 && uMeshIndex == uSelectedMeshIndex)
    {
        color = mix(color, uHighlightColor, 0.45) + uHighlightColor * 0.10;
    }

    color = clamp(color, vec3(0.0), vec3(1.0));
    FragColor = vec4(color, clamp(uColor.a * uSurfaceOpacity, 0.0, 1.0));
}
