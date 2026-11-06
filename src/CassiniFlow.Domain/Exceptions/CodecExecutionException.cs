namespace CassiniFlow.Domain.Exceptions;

public sealed class CodecExecutionException : CassiniFlowException
{
    public CodecExecutionException(
        string userTitle,
        string userExplanation,
        string? suggestedResolution = null,
        Exception? innerException = null)
        : base("ERR_CODEC", userTitle, userExplanation, suggestedResolution, innerException)
    {
    }
}
