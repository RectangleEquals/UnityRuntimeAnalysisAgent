# Locks the overlay's binary assets (the asset bundles and the icon atlas) to a GitHub release: writes
# assets/overlay/release.json (tag, SHA-256 and size of each file) and prints the `gh release create` command that
# publishes them. Run it after build.ps1 or icons.py changed a file; when nothing changed it says so and does nothing.
# The build downloads the locked files from that release when they're missing (they aren't in git).
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$overlay = Join-Path $repo 'assets\overlay'
$lockPath = Join-Path $overlay 'release.json'
$repository = 'RectangleEquals/UnityRuntimeAnalysisAgent'

# JSON files are written with LF line endings and no BOM, the same bytes a git checkout has (.gitattributes).
function Write-JsonFile([string]$path, $value, [int]$depth = 5, [switch]$Compress) {
    $json = if ($Compress) { $value | ConvertTo-Json -Depth $depth -Compress } else { $value | ConvertTo-Json -Depth $depth }
    [System.IO.File]::WriteAllText($path, ($json -replace "`r`n", "`n") + "`n", (New-Object System.Text.UTF8Encoding $false))
}

# The released files: every bundle in the bundle manifest, and the icon atlas.
$manifest = Get-Content (Join-Path $overlay 'bundles\manifest.json') -Raw | ConvertFrom-Json
$files = @($manifest.families.PSObject.Properties | ForEach-Object { "bundles/$($_.Value.file)" }) + @('icons/phosphor.png')
$sha = [System.Security.Cryptography.SHA256]::Create()
$current = [ordered]@{}
foreach ($relative in $files | Sort-Object) {
    $path = Join-Path $overlay ($relative -replace '/', '\')
    if (-not (Test-Path $path)) { throw "$relative is missing; build it first (build.ps1 / icons.py)." }
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $current[$relative] = [ordered]@{ sha256 = (-join ($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') })); size = $bytes.Length }
}

$revision = 1
if (Test-Path $lockPath) {
    $lock = Get-Content $lockPath -Raw | ConvertFrom-Json
    $same = ($lock.files.PSObject.Properties.Name | Sort-Object) -join ',' -eq (($current.Keys | Sort-Object) -join ',')
    if ($same) {
        foreach ($key in $current.Keys) {
            if ($lock.files.$key.sha256 -ne $current[$key].sha256) { $same = $false }
        }
    }
    if ($same) {
        Write-Host "Nothing changed since $($lock.tag); no new release needed."
        return
    }
    $revision = [int]$lock.revision + 1
}

$tag = "overlay-assets-r$revision"
$lockJson = [ordered]@{ repository = $repository; tag = $tag; revision = $revision; files = $current }
Write-JsonFile $lockPath $lockJson 4
$paths = ($current.Keys | ForEach-Object { "assets/overlay/$_" }) -join ' '
Write-Host "Locked to $tag. After committing and pushing release.json, publish the files (from the repository root):"
Write-Host ""
Write-Host "gh release create $tag $paths --repo $repository --title `"Overlay assets r$revision`" --notes `"The in-game overlay's asset bundles and icon atlas (built from tools/OverlayAssets; SHA-256 in assets/overlay/release.json). Downloaded by the build; not an agent release.`" --prerelease"
