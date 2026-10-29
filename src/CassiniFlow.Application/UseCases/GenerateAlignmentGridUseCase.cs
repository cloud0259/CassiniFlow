using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Inbound;
using CassiniFlow.Domain.Services;

namespace CassiniFlow.Application.UseCases;

public sealed class GenerateAlignmentGridUseCase : IGenerateAlignmentGridUseCase
{
    public AlignmentGrid GenerateGrid(AstroFrame frame, StackingOptions options)
    {
        return AlignmentGridGenerator.GenerateGrid(frame, options);
    }
}

