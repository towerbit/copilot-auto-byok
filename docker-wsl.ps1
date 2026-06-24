$ErrorActionPreference = "Stop"

$ImageName = "copilot-auto-byok"
$ContainerName = "copilot-auto-byok"
$HostPort = 15959
$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot ".")).Path

# 1) 确保 WSL 中的 docker 可用
$null = wsl -e sh -c "command -v docker >/dev/null 2>&1"
if ($LASTEXITCODE -ne 0) {
    Write-Host "未检测到 WSL 中的 docker，请先安装（例如：sudo apt-get update && sudo apt-get install -y docker.io && sudo usermod -aG docker $env:USERNAME）" -ForegroundColor Red
    exit 1
}

# 2) 将 Windows 项目路径转换为 WSL 路径
$wslProjectRoot = wsl wslpath -u ($ProjectRoot -replace '\\', '/')
Write-Host "WSL 项目路径: $wslProjectRoot" -ForegroundColor Cyan

# 3) 在 WSL 中构建镜像
Write-Host "在 WSL Docker 中构建镜像: $ImageName ..." -ForegroundColor Cyan
wsl bash -c "cd '$wslProjectRoot' && docker build -t $ImageName ."
if ($LASTEXITCODE -ne 0) {
    Write-Host "镜像构建失败" -ForegroundColor Red
    exit $LASTEXITCODE
}

# 4) 清理同名旧容器（若存在）
$existing = wsl docker ps -aq -f name=$ContainerName
if ($existing) {
    Write-Host "移除旧容器: $ContainerName" -ForegroundColor Yellow
    wsl docker rm -f $ContainerName | Out-Null
}

# 确保 copilot-auto-byok 目录存在（SQLite 数据库持久化）
$volumeDir = Join-Path $ProjectRoot "copilot-auto-byok"
if (-not (Test-Path $volumeDir)) {
    New-Item -ItemType Directory -Path $volumeDir | Out-Null
}

# 5) 运行容器
Write-Host "启动容器: $ContainerName (host port $HostPort -> container 15959)" -ForegroundColor Cyan
Write-Host "SQLite 数据库映射: $volumeDir -> /app/Data" -ForegroundColor Cyan
wsl docker run -d --name $ContainerName -p ${HostPort}:15959 -v "${wslProjectRoot}/copilot-auto-byok:/app/Data" --restart unless-stopped $ImageName

Write-Host ""
Write-Host "✅ 已成功安装并运行到本地 WSL Docker" -ForegroundColor Green
Write-Host "   访问地址: http://localhost:$HostPort" -ForegroundColor Green
Write-Host "   查看日志: wsl docker logs -f $ContainerName" -ForegroundColor Gray
Write-Host "   停止容器: wsl docker stop $ContainerName" -ForegroundColor Gray
Write-Host "   重启容器: wsl docker start $ContainerName" -ForegroundColor Gray
Write-Host "   删除容器: wsl docker rm -f $ContainerName" -ForegroundColor Gray
