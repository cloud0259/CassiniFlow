namespace CassiniFlow.Domain.Exceptions;

public sealed class InsufficientFramesException : CassiniFlowException
{
    public InsufficientFramesException(
        string userTitle,
        string userExplanation,
        string? suggestedResolution = null,
        Exception? innerException = null)
        : base("ERR_INSUFFICIENT_FRAMES", userTitle, userExplanation, suggestedResolution, innerException)
    {
    }
}
