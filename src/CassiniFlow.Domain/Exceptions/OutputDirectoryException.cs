namespace CassiniFlow.Domain.Exceptions;

public sealed class OutputDirectoryException : CassiniFlowException
{
    public OutputDirectoryException(
        string userTitle,
        string userExplanation,
        string? suggestedResolution = null,
        Exception? innerException = null)
        : base("ERR_OUTPUT_DIRECTORY", userTitle, userExplanation, suggestedResolution, innerException)
    {
    }
}
