using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using UnifiedCalendar.App.Shell;
using UnifiedCalendar.Core;
using Xunit;
using ApplicationType = UnifiedCalendar.App.App;

namespace UnifiedCalendar.Tests;

public sealed class Phase8InstallerTests
{
    private const string IconRelativePath = "Resources\\UnifiedCalendar.ico";
    private static readonly string[] TestOnlyPackages =
    [
        "Microsoft.NET.Test.Sdk",
        "NSubstitute",
        "xunit.v3",
        "xunit.runner.visualstudio",
    ];

    [Fact]
    public void ApplicationAssemblyContainsReleaseIdentityAndExistingMetadata()
    {
        var assembly = typeof(ApplicationType).Assembly;

        Assert.Equal(
            "UnifiedCalendar",
            assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
        Assert.Equal(
            "Logic and Design Inc.",
            assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company);
        Assert.Equal(
            "UnifiedCalendar",
            assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title);
        Assert.Contains(
            "2026 Logic and Design Inc.",
            assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright);
        Assert.Equal(
            "Google と Microsoft 365 の予定を1つの時系列へまとめて表示する常駐アプリ",
            assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description);
        Assert.Equal(new Version(1, 0, 0, 0), assembly.GetName().Version);
        Assert.Equal(
            "1.0.0",
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

        var metadataKeys = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Select(attribute => attribute.Key)
            .ToArray();
        Assert.Contains("BuildTimestampUtc", metadataKeys);
        Assert.Contains("GoogleClientId", metadataKeys);
        Assert.Contains("GoogleClientSecret", metadataKeys);
        Assert.Contains("MicrosoftClientId", metadataKeys);
    }

    [Fact]
    public void ThirdPartyNoticesContainsRequiredComponentsAndLicenseTexts()
    {
        var root = FindRepositoryRoot();
        var noticesPath = Path.Combine(root, "THIRD-PARTY-NOTICES.txt");
        var bytes = File.ReadAllBytes(noticesPath);
        var notices = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true).GetString(bytes);

        Assert.DoesNotContain('\r', notices);
        Assert.Contains("UnifiedCalendar Third-Party Notices", notices);
        Assert.Contains("It does not define license terms for UnifiedCalendar itself.", notices);
        Assert.Contains("No code in those binaries has been modified.", notices);

        string[] requiredComponents =
        [
            ".NET 8 runtime and libraries",
            "Microsoft.Extensions.",
            "Microsoft.Identity.Client",
            "Microsoft.IdentityModel.Abstractions",
            "Microsoft.Graph",
            "Microsoft.Graph.Core",
            "Microsoft.Kiota.Abstractions",
            "Azure.Core",
            "System.Security.Cryptography.ProtectedData",
            "CommunityToolkit.Mvvm",
            "Newtonsoft.Json",
            "Google.Apis",
            "Google.Apis.Auth",
            "Google.Apis.Core",
            "Google.Apis.Calendar.v3",
            "Serilog",
            "Serilog.Extensions.Hosting",
            "Serilog.Extensions.Logging",
            "Serilog.Formatting.Compact",
            "Serilog.Sinks.File",
            "Std.UriTemplate",
        ];
        Assert.All(requiredComponents, component => Assert.Contains(component, notices));

        Assert.Contains("Version: 2.0.8", notices);
        Assert.Contains("Copyright: Std.UriTemplate (package authors)", notices);
        Assert.Contains(
            "Permission is hereby granted, free of charge, to any person obtaining a copy",
            notices);
        Assert.Contains(
            "TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION",
            notices);
        Assert.Contains("END OF TERMS AND CONDITIONS", notices);
        Assert.Contains("APPENDIX: How to apply the Apache License to your work.", notices);
        Assert.DoesNotContain("Copyright © 2026 Logic and Design Inc.", notices);
    }

    [Fact]
    public void EveryResolvedApplicationPackageHasAThirdPartyNotice()
    {
        var root = FindRepositoryRoot();
        var notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.txt"));
        var assetsPath = Path.Combine(
            root,
            "src",
            "UnifiedCalendar.App",
            "obj",
            "project.assets.json");
        Assert.True(File.Exists(assetsPath), $"Restore output was not found: {assetsPath}");

        using var assets = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
        var packageNames = assets.RootElement
            .GetProperty("libraries")
            .EnumerateObject()
            .Where(library =>
                library.Value.GetProperty("type").GetString() == "package")
            .Select(library =>
            {
                var separator = library.Name.LastIndexOf('/');
                return separator >= 0 ? library.Name[..separator] : library.Name;
            })
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotEmpty(packageNames);
        Assert.All(
            packageNames,
            packageName => Assert.Contains(
                $"- {packageName}\n",
                notices,
                StringComparison.Ordinal));

        foreach (var testOnlyPackage in TestOnlyPackages)
        {
            Assert.DoesNotContain(testOnlyPackage, packageNames);
            Assert.DoesNotContain($"- {testOnlyPackage}\n", notices, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RootPowerShellScriptsWithNonAsciiContentHaveUtf8Bom()
    {
        var root = FindRepositoryRoot();
        var scripts = Directory
            .EnumerateFiles(root, "*.ps1", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotEmpty(scripts);
        Assert.All(scripts, script =>
        {
            var bytes = File.ReadAllBytes(script);
            var hasUtf8Bom = bytes.Length >= 3
                && bytes[0] == 0xef
                && bytes[1] == 0xbb
                && bytes[2] == 0xbf;
            var contentOffset = hasUtf8Bom ? 3 : 0;
            var hasNonAsciiContent = bytes
                .Skip(contentOffset)
                .Any(value => value > 0x7f);

            if (hasNonAsciiContent)
            {
                Assert.True(
                    hasUtf8Bom,
                    $"Root PowerShell script contains non-ASCII content without a UTF-8 BOM: {Path.GetFileName(script)}");
            }
        });
    }

    [Fact]
    public void OneMultiFrameIconFeedsExecutableTrayAndBothWindows()
    {
        var root = FindRepositoryRoot();
        var sourceFiles = Directory
            .EnumerateFiles(root, "*.ico", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(root, path))
            .ToArray();
        var iconPath = Assert.Single(sourceFiles);
        Assert.EndsWith(IconRelativePath, iconPath, StringComparison.OrdinalIgnoreCase);

        using (var reader = new BinaryReader(File.OpenRead(iconPath)))
        {
            Assert.Equal(0, reader.ReadUInt16());
            Assert.Equal(1, reader.ReadUInt16());
            var count = reader.ReadUInt16();
            Assert.Equal(8, count);
            var sizes = new List<int>(count);
            var bitsPerPixel = new List<int>(count);
            for (var index = 0; index < count; index++)
            {
                var width = reader.ReadByte();
                var height = reader.ReadByte();
                _ = reader.ReadByte();
                _ = reader.ReadByte();
                _ = reader.ReadUInt16();
                bitsPerPixel.Add(reader.ReadUInt16());
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                sizes.Add(width == 0 ? 256 : width);
                Assert.Equal(width, height);
            }

            Assert.Equal([16, 24, 32, 48, 64, 96, 128, 256], sizes.Order().ToArray());
            Assert.All(bitsPerPixel, value => Assert.Equal(32, value));
        }

        var project = XDocument.Load(Path.Combine(
            root,
            "src",
            "UnifiedCalendar.App",
            "UnifiedCalendar.App.csproj"));
        Assert.Equal(
            IconRelativePath,
            project.Descendants("ApplicationIcon").Single().Value);
        Assert.Equal(
            IconRelativePath,
            project.Descendants("Resource").Single(element =>
                element.Attribute("Include")?.Value == IconRelativePath).Attribute("Include")?.Value);

        var mainWindow = File.ReadAllText(Path.Combine(
            root,
            "src",
            "UnifiedCalendar.App",
            "MainWindow.xaml"));
        var settingsWindow = File.ReadAllText(Path.Combine(
            root,
            "src",
            "UnifiedCalendar.App",
            "SettingsWindow.xaml"));
        var trayAdapter = File.ReadAllText(Path.Combine(
            root,
            "src",
            "UnifiedCalendar.App",
            "Shell",
            "TrayIconService.cs"));
        Assert.Contains("Icon=\"Resources/UnifiedCalendar.ico\"", mainWindow);
        Assert.Contains("Icon=\"Resources/UnifiedCalendar.ico\"", settingsWindow);
        Assert.Contains("Resources/UnifiedCalendar.ico", trayAdapter);
        Assert.Contains("SystemInformation.SmallIconSize", trayAdapter);
        Assert.Contains("_icon.Dispose();", trayAdapter);
        Assert.DoesNotContain("SystemIcons.Application", trayAdapter);
    }

    [Fact]
    public void RunningApplicationMutexExistsWithoutOwnershipUntilDisposed()
    {
        var mutexName = $@"Local\UnifiedCalendar.Tests.Running.{Guid.NewGuid():N}";

        using (var runningApplicationMutex = new RunningApplicationMutex(mutexName))
        {
            Assert.True(Mutex.TryOpenExisting(mutexName, out var openedMutex));
            using (openedMutex)
            {
                Assert.True(openedMutex.WaitOne(TimeSpan.Zero));
                openedMutex.ReleaseMutex();
            }
        }

        Assert.False(Mutex.TryOpenExisting(mutexName, out var disposedMutex));
        disposedMutex?.Dispose();
    }

    [Fact]
    public void InstallerDefinitionKeepsPerUserUpgradeAndSigningContracts()
    {
        var root = FindRepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(root, "installer", "UnifiedCalendar.iss"));
        var buildInstaller = File.ReadAllText(Path.Combine(root, "build-installer.ps1"));
        var regularBuild = File.ReadAllText(Path.Combine(root, "build.ps1"));
        var appIdentity = File.ReadAllText(Path.Combine(
            root,
            "src",
            "UnifiedCalendar.Core",
            "AppIdentity.cs"));
        var application = File.ReadAllText(Path.Combine(
            root,
            "src",
            "UnifiedCalendar.App",
            "App.xaml.cs"));
        var singleInstanceCoordinator = File.ReadAllText(Path.Combine(
            root,
            "src",
            "UnifiedCalendar.App",
            "Shell",
            "SingleInstanceCoordinator.cs"));

        Assert.Contains("AppId={{B4ABCCB7-6C6A-4EEC-A270-91B8F1054CD8}", installer);
        Assert.Contains("Never change it", installer);
        Assert.Contains(
            "#define AppVersion GetStringFileInfo(AppExePath, \"ProductVersion\")",
            installer);
        Assert.Contains(
            "#define NumericAppVersion GetVersionNumbersString(AppExePath)",
            installer);
        Assert.Contains(
            "ProductVersion is user-visible, while NumericAppVersion supplies the four-part numeric executable version resource",
            installer);
        Assert.Contains("AppVersion={#AppVersion}", installer);
        Assert.Contains("OutputBaseFilename={#AppName}-Setup-{#AppVersion}", installer);
        Assert.Contains("VersionInfoVersion={#NumericAppVersion}", installer);
        Assert.Contains("GetVersionNumbersString(AppExePath)", installer);
        Assert.DoesNotMatch(
            @"(?m)^(?:AppVersion|OutputBaseFilename|VersionInfoVersion)=.*\b\d+\.\d+\.\d+",
            installer);
        Assert.Contains("PrivilegesRequired=lowest", installer);
        Assert.DoesNotContain("PrivilegesRequiredOverridesAllowed", installer);
        Assert.Contains("ArchitecturesAllowed=x64compatible", installer);
        Assert.Contains("Excludes: \"*.exe,*.dll\"", installer);
        Assert.Contains("Source: \"{#PublishDir}\\*.exe\"", installer);
        Assert.Contains("Source: \"{#PublishDir}\\*.dll\"", installer);
        var thirdPartyNoticesSource = Assert.Single(
            installer.Split('\n'),
            line => line.Contains("THIRD-PARTY-NOTICES.txt", StringComparison.Ordinal));
        Assert.Equal(
            "Source: \"..\\THIRD-PARTY-NOTICES.txt\"; DestDir: \"{app}\"; Flags: ignoreversion",
            thirdPartyNoticesSource.TrimEnd('\r'));
        Assert.DoesNotContain("signonce", thirdPartyNoticesSource, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, CountOccurrences(installer, "signonce"));
        Assert.DoesNotContain("{#PublishDir}\\*\"; DestDir: \"{app}\"; Flags: ignoreversion recursesubdirs createallsubdirs signonce", installer);
        Assert.DoesNotContain("UnifiedCalendar.App.exe\"; DestDir", installer);
        Assert.Contains("CloseApplications=yes", installer);
        Assert.Contains("CloseApplicationsFilter={#AppExeName}", installer);
        Assert.Contains(
            "ErrorCloseApplications=UnifiedCalendar を自動的に終了できませんでした。通知領域の UnifiedCalendar アイコンを右クリックし、「終了」を選んでから続行してください。",
            installer);
        Assert.Contains("#define RunningApplicationMutexName \"Local\\UnifiedCalendar.Running\"", installer);
        Assert.Contains("Never change after release", installer);
        Assert.Contains("[Code]", installer);
        Assert.Contains("function InitializeUninstall(): Boolean;", installer);
        Assert.Contains("while CheckForMutexes('{#RunningApplicationMutexName}') do", installer);
        Assert.Contains("MB_OKCANCEL", installer);
        Assert.Contains("= IDCANCEL then", installer);
        Assert.Contains("通知領域の UnifiedCalendar アイコンを右クリックし、「終了」を選んでから続行してください", installer);
        Assert.DoesNotMatch(@"(?m)^\s*AppMutex\s*=", installer);
        Assert.Contains("uninsdeletevalue", installer);
        Assert.DoesNotContain("[UninstallDelete]", installer);
        Assert.DoesNotMatch(@"(?m)^\s*LicenseFile\s*=", installer);
        Assert.Contains("{autoprograms}\\{#AppName}", installer);
        Assert.DoesNotContain("{userdesktop}", installer);
        Assert.Contains("#ifdef SIGN", installer);
        Assert.Contains("SignTool=MySignTool", installer);
        Assert.Contains("SignToolRetryCount=2", installer);
        Assert.Contains("SignedUninstaller=yes", installer);
        Assert.Contains("[switch]$NoSign", buildInstaller);
        Assert.DoesNotContain("[switch]$Sign", buildInstaller);
        Assert.Contains("if (-not $NoSign)", buildInstaller);
        Assert.Contains("$arguments += '/DSIGN'", buildInstaller);
        Assert.Contains("[switch]$Quiet", buildInstaller);
        Assert.Contains("[Alias('Silent')]", buildInstaller);
        Assert.Contains("if ($Quiet)", buildInstaller);
        Assert.Contains("$arguments += '/Qp'", buildInstaller);
        Assert.Contains("Code signing is disabled", buildInstaller);
        Assert.Contains("Get-AuthenticodeSignature", buildInstaller);
        Assert.Contains("$generatedInstallerPath", buildInstaller);
        Assert.Contains("$publishedExecutablePath", buildInstaller);
        Assert.Contains(".VersionInfo.ProductVersion", buildInstaller);
        Assert.Contains("UnifiedCalendar-Setup-$productVersion.exe", buildInstaller);
        Assert.DoesNotContain(".VersionInfo.FileVersion", buildInstaller);
        Assert.Contains("[switch]$AllowMissingCredentials", buildInstaller);
        Assert.Contains("artifacts\\build-metadata\\win-x64-client-credentials.json", buildInstaller);
        Assert.Contains("ConvertFrom-Json", buildInstaller);
        Assert.Contains("Published application is missing embedded client credentials", buildInstaller);
        Assert.Contains("Missing embedded client credentials were explicitly allowed", buildInstaller);
        Assert.Contains("Credential build metadata was not found", buildInstaller);
        Assert.DoesNotContain("$env:", buildInstaller, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("artifacts\\build-metadata\\win-x64-client-credentials.json", regularBuild);
        Assert.DoesNotContain(
            "artifacts\\publish\\win-x64\\win-x64-client-credentials.json",
            regularBuild);
        Assert.Contains("googleClientIdEmbedded = $googleClientIdProvided", regularBuild);
        Assert.Contains("googleClientSecretEmbedded = $googleClientSecretProvided", regularBuild);
        Assert.Contains("microsoftClientIdEmbedded = $microsoftClientIdProvided", regularBuild);
        Assert.Contains("INCOMPLETE Google credential pair", regularBuild);
        Assert.Contains("UnifiedCalendar__Google__ClientId", regularBuild);
        Assert.Contains("UnifiedCalendar__Google__ClientSecret", regularBuild);
        Assert.Contains("UnifiedCalendar__Microsoft__ClientId", regularBuild);
        Assert.True(
            regularBuild.LastIndexOf("Write-Warning $credentialWarning", StringComparison.Ordinal)
            > regularBuild.LastIndexOf("dotnet publish", StringComparison.Ordinal));
        Assert.DoesNotContain("ISCC", regularBuild, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "THIRD-PARTY-NOTICES",
            regularBuild,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Credential", installer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(@"Local\UnifiedCalendar.Running", AppIdentity.RunningApplicationMutexName);
        Assert.Contains("RunningApplicationMutexName = @\"Local\\UnifiedCalendar.Running\"", appIdentity);
        Assert.Contains("Never change after release", appIdentity);
        Assert.Contains("_runningApplicationMutex = new RunningApplicationMutex();", application);
        Assert.Contains("_runningApplicationMutex?.Dispose();", application);
        Assert.DoesNotContain("RunningApplicationMutex", singleInstanceCoordinator);
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }

    private static bool IsGeneratedPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var firstSegment = relative.Split(Path.DirectorySeparatorChar)[0];
        return firstSegment.Equals("artifacts", StringComparison.OrdinalIgnoreCase)
            || relative.Split(Path.DirectorySeparatorChar).Any(segment =>
                segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "UnifiedCalendar.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root could not be located.");
    }
}

[Collection(WpfApplicationCollection.Name)]
public sealed class Phase8InstallerWpfTests
{
    [Fact]
    public void TrayAdapterLoadsSmallIconAndDisposesIdempotently()
    {
        var adapter = new NotifyIconAdapter();
        var iconField = typeof(NotifyIconAdapter).GetField(
            "_icon",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var icon = Assert.IsType<System.Drawing.Icon>(iconField?.GetValue(adapter));

        var requestedSize = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
        var expectedFrameSize = new[] { 16, 24, 32, 48, 64, 96, 128, 256 }
            .OrderBy(size => Math.Abs(size - requestedSize))
            .ThenByDescending(size => size)
            .First();
        Assert.Equal(new System.Drawing.Size(expectedFrameSize, expectedFrameSize), icon.Size);

        adapter.Dispose();
        adapter.Dispose();
    }
}
