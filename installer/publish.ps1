param([string]$Repository = 'Maksimasz/windows-iptv-player')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    $project = [xml](Get-Content -LiteralPath 'IptvPlayer\IptvPlayer.csproj' -Raw)
    $version = [string]$project.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid project version' }
    $tag = "v$version"
    $installer = "release\$version\CatLu-IPTV-$version-Setup-x64.exe"
    $sums = "release\$version\SHA256SUMS.txt"
    $notes = "installer\release-notes-$version.md"
    foreach ($file in @($installer, $sums, $notes)) {
        if (-not (Test-Path -LiteralPath $file)) { throw "Missing release file: $file" }
    }
    $hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    if ((Get-Content -LiteralPath $sums -Raw).Trim() -ne "$hash  $([IO.Path]::GetFileName($installer))") { throw 'Installer checksum mismatch' }
    gh api "repos/$Repository" --silent
    if ($LASTEXITCODE -ne 0) { throw 'GitHub access failed. Run: gh auth login -h github.com' }
    $remote = git remote get-url origin
    if ($LASTEXITCODE -ne 0 -or $remote -notmatch ('[:/]' + [regex]::Escape($Repository) + '(\.git)?$')) { throw 'Origin does not match release repository' }
    git add -- .gitignore IptvPlayer test/Checks test/WindowChecks build_epg.py README.md installer
    if ($LASTEXITCODE -ne 0) { throw 'Cannot stage release files' }
    git diff --cached --quiet
    if ($LASTEXITCODE -eq 1) {
        git commit -m "Release CatLu IPTV $version with archive playback, seeking and recording"
        if ($LASTEXITCODE -ne 0) { throw 'Commit failed' }
    } elseif ($LASTEXITCODE -ne 0) { throw 'Cannot inspect staged files' }
    $commit = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve release commit' }
    git push origin HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Push failed' }
    gh release create $tag $installer $sums --repo $Repository --target $commit --title "CatLu IPTV $version" --notes-file $notes
    if ($LASTEXITCODE -ne 0) { throw 'Release publication failed; inspect GitHub before retrying' }
    Write-Output "Published: https://github.com/$Repository/releases/tag/$tag"
} finally { Pop-Location }
