# CassiniFlow 🪐

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux-blue)](#prerequisites)
[![UI](https://img.shields.io/badge/UI-Avalonia%2011.2-purple)](https://avaloniaui.net/)

**CassiniFlow** is a modern, high-performance, cross-platform astrophotography preprocessing application built with .NET 10, C# 14, and Avalonia UI, with both a graphical interface and a command-line interface (CLI).

Designed as a modern alternative to traditional preprocessing tools such as PIPP, CassiniFlow handles large video streams, automatic planetary centering and stabilization, atmospheric seeing quality assessment, and low-memory frame selection. It also provides astrophotographic calibration and direct export to your favorite stacking applications, including **AutoStakkert!**, **Siril**, and **RegiStax**.

---

## 🌟 Key Features

### 📹 Supported Formats & Codecs (Input / Output)

- **SER (Lucam Recorder)**: High-speed 8-bit and 16-bit reading and writing (Mono, Bayer RAW, RGB), with precise per-frame UTC timestamp preservation (trailer timestamps).
- **FITS (Flexible Image Transport System)**: Reading and exporting monochrome and color 16-bit astronomical FITS files.
- **Universal Video Containers**: High-speed decoding of AVI, MP4, MOV, and other formats through **OpenCvSharp**, with automatic fallback and transparent transcoding via the **FFmpeg** CLI engine.
- **Standard Images**: Image sequences and standard image formats (BMP, etc.).
- **WinJUPOS Integration**: Automatic generation of WinJUPOS-compatible filenames (`YYYY-MM-DD-HHMM_S-System`) to facilitate subsequent planetary derotation.

### 🔬 High-Performance Processing Engine

- **Bayer Demosaicing (RAW Processing)**:
  - Supports all standard Bayer patterns: `RGGB`, `BGGR`, `GBRG`, `GRBG`.
  - Algorithms: *Bilinear*, *HQ Linear*, and *Malvar-He-Cutler* (gradient-corrected).

- **Planetary Detection & Centering**:
  - Automatic planetary limb detection and center of gravity (CoG) calculation.
  - Automatic apparent diameter measurement in pixels (`PlanetSizeAnalyzer`).
  - Automatic rotational alignment (`AutoRotationAligner`).
  - Automatic rejection of empty, corrupted, or overexposed frames.

- **Atmospheric Seeing Quality Assessment & Frame Selection**:
  - Sharpness evaluation using gradient energy and Laplacian variance.
  - **Single-Pass Bounded Top-K Heap**: Maintains RAM consumption below 900 MB, even when processing sequences containing more than 10,000 16-bit frames.

- **Dynamic or Fixed Cropping**: Centered stabilization and cropping (e.g. 512×512, 800×800, or automatically adjusted to the planetary disk).

- **Astrophotographic Calibration**:
  - Master Dark subtraction.
  - Master Flat division.
  - Automatic hot pixel removal.

- **Processing Modes**:
  - **Batch Mode**: Automated file-by-file processing.
  - **Join Mode**: Chronological concatenation of multiple consecutive video sequences into a single timestamped SER file.

### 🖥️ Dual User Interface

- **CassiniFlow.Desktop**: Modern and responsive graphical interface built with **Avalonia 11**:
  - Real-time image viewer (`AstroViewerControl`) featuring smooth zoom, pan, and centering crosshairs.
  - Native on-the-fly bilingual support (**English / French**).
  - Preconfigured processing profiles (Jupiter, Saturn, Moon, Sun, etc.).
  - Compatible with Windows and Linux (tested on Ubuntu / WSLg).

- **CassiniFlow.Cli**: Headless command-line executable, ideal for scripting pipelines and automating batch processing.

---

## 🏛️ Architecture & Design

CassiniFlow follows **Hexagonal Architecture (Ports & Adapters)** and **Domain-Driven Design (DDD)** principles.

```text
       ┌────────────────────────┐      ┌────────────────────────┐
       │   CassiniFlow.Desktop  │      │    CassiniFlow.Cli     │
       │     (Avalonia UI)      │      │       (Console)        │
       └───────────┬────────────┘      └───────────┬────────────┘
                   │                               │
                   ▼                               ▼
       ┌────────────────────────────────────────────────────────┐
       │                 CassiniFlow.Application                │
       │    Use Cases (RunPipeline, Preview, MeasurePlanet)     │
       │               Pipeline Step Pattern                   │
       └───────────┬───────────────────────────────▲────────────┘
                   │                               │
                   ▼                               │
       ┌────────────────────────┐                  │
       │   CassiniFlow.Domain   │                  │
       │ (Pure, zero dependencies)                 │
       │  AstroFrame, Centroid, │                  │
       │  QualityScore, Invariants                 │
       └────────────────────────┘                  │
                                                   │
       ┌───────────────────────────────────────────┴────────────┐
       │               CassiniFlow.Infrastructure               │
       │ Codecs (SER, FITS), Adapters (OpenCV, FFmpeg CLI)      │
       │ Readers/Writers Factories, ProfileSerializer           │
       └────────────────────────────────────────────────────────┘
```

- **Zero-Allocation Processing Loops**: Extensive use of `Span<T>`, `ReadOnlySpan<T>`, `Memory<T>`, and `ArrayPool<T>` to process tens of gigabytes of video data while minimizing garbage collection (GC) pressure.
- **Cross-Platform**: Native execution on Windows and Linux, supporting x64 and ARM64 architectures.

---

## 🚀 Getting Started

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) or later.
- *(Optional, for MP4 and non-SER video streams)*: [FFmpeg](https://ffmpeg.org/) installed and available in your system `PATH`.

### Building from Source

Clone the repository and build the solution:

```bash
git clone https://github.com/cloud0259/CassiniFlow.git
cd CassiniFlow
dotnet build CassiniFlow.slnx -c Release
```

### Running Unit Tests

```bash
dotnet test CassiniFlow.slnx
```

---

## 📖 Usage

### Graphical Interface (Desktop)

To launch the application with its graphical interface:

```bash
dotnet run --project src/CassiniFlow.Desktop/CassiniFlow.Desktop.csproj -c Release
```

1. **Drag and drop** your SER, FITS, or video files into the application.
2. Select a predefined profile (e.g. *Planetary*, *Solar/Lunar Surface*, etc.) or customize your processing parameters.
3. Preview centering and image quality using the interactive viewer.
4. Click **Start Processing** to export the calibrated and stabilized result.

### Command-Line Interface (CLI)

For headless processing or script-based automation:

```bash
dotnet run --project src/CassiniFlow.Cli/CassiniFlow.Cli.csproj -- [options]
```

#### CLI Command Examples

- **Center Jupiter, crop to 512×512, and retain the top 25% highest-quality frames:**

  ```bash
  CassiniFlow.Cli -i "captures/jupiter_raw.ser" -o "output/" --center --crop 512,512 --quality-limit 25% --debayer RGGB
  ```

- **Stabilize a lunar crescent and export as a 16-bit SER file:**

  ```bash
  CassiniFlow.Cli -i "captures/moon.avi" -o "output/moon_stabilized.ser" --center --format ser16
  ```

- **Join Mode: Concatenate multiple sequences with automatic WinJUPOS-compatible naming:**

  ```bash
  CassiniFlow.Cli -i "captures/*.ser" --mode join -o "output/" --winjupos
  ```

---

## 🤝 Contributing

Contributions are welcome!

If you would like to contribute to CassiniFlow:

1. Fork the repository.
2. Create a dedicated branch (`git checkout -b feat/new-feature`).
3. Follow the project's coding conventions (Hexagonal Architecture, DDD, `dotnet format`).
4. Write unit tests to validate your changes in `CassiniFlow.Tests`.
5. Submit a detailed Pull Request.

---

## 📄 License

This project is distributed under the permissive **MIT License**. See the [LICENSE](LICENSE) file for more details.
