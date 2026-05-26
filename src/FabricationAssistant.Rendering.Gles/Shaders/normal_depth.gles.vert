#version 310 es
precision highp float;

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;
uniform mat3 uNormalMatrix;

out vec3 vViewNormal;
out float vViewDepth;
out vec3 vWorldPos;

void main()
{
    vec4 worldPos = uModel * vec4(aPosition, 1.0);
    vWorldPos = worldPos.xyz;
    vec4 viewPos = uView * worldPos;
    vec3 worldNormal = normalize(uNormalMatrix * aNormal);
    vViewNormal = normalize(mat3(uView) * worldNormal);
    vViewDepth = max(-viewPos.z, 0.0);
    gl_Position = uProjection * viewPos;
}
