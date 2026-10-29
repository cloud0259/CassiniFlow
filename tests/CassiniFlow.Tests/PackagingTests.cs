using System.IO;
using Xunit;

namespace CassiniFlow.Tests;

public class PackagingTests
{
    private static string FindRepositoryRoot()
    {
        string? current = Directory.GetCurrentDirectory();
        while (current != null && !File.Exists(Path.Combine(current, "CassiniFlow.slnx")))
        {
            current = Directory.GetParent(current)?.FullName;
        }
        return current ?? Directory.GetCurrentDirectory();
    }

    [Fact]
    public void WindowsInnoSetupScript_ExistsAndHasRequiredSections()
    {
        string root = FindRepositoryRoot();
        string issPath = Path.Combine(root, "packaging", "windows", "CassiniFlow.iss");

        Assert.True(File.Exists(issPath), $"Le fichier Inno Setup '{issPath}' est manquant.");

        string issContent = File.ReadAllText(issPath);
        Assert.Contains("[Setup]", issContent);
        Assert.Contains("AppName={#MyAppName}", issContent);
        Assert.Contains("OutputBaseFilename=CassiniFlow-Setup-x64", issContent);
        Assert.Contains("[Files]", issContent);
        Assert.Contains("[Icons]", issContent);
        Assert.Contains("[Registry]", issContent);
        Assert.Contains("[Run]", issContent);
        Assert.Contains("Software\\Classes\\.ser", issContent);
        Assert.Contains("Software\\Classes\\.fits", issContent);
    }

    [Fact]
    public void WindowsBuildInstallerScript_Exists()
    {
        string root = FindRepositoryRoot();
        string psScript = Path.Combine(root, "packaging", "windows", "build-installer.ps1");

        Assert.True(File.Exists(psScript), $"Le script PowerShell '{psScript}' est manquant.");
        string script = File.ReadAllText(psScript);
        Assert.Contains("dotnet publish", script);
        Assert.Contains("CassiniFlow.iss", script);
    }

    [Fact]
    public void LinuxDesktopEntry_ExistsAndFollowsFreeDesktopSpec()
    {
        string root = FindRepositoryRoot();
        string desktopPath = Path.Combine(root, "packaging", "linux", "cassiniflow.desktop");

        Assert.True(File.Exists(desktopPath), $"Le fichier desktop '{desktopPath}' est manquant.");

        string content = File.ReadAllText(desktopPath);
        Assert.Contains("[Desktop Entry]", content);
        Assert.Contains("Name=CassiniFlow", content);
        Assert.Contains("Exec=cassiniflow %F", content);
        Assert.Contains("Icon=cassiniflow", content);
        Assert.Contains("Categories=Science;Astronomy;", content);
        Assert.Contains("MimeType=application/x-ser;", content);
    }

    [Fact]
    public void LinuxInstallationScripts_ExistAndAreConfigured()
    {
        string root = FindRepositoryRoot();
        string installSh = Path.Combine(root, "packaging", "linux", "install.sh");
        string uninstallSh = Path.Combine(root, "packaging", "linux", "uninstall.sh");
        string buildDebSh = Path.Combine(root, "packaging", "linux", "build-deb.sh");

        Assert.True(File.Exists(installSh), $"Le script install.sh '{installSh}' est manquant.");
        Assert.True(File.Exists(uninstallSh), $"Le script uninstall.sh '{uninstallSh}' est manquant.");
        Assert.True(File.Exists(buildDebSh), $"Le script build-deb.sh '{buildDebSh}' est manquant.");

        string installContent = File.ReadAllText(installSh);
        Assert.Contains("/opt/cassiniflow", installContent);
        Assert.Contains(".local/share/cassiniflow", installContent);

        string debContent = File.ReadAllText(buildDebSh);
        Assert.Contains("dpkg-deb --build", debContent);
        Assert.Contains("cassiniflow", debContent);
    }
}

