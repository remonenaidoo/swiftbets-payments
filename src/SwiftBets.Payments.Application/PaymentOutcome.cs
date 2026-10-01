using SwiftBets.Payments.Domain;

namespace SwiftBets.Payments.Application;

public sealed record PaymentOutcome<T>(T? Value, PaymentError? Error)
{
    public static PaymentOutcome<T> Ok(T value) => new(value, null);

    public static PaymentOutcome<T> Fail(PaymentError error) => new(default, error);
}

/// <summary>What applying a provider event did; Unknown means the reference is not one of ours.</summary>
public enum ApplyResult
{
    Applied,
    Ignored,
    Unknown,
}
