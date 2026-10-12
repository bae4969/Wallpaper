param([string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'build'))
$ErrorActionPreference = 'Stop'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK(dotnet)를 찾지 못했습니다.'
}
$project = Join-Path $PSScriptRoot 'Wallpaper.csproj'
& dotnet publish $project -c Release -o $OutputDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw '빌드에 실패했습니다.' }
Write-Host ('빌드 완료: ' + (Join-Path $OutputDirectory 'Wallpaper.exe'))
