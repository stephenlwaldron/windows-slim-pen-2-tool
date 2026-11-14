# Builds the installer, portable zip and update files for both Intel/AMD and ARM Surfaces.
#   .\scripts\release.ps1 -Version 2.0.0            build into artifacts\releases
#   .\scripts\release.ps1 -Version 2.0.0 -Publish   also create the GitHub release (needs gh)
# Needs the vpk tool once:  dotnet tool install -g vpk
param(
    [Parameter(Mandatory)][string]$Version,
    [switch]$Publish,
    [string]$Notes = ''
)

$ErrorActionPreference = 'Stop'
$root     = Split-Path $PSScriptRoot
$releases = Join-Path $root 'artifacts\releases'
$repo     = 'stephenlwaldron/windows-slim-pen-haptics-unlocked'

foreach ($rid in 'win-x64', 'win-arm64') {
    $publishDir = Join-Path $root "artifacts\publish\$rid"
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    dotnet publish (Join-Path $root 'SlimPenHaptics.csproj') -c Release -r $rid --self-contained true `
        -p:Version=$Version -p:DebugType=none -o $publishDir --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }

    # Each architecture is its own update channel, so an ARM install never pulls an Intel build.
    vpk pack --packId SlimPenHaptics --packVersion $Version --packDir $publishDir `
        --mainExe SlimPenHaptics.exe --packTitle 'Slim Pen Haptics' --packAuthors 'stephenlwaldron' `
        --icon (Join-Path $root 'assets\app.ico') --runtime $rid --channel $rid --outputDir $releases
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed for $rid" }
}

Get-ChildItem $releases | Sort-Object Name | Format-Table Name, @{ n = 'MB'; e = { '{0:n1}' -f ($_.Length / 1MB) } }

if ($Publish) {
    # The updater reads releases.<channel>.json and the .nupkg files from the latest release.
    $files = Get-ChildItem $releases -File | Where-Object { $_.Name -match "Setup\.exe$|Portable\.zip$|^releases\..*\.json$|-$([regex]::Escape($Version))-.*\.nupkg$" }
    gh release create "v$Version" $files.FullName -R $repo --title "v$Version" --latest --notes $Notes
    if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
}
