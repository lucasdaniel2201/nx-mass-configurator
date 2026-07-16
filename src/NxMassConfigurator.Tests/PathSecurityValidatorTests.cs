using Xunit;
using NxMassConfigurator.Services.Security;

namespace NxMassConfigurator.Tests;

/// <summary>
/// Direct port of TargetPathTests from test_delete.py.
/// </summary>
public class PathSecurityValidatorTests
{
    [Fact]
    public void ValidateRelativeTarget_NormalizesToForwardSlashes()
    {
        var result = PathSecurityValidator.ValidateRelativeTarget(
            "nas01", "Public", @"uuid\hi_quality\AA-BB");

        Assert.Equal("uuid/hi_quality/AA-BB", result);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("uuid/../../outside")]
    [InlineData("uuid//folder")]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateRelativeTarget_RejectsTraversalAndEmpty(string value)
    {
        Assert.Throws<InvalidTargetPathException>(() =>
            PathSecurityValidator.ValidateRelativeTarget("nas01", "Public", value));
    }

    [Theory]
    [InlineData(@"\outside")]
    [InlineData(@"C:\outside")]
    public void ValidateRelativeTarget_RejectsAbsolutePaths(string value)
    {
        Assert.Throws<InvalidTargetPathException>(() =>
            PathSecurityValidator.ValidateRelativeTarget("nas01", "Public", value));
    }

    [Fact]
    public void UncToRelativeTarget_MatchesExpectedServerAndShare()
    {
        var result = PathSecurityValidator.UncToRelativeTarget(
            @"\\nas01\Public\uuid\hi_quality\AA-BB", "nas01", "Public");

        Assert.Equal("uuid/hi_quality/AA-BB", result);
    }

    [Theory]
    [InlineData(@"\\other\Public\uuid\folder", "nas01", "Public")]
    [InlineData(@"\\nas01\Other\uuid\folder", "nas01", "Public")]
    [InlineData(@"\\nas01\Public\uuid\..\outside", "nas01", "Public")]
    public void UncToRelativeTarget_RejectsMismatch(string unc, string server, string share)
    {
        Assert.Throws<InvalidTargetPathException>(() =>
            PathSecurityValidator.UncToRelativeTarget(unc, server, share));
    }

    [Fact]
    public void ValidateRelativeTarget_RejectsInvalidServerChars()
    {
        Assert.Throws<InvalidTargetPathException>(() =>
            PathSecurityValidator.ValidateRelativeTarget("ser/ver", "Public", "uuid/mac"));
    }

    [Fact]
    public void ValidateRelativeTarget_RejectsEmptyServer()
    {
        Assert.Throws<InvalidTargetPathException>(() =>
            PathSecurityValidator.ValidateRelativeTarget("", "Public", "uuid/mac"));
    }

    [Fact]
    public void ValidateRelativeTarget_RejectsEmptyShare()
    {
        Assert.Throws<InvalidTargetPathException>(() =>
            PathSecurityValidator.ValidateRelativeTarget("nas01", "", "uuid/mac"));
    }

    [Fact]
    public void ValidateRelativeTarget_RejectsMultiPartShare()
    {
        Assert.Throws<InvalidTargetPathException>(() =>
            PathSecurityValidator.ValidateRelativeTarget("nas01", "Public/Extra", "uuid/mac"));
    }

    [Fact]
    public void UncToRelativeTarget_RejectsIncompleteUnc()
    {
        Assert.Throws<InvalidTargetPathException>(() =>
            PathSecurityValidator.UncToRelativeTarget(@"\\server", "server", "Public"));
    }

    [Fact]
    public void UncToRelativeTarget_RejectsNonUnc()
    {
        Assert.Throws<InvalidTargetPathException>(() =>
            PathSecurityValidator.UncToRelativeTarget(@"C:\path", "nas01", "Public"));
    }

    [Fact]
    public void UncToRelativeTarget_HandlesMixedSlashes()
    {
        var result = PathSecurityValidator.UncToRelativeTarget(
            @"\\nas01\Public\uuid/hi_quality/AA-BB", "nas01", "Public");

        Assert.Equal("uuid/hi_quality/AA-BB", result);
    }
}
