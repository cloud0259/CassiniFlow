namespace CassiniFlow.Domain.Exceptions;

public sealed class CorruptedMediaException : CassiniFlowException
{
    public CorruptedMediaException(
        string userTitle,
        string userExplanation,
        string? suggestedResolution = null,
        Exception? innerException = null)
        : base("ERR_CORRUPT_MEDIA", userTitle, userExplanation, suggestedResolution, innerException)
    {
    }
}
