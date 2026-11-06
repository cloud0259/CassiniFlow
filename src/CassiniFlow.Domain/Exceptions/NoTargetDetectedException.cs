namespace CassiniFlow.Domain.Exceptions;

public sealed class NoTargetDetectedException : CassiniFlowException
{
    public NoTargetDetectedException(
        string userTitle,
        string userExplanation,
        string? suggestedResolution = null,
        Exception? innerException = null)
        : base("ERR_NO_TARGET", userTitle, userExplanation, suggestedResolution, innerException)
    {
    }
}
