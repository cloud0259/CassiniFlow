namespace CassiniFlow.Domain.Exceptions;

public sealed class CalibrationMismatchException : CassiniFlowException
{
    public CalibrationMismatchException(
        string userTitle,
        string userExplanation,
        string? suggestedResolution = null,
        Exception? innerException = null)
        : base("ERR_CALIBRATION_MISMATCH", userTitle, userExplanation, suggestedResolution, innerException)
    {
    }
}
