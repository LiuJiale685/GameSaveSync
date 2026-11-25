# GameSaveSync 安装包构建脚本
# 用法（在仓库根目录）：powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot 'tmp\publish'
$distDir = Join-Path $repoRoot 'dist'
$issPath = Join-Path $PSScriptRoot 'GameSaveSync.iss'
$iscc = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'

if (-not (Test-Path $iscc)) {
    throw "未找到 Inno Setup 编译器：$iscc（请先安装 Inno Setup 6）"
}

Write-Host '==> 1/3 发布自包含单文件程序…' -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish (Join-Path $repoRoot 'src\GameSaveSync.Desktop\GameSaveSync.Desktop.csproj') `
    -c Release -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败。' }

# 发布目录只保留 exe 与必要的运行库，剔除 pdb 调试符号
Get-ChildItem $publishDir -Filter *.pdb | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host '==> 2/3 使用 Inno Setup 打包…' -ForegroundColor Cyan
if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir | Out-Null }
& $iscc $issPath
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup 编译失败。' }

Write-Host '==> 3/3 产物：' -ForegroundColor Cyan
Get-ChildItem $distDir -Filter *.exe | ForEach-Object {
    '    {0}  ({1:N1} MB)' -f $_.FullName, ($_.Length / 1MB)
}
