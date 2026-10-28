using System;
using System.Collections.Generic;
using CassiniFlow.Domain.Models;
using CassiniFlow.Infrastructure.Metadata;
using Xunit;

namespace CassiniFlow.Tests;

public class AstroMetadataExtractorTests
{
    [Fact]
    public void ExtractFromFitsCards_ParsesExposureIsoGainAndInstrument()
    {
        var cards = new List<string>
        {
            "SIMPLE  =                    T / file does conform to FITS standard",
            "BITPIX  =                   16 / number of bits per data pixel",
            "EXPTIME =                0.020 / Exposure time in seconds",
            "ISO     =                  400 / ISO speed rating",
            "GAIN    =                110.0 / Sensor analog gain",
            "INSTRUME= 'ZWO ASI678MC'       / Camera model",
            "TELESCOP= 'Celestron C11'      / Telescope",
            "DATE-OBS= '2026-10-28T21:30:00'/ Observation date"
        };

        var meta = AstroMetadataExtractor.ExtractFromFitsCards(cards);

        Assert.Equal(0.020, meta.ExposureTimeSeconds);
        Assert.Equal("20.0 ms", meta.EffectiveExposureText);
        Assert.Equal("1/50 s", meta.EffectiveShutterSpeedText);
        Assert.Equal(400, meta.Iso);
        Assert.Equal(110.0, meta.Gain);
        Assert.Equal("ISO 400 (Gain 110)", meta.IsoOrGainText);
        Assert.Equal("ZWO ASI678MC", meta.Camera);
        Assert.Equal("Celestron C11", meta.Telescope);
    }

    [Fact]
    public void ParseSidecarContent_SharpCapAndFireCapture_ParsesAccurately()
    {
        string sharpCapSidecar = @"
[ZWO ASI585MC]
FrameType=Light
Exposure=15.000ms
Gain=180
Shutter=1/67s
Camera=ZWO ASI585MC
Filter=IR-Pass 685nm
";

        var meta = AstroMetadataExtractor.ParseSidecarContent(sharpCapSidecar);

        Assert.Equal(0.015, meta.ExposureTimeSeconds);
        Assert.Equal("15.0 ms", meta.EffectiveExposureText);
        Assert.Equal("1/67 s", meta.EffectiveShutterSpeedText);
        Assert.Equal(180.0, meta.Gain);
        Assert.Equal("Gain 180", meta.IsoOrGainText);
        Assert.Equal("ZWO ASI585MC", meta.Camera);
        Assert.Equal("IR-Pass 685nm", meta.Filter);
    }

    [Theory]
    [InlineData(0.00005, "50 µs")]
    [InlineData(0.005, "5.0 ms")]
    [InlineData(1.5, "1.50 s")]
    [InlineData(75.0, "1m 15s")]
    public void FormatExposure_FormatsVariousScalesCleanly(double seconds, string expected)
    {
        string formatted = AstroMediaMetadata.FormatExposure(seconds);
        Assert.Equal(expected, formatted);
    }

    [Theory]
    [InlineData(0.001, "1/1000 s")]
    [InlineData(0.02, "1/50 s")]
    [InlineData(0.5, "1/2 s")]
    [InlineData(2.0, "2.00 s")]
    public void FormatShutterSpeed_FormatsVariousScalesCleanly(double seconds, string expected)
    {
        string formatted = AstroMediaMetadata.FormatShutterSpeed(seconds);
        Assert.Equal(expected, formatted);
    }
}
