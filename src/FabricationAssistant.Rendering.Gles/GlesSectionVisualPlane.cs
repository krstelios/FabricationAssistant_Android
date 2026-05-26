using System.Numerics;

namespace FabricationAssistant.Rendering.Gles;

public readonly record struct GlesSectionVisualPlane(
    Vector3 Anchor,
    Vector3 AxisX,
    Vector3 AxisY,
    Vector3 Normal,
    bool Selected);
