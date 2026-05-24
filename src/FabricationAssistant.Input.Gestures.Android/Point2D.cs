namespace FabricationAssistant.Input.Gestures.Android;

/// <summary>
/// 2D point in viewport-DIP coordinates. Replaces System.Windows.Point so the
/// gesture recognizer compiles without WPF. Subtraction yields a Vector2D
/// (mirrors System.Windows.Point's operator-).
/// </summary>
public readonly record struct Point2D(double X, double Y)
{
    public static Vector2D operator -(Point2D a, Point2D b) => new(a.X - b.X, a.Y - b.Y);
    public static Point2D operator +(Point2D p, Vector2D v) => new(p.X + v.X, p.Y + v.Y);
    public override string ToString() => $"({X:F2}, {Y:F2})";
}
