namespace Pos.Core;

/// <summary>
/// A request was rejected by validation. <see cref="Code"/> is a stable machine-readable
/// identifier and the message is safe to show to the operator.
/// </summary>
public sealed class PosValidationException : Exception
{
    public PosValidationException(string code, string message) : base(message) => Code = code;

    public PosValidationException() : this("validation_error", "Invalid request.") { }

    public PosValidationException(string message) : this("validation_error", message) { }

    public PosValidationException(string message, Exception innerException) : base(message, innerException) => Code = "validation_error";

    public string Code { get; }
}
