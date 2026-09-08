[CmdletBinding()]
param(
    [switch]$NoSign,

    [switch]$AllowMissingCredentials,

    [Alias('Silent')]
    [switch]$Quiet,

    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
$installerScriptPath = Join-Path $PSScriptRoot 'installer\UnifiedCalendar.iss'
$publishedExecutablePath = Join-Path $PSScriptRoot 'artifacts\publish\win-x64\UnifiedCalendar.App.exe'
$installerOutputPath = Join-Path $PSScriptRoot 'artifacts\installer'
$termsMarkdownPath = Join-Path $PSScriptRoot 'docs\terms.md'
$installerInputPath = Join-Path $PSScriptRoot 'artifacts\installer-input'
$installerLicensePath = Join-Path $installerInputPath 'TERMS.txt'
$buildMetadataPath = Join-Path $PSScriptRoot 'artifacts\build-metadata\win-x64-client-credentials.json'

if (-not (Test-Path -LiteralPath $publishedExecutablePath -PathType Leaf)) {
    throw 'Published application not found. Run .\build.ps1 before building the installer.'
}

if ($AllowMissingCredentials) {
    Write-Warning 'AllowMissingCredentials override is enabled. Credential validation failures will not stop this installer build.'
}

if (-not (Test-Path -LiteralPath $buildMetadataPath -PathType Leaf)) {
    Write-Warning 'Credential build metadata was not found. Embedded credential validation was skipped for this legacy published output.'
}
else {
    $credentialStatus = Get-Content -LiteralPath $buildMetadataPath -Raw | ConvertFrom-Json
    $requiredStatusProperties = @(
        'googleClientIdEmbedded',
        'googleClientSecretEmbedded',
        'microsoftClientIdEmbedded'
    )
    foreach ($requiredStatusProperty in $requiredStatusProperties) {
        if ($null -eq $credentialStatus.PSObject.Properties[$requiredStatusProperty]) {
            throw "Credential build metadata is invalid: missing $requiredStatusProperty."
        }
    }

    $missingEmbeddedCredentials = @()
    if ($credentialStatus.googleClientIdEmbedded -ne $true) {
        $missingEmbeddedCredentials += 'Google ClientId'
    }
    if ($credentialStatus.googleClientSecretEmbedded -ne $true) {
        $missingEmbeddedCredentials += 'Google ClientSecret'
    }
    if ($credentialStatus.microsoftClientIdEmbedded -ne $true) {
        $missingEmbeddedCredentials += 'Microsoft ClientId'
    }

    if ($missingEmbeddedCredentials.Count -gt 0) {
        $missingDescription = $missingEmbeddedCredentials -join ', '
        if (-not $AllowMissingCredentials) {
            throw "Published application is missing embedded client credentials: $missingDescription. Re-run build.ps1 with the required credentials, or pass -AllowMissingCredentials for an intentional non-production build."
        }

        Write-Warning "Missing embedded client credentials were explicitly allowed: $missingDescription."
    }
}

if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $isccCommand = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($null -ne $isccCommand) {
        $IsccPath = $isccCommand.Source
    }
    else {
        $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
        $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
        $candidates = @(
            (Join-Path $programFilesX86 'Inno Setup 6\ISCC.exe'),
            (Join-Path $programFiles 'Inno Setup 6\ISCC.exe')
        )
        $IsccPath = $candidates |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
    }
}

if (
    [string]::IsNullOrWhiteSpace($IsccPath) -or
    -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)
) {
    throw 'ISCC.exe was not found. Install Inno Setup 6 or pass -IsccPath.'
}

$arguments = @()
if ($Quiet) {
    $arguments += '/Qp'
}

if (-not $NoSign) {
    $arguments += '/DSIGN'
}
else {
    Write-Warning 'Code signing is disabled. No signatures will be added to the installer or bundled application binaries.'
}

$termsMarkdown = Get-Content -LiteralPath $termsMarkdownPath -Raw -Encoding UTF8
$normalizedTerms = $termsMarkdown -replace "`r`n?", "`n"
$termsLines = $normalizedTerms -split "`n"
if ($termsLines.Count -lt 3 -or $termsLines[0] -ne '---') {
    throw 'docs/terms.md must start with YAML front matter.'
}

$frontMatterEnd = -1
for ($index = 1; $index -lt $termsLines.Count; $index++) {
    if ($termsLines[$index] -eq '---') {
        $frontMatterEnd = $index
        break
    }
}
if ($frontMatterEnd -lt 0) {
    throw 'docs/terms.md YAML front matter is not closed.'
}

$contentStart = $frontMatterEnd + 1
while ($contentStart -lt $termsLines.Count -and [string]::IsNullOrWhiteSpace($termsLines[$contentStart])) {
    $contentStart++
}
if ($contentStart -ge $termsLines.Count) {
    throw 'docs/terms.md has no content after YAML front matter.'
}

$plainTextLines = foreach ($line in $termsLines[$contentStart..($termsLines.Count - 1)]) {
    if ($line -eq '---') {
        continue
    }

    $line `
        -replace '^#{1,2} ', '' `
        -replace '\*\*', '' `
        -replace '`', ''
}
$plainTextTerms = $plainTextLines -join "`r`n"
[IO.Directory]::CreateDirectory($installerInputPath) | Out-Null
[IO.File]::WriteAllText(
    $installerLicensePath,
    $plainTextTerms,
    (New-Object Text.UTF8Encoding($true)))

$arguments += $installerScriptPath
& $IsccPath @arguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$productVersion = (Get-Item -LiteralPath $publishedExecutablePath).VersionInfo.ProductVersion
$generatedInstallerPath = Join-Path $installerOutputPath "UnifiedCalendar-Setup-$productVersion.exe"
if (-not (Test-Path -LiteralPath $generatedInstallerPath -PathType Leaf)) {
    throw "Installer output was not found at the expected path: $generatedInstallerPath"
}

if (-not $NoSign) {
    foreach ($signatureTarget in @(
        [pscustomobject]@{
            Label = 'Installer'
            Path = $generatedInstallerPath
        },
        [pscustomobject]@{
            Label = 'Published application executable'
            Path = $publishedExecutablePath
        }
    )) {
        $signature = Get-AuthenticodeSignature -LiteralPath $signatureTarget.Path
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
            throw "$($signatureTarget.Label) signature validation failed: $($signature.Status)."
        }
    }
}

Get-Item -LiteralPath $generatedInstallerPath |
    Select-Object FullName, Length, LastWriteTime
