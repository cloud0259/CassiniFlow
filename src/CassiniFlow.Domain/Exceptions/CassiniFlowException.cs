namespace CassiniFlow.Domain.Exceptions;

public class CassiniFlowException : Exception
{
    public string ErrorCode { get; }
    public string UserTitle { get; }
    public string UserExplanation { get; }
    public string? SuggestedResolution { get; }

    public CassiniFlowException(
        string errorCode,
        string userTitle,
        string userExplanation,
        string? suggestedResolution = null,
        Exception? innerException = null)
        : base($"{userTitle}: {userExplanation}", innerException)
    {
        ErrorCode = errorCode;
        UserTitle = userTitle;
        UserExplanation = userExplanation;
        SuggestedResolution = suggestedResolution;
    }
}
