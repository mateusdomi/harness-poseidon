using System.Diagnostics.CodeAnalysis;

namespace Harness.SharedKernel.Results;

public sealed class Result
{
    private Result(bool isSuccess, ErrorDescriptor error)
    {
        if (isSuccess == (error != ErrorDescriptor.None))
        {
            throw new ArgumentException("Success and error state are inconsistent.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public ErrorDescriptor Error { get; }

    public static Result Success() => new(true, ErrorDescriptor.None);

    public static Result Failure(ErrorDescriptor error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(false, error);
    }
}

[SuppressMessage(
    "Design",
    "CA1000:Do not declare static members on generic types",
    Justification = "Generic result factories preserve the TValue type without unsafe casts.")]
public sealed class Result<TValue>
{
    private readonly TValue? _value;

    private Result(TValue value)
    {
        _value = value;
        IsSuccess = true;
        Error = ErrorDescriptor.None;
    }

    private Result(ErrorDescriptor error)
    {
        _value = default;
        IsSuccess = false;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public ErrorDescriptor Error { get; }

    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("A failed result has no value.");

    public static Result<TValue> Success(TValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Result<TValue>(value);
    }

    public static Result<TValue> Failure(ErrorDescriptor error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<TValue>(error);
    }
}
