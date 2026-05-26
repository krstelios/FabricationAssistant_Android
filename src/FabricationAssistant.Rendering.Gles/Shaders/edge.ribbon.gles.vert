#version 310 es
precision highp float;

layout(location = 0) in vec3 aPosition0;
layout(location = 1) in vec3 aPosition1;
layout(location = 2) in vec3 aNormalA;
layout(location = 3) in vec3 aNormalB;
layout(location = 4) in float aFlags;
layout(location = 5) in float aSegmentT;
layout(location = 6) in float aSide;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;
uniform mat3 uNormalMatrix;
uniform vec2 uViewportSize;
uniform float uLineWidthPixels;
uniform float uDepthBias;
uniform bool uSilhouetteEnabled;

out float vDistancePixels;
out float vHalfWidthPixels;
out float vFeatherPixels;
out float vVisible;
out vec3 vWorldPos;

const float NearPlaneClipEpsilon = 0.0;

const float EdgeFlagBoundary = 1.0;
const float EdgeFlagFeature = 2.0;
const float EdgeFlagSilhouetteCandidate = 4.0;
const float EdgeFlagImported = 8.0;

bool HasFlag(float flags, float flag)
{
    return mod(floor(flags / flag), 2.0) >= 1.0;
}

bool ShouldRenderEdge(vec3 viewPos0, vec3 viewPos1, vec3 viewNormalA, vec3 viewNormalB)
{
    if (HasFlag(aFlags, EdgeFlagBoundary) || HasFlag(aFlags, EdgeFlagFeature) || HasFlag(aFlags, EdgeFlagImported))
        return true;

    if (!uSilhouetteEnabled || !HasFlag(aFlags, EdgeFlagSilhouetteCandidate))
        return false;

    vec3 midPoint = (viewPos0 + viewPos1) * 0.5;
    vec3 viewDir = normalize(-midPoint);
    float facingA = dot(viewNormalA, viewDir);
    float facingB = dot(viewNormalB, viewDir);
    return facingA * facingB < 0.0;
}

void HideVertex()
{
    gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
    vDistancePixels = 0.0;
    vHalfWidthPixels = 0.0;
    vFeatherPixels = 1.0;
    vVisible = 0.0;
    vWorldPos = vec3(0.0);
}

void main()
{
    vec4 world0 = uModel * vec4(aPosition0, 1.0);
    vec4 world1 = uModel * vec4(aPosition1, 1.0);
    vec4 view0 = uView * world0;
    vec4 view1 = uView * world1;

    mat3 viewRotation = mat3(uView);
    vec3 viewNormalA = normalize(viewRotation * (uNormalMatrix * aNormalA));
    vec3 viewNormalB = normalize(viewRotation * (uNormalMatrix * aNormalB));

    if (!ShouldRenderEdge(view0.xyz, view1.xyz, viewNormalA, viewNormalB))
    {
        HideVertex();
        return;
    }

    vec4 p0 = uProjection * view0;
    vec4 p1 = uProjection * view1;

    float near0 = p0.z + p0.w;
    float near1 = p1.z + p1.w;
    if (near0 < NearPlaneClipEpsilon && near1 < NearPlaneClipEpsilon)
    {
        HideVertex();
        return;
    }
    if (near0 < NearPlaneClipEpsilon || near1 < NearPlaneClipEpsilon)
    {
        float t = clamp((NearPlaneClipEpsilon - near0) / (near1 - near0), 0.0, 1.0);
        vec4 clippedPosition = mix(p0, p1, t);
        if (near0 < NearPlaneClipEpsilon)
            p0 = clippedPosition;
        else
            p1 = clippedPosition;
    }

    if (p0.w <= 0.0 || p1.w <= 0.0 || uViewportSize.x <= 0.0 || uViewportSize.y <= 0.0)
    {
        HideVertex();
        return;
    }

    vec2 ndc0 = p0.xy / p0.w;
    vec2 ndc1 = p1.xy / p1.w;
    vec2 directionPixels = (ndc1 - ndc0) * uViewportSize;
    float lengthPixels = length(directionPixels);
    if (lengthPixels < 0.01)
    {
        HideVertex();
        return;
    }

    vec2 normalPixels = vec2(-directionPixels.y, directionPixels.x) / lengthPixels;
    float halfWidthPixels = max(uLineWidthPixels * 0.5, 0.025);
    float featherPixels = 1.0;
    float expandedHalfWidth = halfWidthPixels + featherPixels;
    vec2 offsetNdc = normalPixels * (expandedHalfWidth * 2.0) / uViewportSize;

    vec4 clipPosition = mix(p0, p1, aSegmentT);
    clipPosition.xy += offsetNdc * clipPosition.w * aSide;
    clipPosition.z -= uDepthBias * clipPosition.w;

    gl_Position = clipPosition;
    vWorldPos = mix(world0.xyz, world1.xyz, aSegmentT);
    vDistancePixels = aSide * expandedHalfWidth;
    vHalfWidthPixels = halfWidthPixels;
    vFeatherPixels = featherPixels;
    vVisible = 1.0;
}
