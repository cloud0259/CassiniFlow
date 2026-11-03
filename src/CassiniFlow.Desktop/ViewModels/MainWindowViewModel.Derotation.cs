using CommunityToolkit.Mvvm.ComponentModel;
using CassiniFlow.Domain.Services;
using CassiniFlow.Desktop.Localization;

namespace CassiniFlow.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    // Dérotation de champ (Monture Dobson / Alt-Azimutale)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DerotationInfoText))]
    private bool _enableFieldDerotation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DerotationInfoText))]
    private double _observerLatitudeDegrees = 49.62;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DerotationInfoText))]
    private double _targetAltitudeDegrees = 45.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DerotationInfoText))]
    private double _targetAzimuthDegrees = 180.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DerotationInfoText))]
    private double _admissibleBlurPixels = 1.5;

    public string DerotationInfoText
    {
        get
        {
            double rate = FieldDerotationEngine.ComputeFieldRotationRateDegPerMin(ObserverLatitudeDegrees, TargetAltitudeDegrees, TargetAzimuthDegrees);
            double sensorRadius = (CropWidth > 0 ? CropWidth : 1936) / 2.0;
            double maxT = FieldDerotationEngine.ComputeMaxExposureSeconds(ObserverLatitudeDegrees, TargetAltitudeDegrees, TargetAzimuthDegrees, sensorRadius, AdmissibleBlurPixels);
            return Loc.Format("Messages.DerotationRateInfo", rate, maxT);
        }
    }


    partial void OnEnableFieldDerotationChanged(bool value) => RequestPreviewUpdate(reloadFromDisk: false);
}
