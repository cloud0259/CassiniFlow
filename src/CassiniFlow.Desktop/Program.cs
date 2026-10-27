using Avalonia;

namespace CassiniFlow.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        bool isWslOrLinuxSw = OperatingSystem.IsLinux() &&
            (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")) ||
             Directory.Exists("/mnt/wslg") ||
             File.Exists("/proc/sys/fs/binfmt_misc/WSLInterop"));

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions
            {
                RenderingMode = isWslOrLinuxSw
                    ? [X11RenderingMode.Software]
                    : [X11RenderingMode.Glx, X11RenderingMode.Egl, X11RenderingMode.Software],
                UseDBusMenu = false,
                UseDBusFilePicker = !isWslOrLinuxSw
            })
            .WithInterFont()
            .LogToTrace();
    }
}
