$ErrorActionPreference = "Stop"

$projectDir = Join-Path $PSScriptRoot "src\CopilotAutoBYOK"
$outputDir = Join-Path $PSScriptRoot "publish"

Write-Host "清理 publish 目录..." -ForegroundColor Cyan
if (Test-Path $outputDir) {
    Remove-Item "$outputDir\*" -Recurse -Force
}

Write-Host "发布 Release 到 publish..." -ForegroundColor Cyan
dotnet publish $projectDir -c Release -o $outputDir --self-contained false

if ($LASTEXITCODE -ne 0) {
    Write-Host "发布失败！" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "发布完成！输出目录: $outputDir" -ForegroundColor Green
