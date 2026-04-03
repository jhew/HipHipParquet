namespace HipHipParquet.Services;

/// <summary>
/// Compares strings using case-insensitive natural ordering (e.g. file-2 before file-10).
/// </summary>
public sealed class NaturalStringComparer : IComparer<string?>
{
    public static NaturalStringComparer Instance { get; } = new();

    private NaturalStringComparer() { }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        var ix = 0;
        var iy = 0;

        while (ix < x.Length && iy < y.Length)
        {
            var cx = x[ix];
            var cy = y[iy];

            var xIsDigit = char.IsDigit(cx);
            var yIsDigit = char.IsDigit(cy);

            if (xIsDigit && yIsDigit)
            {
                var startX = ix;
                var startY = iy;

                while (ix < x.Length && x[ix] == '0')
                    ix++;
                while (iy < y.Length && y[iy] == '0')
                    iy++;

                var sigStartX = ix;
                var sigStartY = iy;

                while (ix < x.Length && char.IsDigit(x[ix]))
                    ix++;
                while (iy < y.Length && char.IsDigit(y[iy]))
                    iy++;

                var sigLenX = ix - sigStartX;
                var sigLenY = iy - sigStartY;

                // If all digits were zeros, treat significant length as zero.
                if (sigLenX != sigLenY)
                    return sigLenX.CompareTo(sigLenY);

                for (var i = 0; i < sigLenX; i++)
                {
                    var digitCompare = x[sigStartX + i].CompareTo(y[sigStartY + i]);
                    if (digitCompare != 0)
                        return digitCompare;
                }

                // Same numeric value; fewer leading zeros sorts first.
                var totalLenX = ix - startX;
                var totalLenY = iy - startY;
                if (totalLenX != totalLenY)
                    return totalLenX.CompareTo(totalLenY);

                continue;
            }

            var upperX = char.ToUpperInvariant(cx);
            var upperY = char.ToUpperInvariant(cy);
            var charCompare = upperX.CompareTo(upperY);
            if (charCompare != 0)
                return charCompare;

            ix++;
            iy++;
        }

        return x.Length.CompareTo(y.Length);
    }
}