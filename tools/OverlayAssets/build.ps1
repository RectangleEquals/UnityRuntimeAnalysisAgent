# Builds the overlay's asset bundles with every installed Unity editor that builds a family (2021.3.x → 2021.3,
# 6000.3.x → 6000.3, 2018.4.x → legacy), in batch mode, into assets/overlay/bundles/, then writes manifest.json.
# Editors are found from Unity Hub's records (its install folder settings), so no paths are needed.
#   .\build.ps1                 all families with an installed editor
#   .\build.ps1 -Family 2021.3  one family
param(
    [ValidateSet('2021.3', '6000.3', 'legacy')]
    [string]$Family
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $here '..\..')).Path
$source = Join-Path $here 'Source\Overlay'
$projects = Join-Path $here 'Projects'
$bundles = Join-Path $repo 'assets\overlay\bundles'
$assetsVersion = 1  # raise when a change in Source needs agents to require the new bundles

function Find-Editors {
    $roots = @()
    $hubPath = Join-Path $env:APPDATA 'UnityHub\secondaryInstallPath.json'
    if (Test-Path $hubPath) {
        $custom = (Get-Content $hubPath -Raw | ConvertFrom-Json)
        if ($custom) { $roots += $custom }
    }
    $roots += Join-Path $env:ProgramFiles 'Unity\Hub\Editor'  # Unity Hub's default install folder
    $found = @{}
    foreach ($root in $roots | Where-Object { $_ -and (Test-Path $_) }) {
        foreach ($dir in Get-ChildItem $root -Directory) {
            $exe = Join-Path $dir.FullName 'Editor\Unity.exe'
            if (Test-Path $exe) { $found[$dir.Name] = $exe }
        }
    }
    return $found
}

function Get-FamilyOf([string]$version) {
    if ($version -like '2021.3.*') { return '2021.3' }
    if ($version -like '6000.3.*') { return '6000.3' }
    if ($version -like '2018.4.*') { return 'legacy' }
    return $null
}

# The fonts aren't in git: fonts.py fetches them (pinned and byte-reproducible).
if (-not (Get-ChildItem (Join-Path $source 'Fonts') -Filter '*.ttf' -ErrorAction SilentlyContinue)) {
    throw "The fonts are missing from Source/Overlay/Fonts; run: python tools/OverlayAssets/fonts.py"
}

# The hash of the sources (everything but the builder): each family records the one it was built from.
$sha = [System.Security.Cryptography.SHA256]::Create()
$sourceFiles = Get-ChildItem $source -Recurse -File | Where-Object { $_.FullName -notlike '*\Editor\*' -and $_.Extension -ne '.meta' } | Sort-Object { $_.FullName.Substring($source.Length).Replace('\', '/') } -CaseSensitive
$stream = New-Object System.IO.MemoryStream
foreach ($file in $sourceFiles) {
    $name = [System.Text.Encoding]::UTF8.GetBytes($file.FullName.Substring($source.Length + 1).Replace('\', '/'))
    $stream.Write($name, 0, $name.Length)
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $stream.Write($bytes, 0, $bytes.Length)
}
$sourceHash = -join ($sha.ComputeHash($stream.ToArray()) | ForEach-Object { $_.ToString('x2') })

$editors = Find-Editors
$byFamily = @{}
foreach ($version in $editors.Keys | Sort-Object { [version]($_ -replace '[a-z].*$', '') }) {
    $f = Get-FamilyOf $version
    if ($f) { $byFamily[$f] = @{ Version = $version; Exe = $editors[$version] } }  # the newest patch wins
}

$wanted = if ($Family) { @($Family) } else { @('2021.3', '6000.3', 'legacy') }
New-Item -ItemType Directory -Force $bundles, $projects | Out-Null
$built = 0
foreach ($f in $wanted) {
    if (-not $byFamily.ContainsKey($f)) {
        Write-Host "[$f] no editor installed; skipped."
        continue
    }

    $editor = $byFamily[$f]
    $project = Join-Path $projects $f
    $log = Join-Path $projects "$f-build.log"
    if (-not (Test-Path (Join-Path $project 'Assets'))) {
        Write-Host "[$f] creating the project with Unity $($editor.Version)..."
        $p = Start-Process $editor.Exe -ArgumentList '-batchmode', '-quit', '-nographics', '-createProject', "`"$project`"", '-logFile', "`"$(Join-Path $projects "$f-create.log")`"" -Wait -PassThru -NoNewWindow
        if ($p.ExitCode -ne 0) { throw "[$f] creating the project failed (exit $($p.ExitCode))." }
    }

    # The sources replace the project's Assets/Overlay (Unity makes the .meta files).
    $target = Join-Path $project 'Assets\Overlay'
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    Copy-Item -Recurse $source $target

    Write-Host "[$f] building with Unity $($editor.Version)..."
    $p = Start-Process $editor.Exe -ArgumentList '-batchmode', '-quit', '-nographics', '-projectPath', "`"$project`"", '-executeMethod', 'OverlayAssetsBuilder.BuildFromCommandLine', '-outDir', "`"$bundles`"", '-logFile', "`"$log`"" -Wait -PassThru -NoNewWindow
    $done = Select-String -Path $log -Pattern '\[OverlayAssets\] Done' -SimpleMatch:$false | Select-Object -First 1
    if ($p.ExitCode -ne 0 -or -not $done) {
        Get-Content $log -Tail 40
        throw "[$f] the build failed (exit $($p.ExitCode)); see $log"
    }

    $recordPath = Join-Path $bundles "$f.json"
    $record = Get-Content $recordPath -Raw | ConvertFrom-Json
    $record | Add-Member -NotePropertyName sourceSha256 -NotePropertyValue $sourceHash -Force
    $record | ConvertTo-Json -Depth 4 -Compress | Set-Content $recordPath -Encoding UTF8
    Write-Host "  $($done.Line.Substring($done.Line.IndexOf('[OverlayAssets]')))"
    $built++
}

$families = [ordered]@{}
foreach ($record in Get-ChildItem $bundles -Filter '*.json' | Where-Object { $_.Name -ne 'manifest.json' } | Sort-Object Name) {
    $data = Get-Content $record.FullName -Raw | ConvertFrom-Json
    $families[$data.family] = $data
}
$stale = @($families.Values | Where-Object { $_.sourceSha256 -ne $sourceHash } | ForEach-Object { $_.family })
if ($stale.Count -gt 0) { Write-Warning "Built from older sources, rebuild: $($stale -join ', ')." }
$manifest = [ordered]@{ assetsVersion = $assetsVersion; sourceSha256 = $sourceHash; families = $families }
$manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $bundles 'manifest.json') -Encoding UTF8
Write-Host "Built $built famil$(if ($built -eq 1) { 'y' } else { 'ies' }); manifest lists: $($families.Keys -join ', ')."
if ($built -gt 0) { Write-Host "Next: tools/OverlayAssets/release.ps1 locks the new bundles and prints the release command." }
