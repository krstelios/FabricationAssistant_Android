#version 310 es
precision highp float;

// FXAA (luma, NVIDIA/Geeks3D "console" quality 3.11 variant). Post-process pass
// over the fully composited scene+overlays. Smooths every edge in the final
// image - geometry silhouettes, the screen-space CAD silhouette overlay, and
// the selection outlines - none of which the hardware MSAA resolve can touch.

uniform sampler2D uScene;       // composited scene color (full resolution)
uniform vec2 uInvResolution;    // 1 / viewport size

out vec4 FragColor;

const float EDGE_THRESHOLD_MIN = 0.0312;
const float EDGE_THRESHOLD     = 0.125;
const float SUBPIX_REDUCE      = 1.0 / 8.0;
const float SPAN_MAX           = 8.0;

float luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }

void main()
{
    vec2 uv = gl_FragCoord.xy * uInvResolution;

    vec3 rgbM  = texture(uScene, uv).rgb;
    vec3 rgbNW = texture(uScene, uv + vec2(-1.0, -1.0) * uInvResolution).rgb;
    vec3 rgbNE = texture(uScene, uv + vec2( 1.0, -1.0) * uInvResolution).rgb;
    vec3 rgbSW = texture(uScene, uv + vec2(-1.0,  1.0) * uInvResolution).rgb;
    vec3 rgbSE = texture(uScene, uv + vec2( 1.0,  1.0) * uInvResolution).rgb;

    float lumaM  = luma(rgbM);
    float lumaNW = luma(rgbNW);
    float lumaNE = luma(rgbNE);
    float lumaSW = luma(rgbSW);
    float lumaSE = luma(rgbSE);

    float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
    float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));

    // Flat region: leave the centre pixel untouched (preserves fine detail).
    float range = lumaMax - lumaMin;
    if (range < max(EDGE_THRESHOLD_MIN, lumaMax * EDGE_THRESHOLD))
    {
        FragColor = vec4(rgbM, 1.0);
        return;
    }

    // Edge tangent direction from the 4 diagonal luma gradients.
    vec2 dir;
    dir.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
    dir.y =  ((lumaNW + lumaSW) - (lumaNE + lumaSE));

    float dirReduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * 0.25 * SUBPIX_REDUCE, 1.0 / 128.0);
    float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
    dir = clamp(dir * rcpDirMin, vec2(-SPAN_MAX), vec2(SPAN_MAX)) * uInvResolution;

    vec3 rgbA = 0.5 * (
        texture(uScene, uv + dir * (1.0 / 3.0 - 0.5)).rgb +
        texture(uScene, uv + dir * (2.0 / 3.0 - 0.5)).rgb);
    vec3 rgbB = rgbA * 0.5 + 0.25 * (
        texture(uScene, uv + dir * -0.5).rgb +
        texture(uScene, uv + dir *  0.5).rgb);

    float lumaB = luma(rgbB);
    FragColor = vec4((lumaB < lumaMin || lumaB > lumaMax) ? rgbA : rgbB, 1.0);
}
