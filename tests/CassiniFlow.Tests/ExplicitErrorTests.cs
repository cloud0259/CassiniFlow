using System;
using System.IO;
using CassiniFlow.Desktop.ViewModels;
using CassiniFlow.Domain.Exceptions;
using CassiniFlow.Domain.Localization;
using CassiniFlow.Domain.Ports.Inbound;
using CassiniFlow.Domain.Services;
using Xunit;

namespace CassiniFlow.Tests;

public class ExplicitErrorTests
{
    private readonly IAstroLocalization _loc;

    public ExplicitErrorTests()
    {
        _loc = new AstroLocalizationService();
        _loc.SetLanguage(DomainLanguage.French);
    }

    [Fact]
    public void Explain_CustomException_PreservesDetails()
    {
        var ex = new NoTargetDetectedException("Cible absente", "Aucune planète trouvée", "Baissez le seuil");
        var details = CassiniFlowErrorExplainer.Explain(ex, _loc);

        Assert.Equal("ERR_NO_TARGET", details.ErrorCode);
        Assert.Equal("Cible absente", details.Title);
        Assert.Equal("Aucune planète trouvée", details.Message);
        Assert.Equal("Baissez le seuil", details.SuggestedResolution);
        Assert.Contains(nameof(NoTargetDetectedException), details.TechnicalDetails);
    }

    [Fact]
    public void Explain_OutOfMemoryException_ReturnsExplicitAdvice()
    {
        var ex = new OutOfMemoryException("Insufficient memory");
        var details = CassiniFlowErrorExplainer.Explain(ex, _loc);

        Assert.Equal("ERR_OUT_OF_MEMORY", details.ErrorCode);
        Assert.Contains("Mémoire", details.Title);
        Assert.NotNull(details.SuggestedResolution);
        Assert.Contains("Crop", details.SuggestedResolution);
    }

    [Fact]
    public void Explain_NotSupportedException_ReturnsSupportedFormatsAdvice()
    {
        var ex = new NotSupportedException("Format de fichier non supporté '.xyz'.");
        var details = CassiniFlowErrorExplainer.Explain(ex, _loc);

        Assert.Equal("ERR_UNSUPPORTED_FORMAT", details.ErrorCode);
        Assert.Contains("Format", details.Title);
        Assert.NotNull(details.SuggestedResolution);
        Assert.Contains("SER", details.SuggestedResolution);
    }

    [Fact]
    public void Explain_InvalidDataException_ReturnsCorruptMediaAdvice()
    {
        var ex = new InvalidDataException("File is corrupted or header is invalid.");
        var details = CassiniFlowErrorExplainer.Explain(ex, _loc);

        Assert.Equal("ERR_CORRUPT_MEDIA", details.ErrorCode);
        Assert.Contains("corrompu", details.Title);
        Assert.NotNull(details.SuggestedResolution);
    }

    [Fact]
    public void Explain_FfmpegFailure_ReturnsCodecAdvice()
    {
        var ex = new InvalidOperationException("Failed to start FFmpeg process.");
        var details = CassiniFlowErrorExplainer.Explain(ex, _loc);

        Assert.Equal("ERR_CODEC", details.ErrorCode);
        Assert.Contains("FFmpeg", details.Title);
        Assert.NotNull(details.SuggestedResolution);
        Assert.Contains("FFmpeg", details.SuggestedResolution);
    }

    [Fact]
    public void Explain_DirectoryNotFoundException_ReturnsDirectoryAdvice()
    {
        var ex = new DirectoryNotFoundException("Could not find path D:\\Output");
        var details = CassiniFlowErrorExplainer.Explain(ex, _loc);

        Assert.Equal("ERR_OUTPUT_DIRECTORY", details.ErrorCode);
        Assert.Contains("Dossier", details.Title);
        Assert.NotNull(details.SuggestedResolution);
    }

    [Fact]
    public void ViewModel_ShowErrorAndDismiss_WorksAsExpected()
    {
        var vm = TestFactory.CreateMainWindowViewModel();
        Assert.False(vm.HasActiveError);

        var details = new CassiniFlowErrorDetails("ERR_TEST", "Erreur test", "Explication test", "Conseil test", "Stack");
        vm.ShowError(details);

        Assert.True(vm.HasActiveError);
        Assert.Equal("ERR_TEST", vm.ActiveErrorCode);
        Assert.Equal("Erreur test", vm.ActiveErrorTitle);
        Assert.Equal("Explication test", vm.ActiveErrorMessage);
        Assert.Equal("Conseil test", vm.ActiveErrorSuggestion);
        Assert.True(vm.HasActiveErrorSuggestion);

        vm.DismissError();
        Assert.False(vm.HasActiveError);
        Assert.Empty(vm.ActiveErrorCode);
        Assert.Empty(vm.ActiveErrorTitle);
    }
}
