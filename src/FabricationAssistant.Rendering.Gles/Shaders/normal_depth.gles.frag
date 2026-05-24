#version 310 es
precision highp float;

// Direct port of src/FabricationAssistant.Rendering.OpenTK/Shaders/normal_depth.frag.glsl
// Encodes the view-space normal into two channels using the octahedral mapping
// (Cigolle et al). Depth is written to the FBO's depth attachment naturally;
// the SSAO pass samples it via a separate sampler2D bound to the depth texture.

in vec3 vViewNormal;

layout(location = 0) out vec2 FragColor;

vec2 EncodeOctNormal(vec3 normal)
{
    normal /= max(abs(normal.x) + abs(normal.y) + abs(normal.z), 1e-6);
    vec2 encoded = normal.xy;
    if (normal.z < 0.0)
    {
        encoded = (1.0 - abs(encoded.yx)) * sign(encoded.xy);
    }
    return encoded * 0.5 + 0.5;
}

void main()
{
    vec3 normal = normalize(vViewNormal);
    FragColor = EncodeOctNormal(normal);
}
