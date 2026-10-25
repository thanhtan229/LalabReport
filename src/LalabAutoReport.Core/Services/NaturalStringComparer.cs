using System;
using System.Collections.Generic;

namespace LalabAutoReport.Core.Services;

public class NaturalStringComparer : IComparer<string?>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int ix = 0, iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            if (char.IsDigit(x[ix]) && char.IsDigit(y[iy]))
            {
                int startX = ix;
                while (ix < x.Length && char.IsDigit(x[ix])) ix++;
                string numStrX = x.Substring(startX, ix - startX);

                int startY = iy;
                while (iy < y.Length && char.IsDigit(y[iy])) iy++;
                string numStrY = y.Substring(startY, iy - startY);

                string trimmedX = numStrX.TrimStart('0');
                string trimmedY = numStrY.TrimStart('0');

                if (trimmedX.Length != trimmedY.Length)
                {
                    return trimmedX.Length.CompareTo(trimmedY.Length);
                }

                int numComp = string.CompareOrdinal(trimmedX, trimmedY);
                if (numComp != 0) return numComp;

                int lenComp = numStrX.Length.CompareTo(numStrY.Length);
                if (lenComp != 0) return lenComp;
            }
            else
            {
                int charComp = char.ToUpperInvariant(x[ix]).CompareTo(char.ToUpperInvariant(y[iy]));
                if (charComp != 0) return charComp;
                ix++;
                iy++;
            }
        }

        return x.Length.CompareTo(y.Length);
    }
}
