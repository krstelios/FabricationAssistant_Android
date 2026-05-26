namespace FabricationAssistant.App.Android;

internal sealed class AndroidPathComparer : IComparer<string>
{
    public static readonly AndroidPathComparer Instance = new();

    private AndroidPathComparer()
    {
    }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        string[] left = x.Split('/');
        string[] right = y.Split('/');
        int count = Math.Min(left.Length, right.Length);
        for (int i = 0; i < count; i++)
        {
            int segmentCompare = CompareSegment(left[i], right[i]);
            if (segmentCompare != 0)
                return segmentCompare;
        }

        return left.Length.CompareTo(right.Length);
    }

    private static int CompareSegment(string left, string right)
    {
        bool leftNumber = int.TryParse(left, out int leftValue);
        bool rightNumber = int.TryParse(right, out int rightValue);
        if (leftNumber && rightNumber)
            return leftValue.CompareTo(rightValue);

        return string.Compare(left, right, StringComparison.Ordinal);
    }
}
