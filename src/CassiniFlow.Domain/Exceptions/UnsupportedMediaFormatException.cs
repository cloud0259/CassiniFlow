namespace CassiniFlow.Domain.Exceptions;

public sealed class UnsupportedMediaFormatException : CassiniFlowException
{
    public UnsupportedMediaFormatException(
        string userTitle,
        string userExplanation,
        string? suggestedResolution = null,
        Exception? innerException = null)
        : base("ERR_UNSUPPORTED_FORMAT", userTitle, userExplanation, suggestedResolution, innerException)
    {
    }
}
