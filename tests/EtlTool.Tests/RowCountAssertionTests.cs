using EtlTool.Core.Engine;

namespace EtlTool.Tests;

public class RowCountAssertionTests
{
    // Case 1: policy=Ignore → always Passed=true, Violation=null regardless of input
    [Theory]
    [InlineData(0L, null, null)]
    [InlineData(50L, 100L, null)]
    [InlineData(2000L, null, 1000L)]
    [InlineData(500L, 100L, 1000L)]
    public void Ignore_AlwaysPassed(long actual, long? min, long? max)
    {
        var result = RowCountAssertion.Check(actual, min, max, RowCountAssertionPolicy.Ignore);
        Assert.True(result.Passed);
        Assert.Null(result.Violation);
    }

    // Case 2: min=100, max=null, actual=50 → Passed=false, Violation contains "少於最小 100"
    [Fact]
    public void MinOnly_BelowMin_Fails()
    {
        var result = RowCountAssertion.Check(50, 100, null, RowCountAssertionPolicy.Fail);
        Assert.False(result.Passed);
        Assert.NotNull(result.Violation);
        Assert.Contains("少於最小 100", result.Violation);
    }

    // Case 3: min=100, max=null, actual=100 → Passed=true (boundary inclusive)
    [Fact]
    public void MinOnly_AtMin_Passes()
    {
        var result = RowCountAssertion.Check(100, 100, null, RowCountAssertionPolicy.Fail);
        Assert.True(result.Passed);
        Assert.Null(result.Violation);
    }

    // Case 4: min=null, max=1000, actual=1500 → Passed=false, Violation contains "超過最大 1000"
    [Fact]
    public void MaxOnly_AboveMax_Fails()
    {
        var result = RowCountAssertion.Check(1500, null, 1000, RowCountAssertionPolicy.Fail);
        Assert.False(result.Passed);
        Assert.NotNull(result.Violation);
        Assert.Contains("超過最大 1000", result.Violation);
    }

    // Case 5: min=null, max=1000, actual=1000 → Passed=true (boundary inclusive)
    [Fact]
    public void MaxOnly_AtMax_Passes()
    {
        var result = RowCountAssertion.Check(1000, null, 1000, RowCountAssertionPolicy.Fail);
        Assert.True(result.Passed);
        Assert.Null(result.Violation);
    }

    // Case 6: min=100, max=1000, actual=500 → Passed=true
    [Fact]
    public void MinMax_InRange_Passes()
    {
        var result = RowCountAssertion.Check(500, 100, 1000, RowCountAssertionPolicy.Fail);
        Assert.True(result.Passed);
        Assert.Null(result.Violation);
    }

    // Case 7: min=100, max=1000, actual=99 → Passed=false (min violation)
    [Fact]
    public void MinMax_BelowMin_Fails()
    {
        var result = RowCountAssertion.Check(99, 100, 1000, RowCountAssertionPolicy.Fail);
        Assert.False(result.Passed);
        Assert.NotNull(result.Violation);
        Assert.Contains("少於最小 100", result.Violation);
    }

    // Case 8: min=100, max=1000, actual=1001 → Passed=false (max violation)
    [Fact]
    public void MinMax_AboveMax_Fails()
    {
        var result = RowCountAssertion.Check(1001, 100, 1000, RowCountAssertionPolicy.Fail);
        Assert.False(result.Passed);
        Assert.NotNull(result.Violation);
        Assert.Contains("超過最大 1000", result.Violation);
    }

    // Case 9: min=null, max=null, actual=0, policy=Warn → Passed=true (no limit set)
    [Fact]
    public void NoLimits_AnyActual_Passes()
    {
        var result = RowCountAssertion.Check(0, null, null, RowCountAssertionPolicy.Warn);
        Assert.True(result.Passed);
        Assert.Null(result.Violation);
    }

    // Case 10: policy=Warn vs policy=Fail produce same Passed/Violation result
    [Fact]
    public void WarnAndFail_ProduceSameCheckResult()
    {
        var warnResult = RowCountAssertion.Check(50, 100, null, RowCountAssertionPolicy.Warn);
        var failResult = RowCountAssertion.Check(50, 100, null, RowCountAssertionPolicy.Fail);
        Assert.Equal(warnResult.Passed, failResult.Passed);
        Assert.Equal(warnResult.Violation, failResult.Violation);
    }

    // Case 11: min < 0 → ArgumentException
    [Fact]
    public void NegativeMin_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            RowCountAssertion.Check(100, -1, null, RowCountAssertionPolicy.Fail));
    }

    // Case 12: max < 0 → ArgumentException
    [Fact]
    public void NegativeMax_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            RowCountAssertion.Check(100, null, -1, RowCountAssertionPolicy.Fail));
    }

    // Case 13: min > max → ArgumentException
    [Fact]
    public void MinGreaterThanMax_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            RowCountAssertion.Check(100, 500, 100, RowCountAssertionPolicy.Fail));
    }
}
