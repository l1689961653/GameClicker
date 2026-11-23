# RunTests.ps1 —— 编译并运行核心功能验证
$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$csc = "C:\Program Files (x86)\Microsoft Visual Studio\Shared\Packages\Microsoft.CodeDom.Providers.DotNetCompilerPlatform.2.0.1\tools\RoslynLatest\csc.exe"
$fx = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$outExe = Join-Path $Root "dist\GameClickerTests.exe"
$outDir = Join-Path $Root "dist\testout"

if (-not (Test-Path $csc)) { throw "找不到 Roslyn 编译器：$csc" }

$sources = @(Get-ChildItem (Join-Path $Root "src") -Filter *.cs | ForEach-Object FullName) +
           @(Get-ChildItem (Join-Path $Root "tests") -Filter *.cs | ForEach-Object FullName)

$opts = @(
    "/nologo", "/target:exe", "/platform:anycpu", "/langversion:latest",
    "/codepage:65001", "/optimize+",
    "/main:GameClicker.Test.TestProgram",
    "/out:$outExe",
    "/r:$fx\System.dll", "/r:$fx\System.Core.dll", "/r:$fx\System.Windows.Forms.dll",
    "/r:$fx\System.Drawing.dll", "/r:$fx\System.Web.Extensions.dll"
)
foreach ($s in $sources) { $opts += $s }

Write-Host "编译测试程序…" -ForegroundColor Cyan
& $csc $opts
if ($LASTEXITCODE -ne 0) { throw "测试程序编译失败" }

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir | Out-Null

Write-Host "运行验证…" -ForegroundColor Cyan
$p = Start-Process -FilePath $outExe -ArgumentList $outDir -PassThru `
    -RedirectStandardOutput (Join-Path $outDir "stdout.txt") `
    -RedirectStandardError (Join-Path $outDir "stderr.txt")

if (-not $p.WaitForExit(180000)) {
    Stop-Process -Id $p.Id -Force
    Write-Host "测试超时" -ForegroundColor Red
    exit 1
}

$code = $p.ExitCode
Get-Content (Join-Path $outDir "stdout.txt") -Encoding UTF8
$err = Get-Content (Join-Path $outDir "stderr.txt") -Encoding UTF8
if ($err) { Write-Host "--- stderr ---" -ForegroundColor DarkYellow; $err }

Write-Host ""
Write-Host "退出码：$code" -ForegroundColor $(if ($code -eq 0) { "Green" } else { "Red" })
exit $code
