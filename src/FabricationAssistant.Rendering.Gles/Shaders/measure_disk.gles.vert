#version 310 es
precision highp float;

layout(location = 0) in vec3 aCenter;
layout(location = 1) in vec4 aColor;
layout(location = 2) in vec3 aAxisU;
layout(location = 3) in vec3 aAxisV;
layout(location = 4) in vec2 aOffset;

uniform mat4 uMVP;
uniform float uWorldPerPixelFactor;
uniform float uPixelRadius;

out vec4 vColor;
out vec2 vOffset;

void main()
{
    vColor = aColor;
    vOffset = aOffset;

    vec4 clipCenter = uMVP * vec4(aCenter, 1.0);
    float worldPerPixel = max(clipCenter.w * uWorldPerPixelFactor, 0.0);
    float radius = uPixelRadius * worldPerPixel;
    vec3 world = aCenter + aAxisU * (aOffset.x * radius) + aAxisV * (aOffset.y * radius);
    gl_Position = uMVP * vec4(world, 1.0);
}
