# Build.ps1 —— 编译游戏连点器为免依赖单文件 exe
#
# 用法：pwsh -File Build.ps1
#
# 关于权限：清单使用 asInvoker（不强制提权），是否以管理员身份运行由程序内的
# 「默认以管理员身份运行」设置决定（见 src/Elevation.cs）。
# 因此只产出一个 exe：自动化测试用普通权限即可启动它，
# 测试时把配置里的 runAsAdmin 设为 false 就不会触发 UAC。

param()

$ErrorActionPreference = "Stop"

$Root   = Split-Path -Parent $MyInvocation.MyCommand.Definition
$SrcDir = Join-Path $Root "src"
$DistDir = Join-Path $Root "dist"
$OutExe = Join-Path $DistDir "GameClicker.exe"

# 1) 选择可用的 C# 编译器（优先较新的 Roslyn，语法支持更好）
$cscCandidates = @(
    "C:\Program Files (x86)\Microsoft Visual Studio\Shared\Packages\Microsoft.CodeDom.Providers.DotNetCompilerPlatform.2.0.1\tools\RoslynLatest\csc.exe",
    "C:\Program Files (x86)\Microsoft Visual Studio\Shared\Packages\Microsoft.CodeDom.Providers.DotNetCompilerPlatform.2.0.0\tools\RoslynLatest\csc.exe",
    "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw "找不到 C# 编译器（csc.exe）。请确认已安装 .NET Framework 4.x。" }

# 2) 引用 .NET Framework 4.x 的库
$fxDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$refs = @(
    "System.dll",
    "System.Core.dll",
    "System.Windows.Forms.dll",
    "System.Drawing.dll",
    "System.Web.Extensions.dll",
    "System.Xml.dll"
) | ForEach-Object { Join-Path $fxDir $_ }
foreach ($r in $refs) { if (-not (Test-Path $r)) { throw "缺少引用程序集：$r" } }

# 3) 源文件
$sources = @(Get-ChildItem -Path $SrcDir -Filter "*.cs" | Select-Object -ExpandProperty FullName)
if ($sources.Count -eq 0) { throw "src 目录下没有 .cs 源文件" }

if (-not (Test-Path $DistDir)) { New-Item -ItemType Directory -Path $DistDir | Out-Null }

# 4) 组装编译参数
$cscOptions = New-Object System.Collections.Generic.List[string]
$cscOptions.Add("/nologo")
$cscOptions.Add("/target:winexe")
$cscOptions.Add("/platform:anycpu")
$cscOptions.Add("/langversion:latest")
$cscOptions.Add("/codepage:65001")
$cscOptions.Add("/optimize+")
$cscOptions.Add("/warn:4")
$cscOptions.Add("/out:$OutExe")

foreach ($r in $refs) { $cscOptions.Add("/r:$r") }

$icon = Join-Path $Root "assets\app.ico"
if (Test-Path $icon) { $cscOptions.Add("/win32icon:$icon") }

$manifest = Join-Path $SrcDir "app.manifest"
if (Test-Path $manifest) {
    $cscOptions.Add("/win32manifest:$manifest")
    Write-Host "已嵌入清单（asInvoker + 高DPI + 系统兼容；是否提权由程序设置决定）" -ForegroundColor Yellow
}

foreach ($s in $sources) { $cscOptions.Add($s) }

Write-Host "编译器：$csc" -ForegroundColor Cyan
Write-Host "输出  ：$OutExe" -ForegroundColor Cyan
Write-Host ""

if (Test-Path $OutExe) {
    try {
        Remove-Item $OutExe -Force -ErrorAction Stop
    }
    catch {
        Write-Host ""
        Write-Host "无法覆盖 $OutExe" -ForegroundColor Red
        Write-Host "原因：该文件正被占用（通常是有连点器实例仍在运行）。" -ForegroundColor Yellow
        Write-Host "请先从托盘图标退出正在运行的连点器，然后再重新构建。" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "提示：若实例以管理员权限启动，普通终端无法结束它，" -ForegroundColor DarkGray
        Write-Host "      可在任务管理器中结束，或用管理员终端执行：" -ForegroundColor DarkGray
        Write-Host "      taskkill /F /IM GameClicker.exe" -ForegroundColor DarkGray
        exit 2
    }
}

& $csc $cscOptions.ToArray()
if ($LASTEXITCODE -ne 0) {
    Write-Host "编译失败" -ForegroundColor Red
    exit $LASTEXITCODE
}

$f = Get-Item $OutExe
Write-Host ("  {0,-20} {1,9:N0} 字节" -f $f.Name, $f.Length) -ForegroundColor Green

# 清理历史遗留的副本（旧版构建曾产出过它），避免残留旧代码造成误判
$stale = Join-Path $DistDir "GameClicker-NoAdmin.exe"
if (Test-Path $stale) {
    try {
        Remove-Item $stale -Force -ErrorAction Stop
        Write-Host "  已清理历史遗留的 GameClicker-NoAdmin.exe（新版不再需要单独副本）" -ForegroundColor DarkGray
    }
    catch {
        Write-Host "  提示：GameClicker-NoAdmin.exe 正被占用，未能清理" -ForegroundColor DarkYellow
    }
}

exit 0
