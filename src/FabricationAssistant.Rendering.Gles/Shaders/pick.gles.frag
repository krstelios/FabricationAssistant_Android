#version 310 es
precision highp float;
precision highp int;

uniform uint uMeshIndex;

layout(location = 0) out uint fragId;

void main()
{
    fragId = uMeshIndex;
}
