[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$solutionPath = Join-Path $PSScriptRoot 'UnifiedCalendar.sln'
$appProjectPath = Join-Path $PSScriptRoot 'src\UnifiedCalendar.App\UnifiedCalendar.App.csproj'
$publishPath = Join-Path $PSScriptRoot 'artifacts\publish\win-x64'
$buildMetadataPath = Join-Path $PSScriptRoot 'artifacts\build-metadata\win-x64-client-credentials.json'
$clientCredentialProperties = @()
$credentialWarnings = @()
$googleClientIdProvided = -not [string]::IsNullOrWhiteSpace(
    $env:UnifiedCalendar__Google__ClientId)
$googleClientSecretProvided = -not [string]::IsNullOrWhiteSpace(
    $env:UnifiedCalendar__Google__ClientSecret)
$microsoftClientIdProvided = -not [string]::IsNullOrWhiteSpace(
    $env:UnifiedCalendar__Microsoft__ClientId)

if ($googleClientIdProvided) {
    $clientCredentialProperties += "--property:UnifiedCalendarGoogleClientId=$($env:UnifiedCalendar__Google__ClientId)"
}

if ($googleClientSecretProvided) {
    $clientCredentialProperties += "--property:UnifiedCalendarGoogleClientSecret=$($env:UnifiedCalendar__Google__ClientSecret)"
}

if ($microsoftClientIdProvided) {
    $clientCredentialProperties += "--property:UnifiedCalendarMicrosoftClientId=$($env:UnifiedCalendar__Microsoft__ClientId)"
}

if ($googleClientIdProvided -xor $googleClientSecretProvided) {
    $missingGoogleVariable = if ($googleClientIdProvided) {
        'UnifiedCalendar__Google__ClientSecret'
    }
    else {
        'UnifiedCalendar__Google__ClientId'
    }
    $credentialWarnings += "INCOMPLETE Google credential pair: $missingGoogleVariable is missing. Google credentials will not be usable."
}
elseif (-not $googleClientIdProvided) {
    $credentialWarnings += 'Google credentials were not embedded. Missing: UnifiedCalendar__Google__ClientId, UnifiedCalendar__Google__ClientSecret.'
}

if (-not $microsoftClientIdProvided) {
    $credentialWarnings += 'Microsoft credentials were not embedded. Missing: UnifiedCalendar__Microsoft__ClientId.'
}

dotnet restore $solutionPath --runtime win-x64
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

dotnet build $solutionPath `
    --configuration $Configuration `
    --no-restore `
    --property:ContinuousIntegrationBuild=true `
    @clientCredentialProperties
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

dotnet test $solutionPath --configuration $Configuration --no-restore --no-build
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if (-not $SkipPublish) {
    if (Test-Path -LiteralPath $buildMetadataPath -PathType Leaf) {
        Remove-Item -LiteralPath $buildMetadataPath -Force
    }

    dotnet publish $appProjectPath `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        --output $publishPath `
        --property:PublishSingleFile=false `
        --property:PublishTrimmed=false `
        @clientCredentialProperties

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $buildMetadataDirectory = Split-Path -Parent $buildMetadataPath
    New-Item -ItemType Directory -Path $buildMetadataDirectory -Force | Out-Null
    [ordered]@{
        schemaVersion = 1
        googleClientIdEmbedded = $googleClientIdProvided
        googleClientSecretEmbedded = $googleClientSecretProvided
        microsoftClientIdEmbedded = $microsoftClientIdProvided
    } |
        ConvertTo-Json |
        Set-Content -LiteralPath $buildMetadataPath -Encoding utf8
}

if ($credentialWarnings.Count -eq 0) {
    Write-Host 'Client credential embedding check: Google and Microsoft credentials were provided.'
}
else {
    Write-Warning 'CLIENT CREDENTIAL BUILD WARNING: the build succeeded, but one or more credentials were not embedded.'
    foreach ($credentialWarning in $credentialWarnings) {
        Write-Warning $credentialWarning
    }
}
