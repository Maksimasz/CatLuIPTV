param([string]$InnoCompiler)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$projectFile = Join-Path $projectRoot 'IptvPlayer\IptvPlayer.csproj'
$project = [xml](Get-Content -LiteralPath $projectFile -Raw)
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'В проекте должна быть задана версия X.Y.Z' }
if (-not $InnoCompiler) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    )
    $InnoCompiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler)) { throw 'Установите Inno Setup или укажите -InnoCompiler' }
$releaseDirectory = Join-Path $projectRoot "release\$version"
$payloadDirectory = Join-Path $releaseDirectory 'app'
dotnet publish $projectFile -c Release -r win-x64 --self-contained true -o $payloadDirectory -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать автономный плеер' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses') -Destination $payloadDirectory -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md') -Destination $payloadDirectory -Force
& $InnoCompiler '/Q' "/DAppVersion=$version" (Join-Path $PSScriptRoot 'CatLu-IPTV.iss')
if ($LASTEXITCODE -ne 0) { throw 'Ошибка сборки Inno Setup' }
$installer = Join-Path $releaseDirectory "CatLu-IPTV-$version-Setup-x64.exe"
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $releaseDirectory 'SHA256SUMS.txt'), "$hash  $([IO.Path]::GetFileName($installer))`n")
Write-Output "Готово: $installer"
