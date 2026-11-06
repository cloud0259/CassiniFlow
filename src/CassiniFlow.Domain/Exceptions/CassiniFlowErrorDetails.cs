namespace CassiniFlow.Domain.Exceptions;

public sealed record CassiniFlowErrorDetails(
    string ErrorCode,
    string Title,
    string Message,
    string? SuggestedResolution,
    string TechnicalDetails);
