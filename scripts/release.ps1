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
$repo     = 'stephenlwaldron/windows-slim-pen-2-tool'

foreach ($rid in 'win-x64', 'win-arm64') {
    $publishDir = Join-Path $root "artifacts\publish\$rid"
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    dotnet publish (Join-Path $root 'SlimPenHaptics.csproj') -c Release -r $rid --self-contained true `
        -p:Version=$Version -p:DebugType=none -o $publishDir --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }

    # Each architecture is its own update channel, so an ARM install never pulls an Intel build.
    vpk pack --packId SlimPenHaptics --packVersion $Version --packDir $publishDir `
        --mainExe SlimPenHaptics.exe --packTitle 'Slim Pen Tool' --packAuthors 'stephenlwaldron' `
        --icon (Join-Path $root 'assets\app.ico') --runtime $rid --channel $rid --outputDir $releases
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed for $rid" }
}

# GitHub lists a release's files alphabetically. Give the files people should pick names that
# sort ahead of the updater's files, and that say which Surface they are for.
$downloads = @{
    'SlimPenHaptics-win-x64-Setup.exe'      = 'Install-SlimPenHaptics-Intel-AMD.exe'
    'SlimPenHaptics-win-arm64-Setup.exe'    = 'Install-SlimPenHaptics-Snapdragon.exe'
    'SlimPenHaptics-win-x64-Portable.zip'   = 'Portable-SlimPenHaptics-Intel-AMD.zip'
    'SlimPenHaptics-win-arm64-Portable.zip' = 'Portable-SlimPenHaptics-Snapdragon.zip'
}
foreach ($name in $downloads.Keys) {
    Move-Item (Join-Path $releases $name) (Join-Path $releases $downloads[$name]) -Force
}

Get-ChildItem $releases | Sort-Object Name | Format-Table Name, @{ n = 'MB'; e = { '{0:n1}' -f ($_.Length / 1MB) } }

if ($Publish) {
    # The updater reads releases.<channel>.json and the .nupkg files from the latest release.
    $files = Get-ChildItem $releases -File | Where-Object { $_.Name -match "^Install-|^Portable-|^releases\..*\.json$|-$([regex]::Escape($Version))-.*\.nupkg$" }
    # Notes go through a file: quotation marks in them don't survive being passed as an argument.
    $notesFile = Join-Path $releases "notes-$Version.md"
    [IO.File]::WriteAllText($notesFile, $Notes)
    gh release create "v$Version" $files.FullName -R $repo --title "v$Version" --latest --notes-file $notesFile
    if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
}
