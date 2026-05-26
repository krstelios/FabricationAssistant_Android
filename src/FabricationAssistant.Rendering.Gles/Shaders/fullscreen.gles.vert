#version 310 es
precision highp float;

// Fullscreen-triangle technique: emits a single triangle that covers the
// viewport in clip space. Android devices are less consistent with
// gl_VertexID-only fullscreen draws, so the renderer supplies explicit
// position/UV attributes.

layout(location = 0) in vec2 aPosition;
layout(location = 1) in vec2 aTexCoord;

out vec2 vTexCoord;

void main()
{
    vTexCoord = aTexCoord;
    gl_Position = vec4(aPosition, 0.0, 1.0);
}
