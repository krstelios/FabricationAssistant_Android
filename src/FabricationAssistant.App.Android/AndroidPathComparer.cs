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
        bool leftNumber = IsUnsignedInteger(left);
        bool rightNumber = IsUnsignedInteger(right);
        if (leftNumber && rightNumber)
            return CompareUnsignedIntegerText(left, right);

        return string.Compare(left, right, StringComparison.Ordinal);
    }

    private static bool IsUnsignedInteger(string value)
    {
        if (value.Length == 0)
            return false;

        foreach (char ch in value)
        {
            if (ch < '0' || ch > '9')
                return false;
        }

        return true;
    }

    private static int CompareUnsignedIntegerText(string left, string right)
    {
        ReadOnlySpan<char> normalizedLeft = TrimLeadingZeroes(left);
        ReadOnlySpan<char> normalizedRight = TrimLeadingZeroes(right);
        int lengthCompare = normalizedLeft.Length.CompareTo(normalizedRight.Length);
        if (lengthCompare != 0)
            return lengthCompare;

        int valueCompare = normalizedLeft.SequenceCompareTo(normalizedRight);
        if (valueCompare != 0)
            return valueCompare;

        return left.Length.CompareTo(right.Length);
    }

    private static ReadOnlySpan<char> TrimLeadingZeroes(string value)
    {
        int index = 0;
        while (index < value.Length - 1 && value[index] == '0')
            index++;

        return value.AsSpan(index);
    }
}
