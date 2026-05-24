#version 310 es
precision highp float;

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;
uniform mat3 uNormalMatrix;

out vec3 vViewNormal;

void main()
{
    vec4 worldPos = uModel * vec4(aPosition, 1.0);
    vec3 worldNormal = normalize(uNormalMatrix * aNormal);
    vViewNormal = normalize(mat3(uView) * worldNormal);
    gl_Position = uProjection * uView * worldPos;
}
