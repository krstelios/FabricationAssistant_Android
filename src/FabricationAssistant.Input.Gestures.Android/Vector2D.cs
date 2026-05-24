namespace FabricationAssistant.Input.Gestures.Android;

/// <summary>
/// 2D vector in viewport-DIP coordinates. Replaces System.Windows.Vector.
/// Only the members the recognizer uses are exposed: X, Y, Length, Zero,
/// and addition/subtraction.
/// </summary>
public readonly record struct Vector2D(double X, double Y)
{
    public static readonly Vector2D Zero = new(0, 0);

    public double Length => System.Math.Sqrt(X * X + Y * Y);

    public static Vector2D operator +(Vector2D a, Vector2D b) => new(a.X + b.X, a.Y + b.Y);
    public static Vector2D operator -(Vector2D a, Vector2D b) => new(a.X - b.X, a.Y - b.Y);
    public override string ToString() => $"<{X:F2}, {Y:F2}>";
}
