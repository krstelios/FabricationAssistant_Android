#version 310 es
precision highp float;

// Fullscreen-triangle technique: emits a single triangle that covers the
// viewport in clip space. Saves an extra fragment of overdraw vs. a
// fullscreen quad. Indexed by gl_VertexID 0,1,2 - no VBO needed.

out vec2 vTexCoord;

void main()
{
    vec2 pos = vec2(
        (gl_VertexID == 1) ?  3.0 : -1.0,
        (gl_VertexID == 2) ? -3.0 :  1.0
    );
    vTexCoord = (pos * 0.5) + 0.5;
    gl_Position = vec4(pos, 0.0, 1.0);
}
