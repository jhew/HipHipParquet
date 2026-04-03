using HipHipParquet.Services;

namespace HipHipParquet.Tests;

public class NaturalStringComparerTests
{
    private readonly NaturalStringComparer _comparer = NaturalStringComparer.Instance;

    // ── Null handling ──────────────────────────────────────────────────────────

    [Fact]
    public void Compare_BothNull_ReturnsZero()
    {
        Assert.Equal(0, _comparer.Compare(null, null));
    }

    [Fact]
    public void Compare_LeftNull_ReturnsNegative()
    {
        Assert.True(_comparer.Compare(null, "file1") < 0);
    }

    [Fact]
    public void Compare_RightNull_ReturnsPositive()
    {
        Assert.True(_comparer.Compare("file1", null) > 0);
    }

    // ── Same reference / same value ────────────────────────────────────────────

    [Fact]
    public void Compare_SameReference_ReturnsZero()
    {
        var s = "file10";
        Assert.Equal(0, _comparer.Compare(s, s));
    }

    [Fact]
    public void Compare_EqualStrings_ReturnsZero()
    {
        Assert.Equal(0, _comparer.Compare("file10", "file10"));
    }

    // ── Numeric segment ordering ───────────────────────────────────────────────

    [Fact]
    public void Compare_NumericSegment_LowerNumberSortsFirst()
    {
        Assert.True(_comparer.Compare("file2", "file10") < 0);
    }

    [Fact]
    public void Compare_NumericSegment_HigherNumberSortsLast()
    {
        Assert.True(_comparer.Compare("file10", "file2") > 0);
    }

    [Fact]
    public void Compare_NumericSegment_SingleDigitVsDoubleDigit()
    {
        Assert.True(_comparer.Compare("report9", "report10") < 0);
    }

    [Fact]
    public void Compare_NumericSegment_AdjacentNumbers()
    {
        Assert.True(_comparer.Compare("file1", "file2") < 0);
    }

    // ── Leading zeros ──────────────────────────────────────────────────────────

    [Fact]
    public void Compare_LeadingZeros_NumericValueDeterminesOrder()
    {
        // "02" == 2, "010" == 10 → 02 sorts before 010
        Assert.True(_comparer.Compare("file02", "file010") < 0);
    }

    [Fact]
    public void Compare_LeadingZeros_SameValueFewerZerosSortsFirst()
    {
        // "02" and "002" are numerically equal; fewer leading zeros ranks first
        Assert.True(_comparer.Compare("file02", "file002") < 0);
    }

    [Fact]
    public void Compare_AllZeros_TreatedAsZero()
    {
        Assert.Equal(0, _comparer.Compare("file00", "file00"));
    }

    // ── Mixed case ─────────────────────────────────────────────────────────────

    [Fact]
    public void Compare_DifferentCase_TreatedAsEqual()
    {
        Assert.Equal(0, _comparer.Compare("FILE", "file"));
    }

    [Fact]
    public void Compare_MixedCase_AlphaOrderIgnoresCase()
    {
        Assert.True(_comparer.Compare("Apple", "banana") < 0);
    }

    // ── Non-numeric differences ────────────────────────────────────────────────

    [Fact]
    public void Compare_AlphaPrefix_ShorterSortsFirst()
    {
        Assert.True(_comparer.Compare("file", "file1") < 0);
    }

    [Fact]
    public void Compare_AlphaDifference_CorrectOrder()
    {
        Assert.True(_comparer.Compare("abc", "abd") < 0);
    }
}
