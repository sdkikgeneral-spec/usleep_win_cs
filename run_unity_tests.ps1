#Requires -Version 5.1
<#
.SYNOPSIS
    Unity Editor（Mono）上で usleep_win_cs.unity.dll の EditMode テストを実行します。

.DESCRIPTION
    tests/UsleepWin.UnityWindows.Tests は CoreCLR 上で DllImport 経路を検証しますが、
    Unity が実際に使う Mono ランタイムの検証にはなりません。実際に差異があります。
    例: Mono の Stopwatch.GetTimestamp() はプロセス基準で、QPC（ブート基準）と一致しません
    （CoreCLR では Stopwatch が QPC そのものを返すため一致します）。

    このスクリプトは Unity をバッチモードで起動し、Unity Windows 版 DLL を
    Assets/Plugins に配置した状態で EditMode テストを走らせます。

.PARAMETER UnityPath
    Unity.exe のパス。省略時は既定のインストール先を探します。

.EXAMPLE
    .\run_unity_tests.ps1
    .\run_unity_tests.ps1 -UnityPath "C:\Program Files\Unity\Hub\Editor\6000.5.10f1\Editor\Unity.exe"
#>
param(
    [string]$UnityPath = ""
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# ── Unity の場所を決める ──────────────────────────────────────────────────────
if (-not $UnityPath) {
    # ProjectVersion.txt に記録されたバージョンを優先して探す
    $versionFile = Join-Path $PSScriptRoot "tests\UnityEditor.Tests\ProjectSettings\ProjectVersion.txt"
    $preferred = $null
    if (Test-Path $versionFile) {
        $line = (Get-Content $versionFile | Where-Object { $_ -match "^m_EditorVersion:" })
        if ($line) { $preferred = ($line -split ":")[1].Trim() }
    }

    $hubRoot = "C:\Program Files\Unity\Hub\Editor"
    $candidates = @()
    if ($preferred) { $candidates += Join-Path $hubRoot "$preferred\Editor\Unity.exe" }
    if (Test-Path $hubRoot) {
        $candidates += Get-ChildItem $hubRoot -Directory |
                       Sort-Object Name -Descending |
                       ForEach-Object { Join-Path $_.FullName "Editor\Unity.exe" }
    }

    $UnityPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $UnityPath -or -not (Test-Path $UnityPath)) {
    Write-Error "Unity.exe が見つかりません。-UnityPath で明示してください。"
    exit 1
}

Write-Host "Unity: $UnityPath" -ForegroundColor Cyan

# ── Unity Windows 版 DLL をビルドして配置 ────────────────────────────────────
Write-Host "`n-- Unity Windows DLL をビルド --" -ForegroundColor Yellow
& cmd.exe /c "$PSScriptRoot\build_unity_windows.bat"
if ($LASTEXITCODE -ne 0) {
    Write-Error "build_unity_windows.bat が失敗しました (exit $LASTEXITCODE)"
    exit $LASTEXITCODE
}

$projectPath = Join-Path $PSScriptRoot "tests\UnityEditor.Tests"
$pluginDir   = Join-Path $projectPath "Assets\Plugins\usleep_win_cs"
$dllSrc      = Join-Path $PSScriptRoot "unity\bin\Release\netstandard2.1\usleep_win_cs.unity.dll"

if (-not (Test-Path $dllSrc)) {
    Write-Error "DLL が見つかりません: $dllSrc"
    exit 1
}

New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
Copy-Item $dllSrc $pluginDir -Force
Write-Host "配置: $pluginDir\usleep_win_cs.unity.dll" -ForegroundColor DarkGray

# ── テスト実行 ────────────────────────────────────────────────────────────────
$resultsPath = Join-Path $projectPath "results.xml"
$logPath     = Join-Path $projectPath "unity.log"

# 前回の結果を消しておく。残っていると失敗時に古い結果を成功と誤認する
# （実際にこれで一度誤読した）。
Remove-Item $resultsPath -Force -ErrorAction SilentlyContinue

Write-Host "`n-- EditMode テストを実行（初回はパッケージ取得で数分かかります）--" -ForegroundColor Yellow

# 呼び出し演算子 (&) では Unity の終了を待ちきれず、results.xml が書かれる前に
# 次の行へ進んでしまうことがある（実際に「results.xml が生成されませんでした」と
# 誤判定した）。Start-Process -Wait で確実に待つ。
$unityArgs = @(
    "-batchmode", "-nographics",
    "-projectPath", $projectPath,
    "-runTests", "-testPlatform", "EditMode",
    "-testResults", $resultsPath,
    "-logFile", $logPath
)
$proc = Start-Process -FilePath $UnityPath -ArgumentList $unityArgs -Wait -PassThru -NoNewWindow
$unityExit = $proc.ExitCode

# それでも書き込みが遅れることがあるので、短時間だけ出現を待つ
for ($i = 0; $i -lt 30 -and -not (Test-Path $resultsPath); $i++) {
    Start-Sleep -Milliseconds 500
}

# ── 結果の要約 ────────────────────────────────────────────────────────────────
if (-not (Test-Path $resultsPath)) {
    Write-Host "`n[NG] results.xml が生成されませんでした。コンパイルエラーの可能性があります。" -ForegroundColor Red
    if (Test-Path $logPath) {
        Write-Host "--- ログ中のコンパイルエラー ---" -ForegroundColor Red
        Select-String -Path $logPath -Pattern "error CS" | Select-Object -First 20
    }
    exit 1
}

[xml]$results = Get-Content $resultsPath
$run = $results.'test-run'
Write-Host ""
Write-Host "合計: $($run.total)  成功: $($run.passed)  失敗: $($run.failed)  スキップ: $($run.skipped)"

if ([int]$run.failed -gt 0) {
    Write-Host "`n--- 失敗したテスト ---" -ForegroundColor Red
    $results.SelectNodes("//test-case[@result='Failed']") | ForEach-Object {
        Write-Host "  $($_.fullname)" -ForegroundColor Red
        if ($_.failure.message) { Write-Host "    $($_.failure.message.Trim())" -ForegroundColor DarkRed }
    }
    exit 1
}

Write-Host "`n[OK] Unity EditMode テストがすべて成功しました。" -ForegroundColor Green
exit 0
