using Harness.SharedKernel.Results;

namespace Harness.UnitTests.SharedKernel;

public sealed class ResultTests
{
    private static readonly ErrorDescriptor ValidationError = new("validation.invalid", "errors.validation.invalid");

    [Fact]
    public void SuccessHasNoError()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Equal(ErrorDescriptor.None, result.Error);
    }

    [Fact]
    public void FailurePreservesStableErrorCodeAndMessageKey()
    {
        var result = Result.Failure(ValidationError);

        Assert.True(result.IsFailure);
        Assert.Equal("validation.invalid", result.Error.Code);
        Assert.Equal("errors.validation.invalid", result.Error.MessageKey);
    }

    [Fact]
    public void GenericSuccessExposesValue()
    {
        var result = Result<string>.Success("value");

        Assert.True(result.IsSuccess);
        Assert.Equal("value", result.Value);
    }

    [Fact]
    public void GenericFailureDoesNotExposeValue()
    {
        var result = Result<string>.Failure(ValidationError);

        Assert.True(result.IsFailure);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void FailureRejectsNullError()
    {
        Assert.Throws<ArgumentNullException>(() => Result.Failure(null!));
        Assert.Throws<ArgumentNullException>(() => Result<string>.Failure(null!));
    }

    [Fact]
    public void GenericSuccessRejectsNullReference()
    {
        Assert.Throws<ArgumentNullException>(() => Result<string>.Success(null!));
    }
}
