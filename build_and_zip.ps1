#Requires -Version 5.1
<#
.SYNOPSIS
    すべてのライブラリをビルドし、DLL をまとめた ZIP ファイルを生成します。
    NuGet パッケージ (.nupkg / .snupkg) は ZIP に含みません。

.DESCRIPTION
    生成される ZIP の構造:
      usleep_win_cs_<version>/
        net10.0-windows/
          usleep_win_cs.dll
          usleep_win_cs.xml
        unity_generic/
          netstandard2.1/
            usleep_win_cs.unity.dll
            usleep_win_cs.unity.xml   (存在する場合)
        unity_windows/
          netstandard2.1/
            usleep_win_cs.unity.dll
            usleep_win_cs.unity.xml   (存在する場合)
#>

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# ── バージョンを csproj から取得 ──────────────────────────────────────────────
$csprojPath = Join-Path $PSScriptRoot "pack\usleep_win_cs.nupkg.csproj"
[xml]$csproj  = Get-Content $csprojPath -Raw
$version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } |
             Where-Object { $_ } | Select-Object -First 1).Trim()

if (-not $version) {
    Write-Error "csproj からバージョンを取得できませんでした。"
    exit 1
}

$zipBaseName = "usleep_win_cs_$version"
Write-Host ""
Write-Host "=== build_and_zip.ps1  (version: $version) ===" -ForegroundColor Cyan
Write-Host ""

# ── 一時ディレクトリ ──────────────────────────────────────────────────────────
$tmpRoot = Join-Path $env:TEMP ("usleep_win_cs_" + [System.IO.Path]::GetRandomFileName())
$stagingDir = Join-Path $tmpRoot $zipBaseName
New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null

function Invoke-Dotnet {
    param([string[]]$DotnetArgs)
    & dotnet @DotnetArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet $($DotnetArgs -join ' ') が失敗しました (exit $LASTEXITCODE)"
        exit $LASTEXITCODE
    }
}

function Copy-BuildOutput {
    param(
        [string]$SrcDir,
        [string]$DstDir
    )
    New-Item -ItemType Directory -Force -Path $DstDir | Out-Null
    $files = Get-ChildItem -Path $SrcDir -File |
             Where-Object { $_.Extension -in ".dll", ".xml", ".pdb" }
    foreach ($f in $files) {
        Copy-Item $f.FullName -Destination $DstDir
        Write-Host "  コピー: $($f.Name) → $DstDir" -ForegroundColor DarkGray
    }
}

# ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
# Step 1: .NET 10 ビルド (dotnet build で DLL のみ取得)
# ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
Write-Host "── Step 1: .NET 10 Windows ビルド ──────────────────────────────" -ForegroundColor Yellow
Invoke-Dotnet @("restore", "pack\usleep_win_cs.nupkg.csproj")
Invoke-Dotnet @("build",   "pack\usleep_win_cs.nupkg.csproj", "-c", "Release", "--no-restore")

$net10Src = Join-Path $PSScriptRoot "pack\bin\Release\net10.0-windows"
$net10Dst = Join-Path $stagingDir "net10.0-windows"
Copy-BuildOutput -SrcDir $net10Src -DstDir $net10Dst

# ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
# Step 2: Unity generic ビルド (DefineConstants: USLP_UNITY のみ)
# ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
Write-Host ""
Write-Host "── Step 2: Unity generic ビルド (netstandard2.1) ───────────────" -ForegroundColor Yellow
Invoke-Dotnet @("restore", "unity\usleep_win_cs.unity.csproj")
Invoke-Dotnet @("build",   "unity\usleep_win_cs.unity.csproj", "-c", "Release", "--no-restore")

$unitySrc = Join-Path $PSScriptRoot "unity\bin\Release\netstandard2.1"
$unityGenericDst = Join-Path $stagingDir "unity_generic\netstandard2.1"
Copy-BuildOutput -SrcDir $unitySrc -DstDir $unityGenericDst

# ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
# Step 3: Unity Windows ビルド (DefineConstants: USLP_UNITY;USLP_WINDOWS)
# ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
Write-Host ""
Write-Host "── Step 3: Unity Windows ビルド (netstandard2.1) ───────────────" -ForegroundColor Yellow
Invoke-Dotnet @("build", "unity\usleep_win_cs.unity.csproj", "-c", "Release", "--no-restore",
              "-p:DefineConstants=USLP_UNITY%3BUSLP_WINDOWS")

$unityWindowsDst = Join-Path $stagingDir "unity_windows\netstandard2.1"
Copy-BuildOutput -SrcDir $unitySrc -DstDir $unityWindowsDst

# ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
# Step 4: ZIP 作成
# ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
Write-Host ""
Write-Host "── Step 4: ZIP 作成 ────────────────────────────────────────────" -ForegroundColor Yellow
$zipPath = Join-Path $PSScriptRoot "$zipBaseName.zip"
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}
Compress-Archive -Path $stagingDir -DestinationPath $zipPath

# 後片付け
Remove-Item $tmpRoot -Recurse -Force

Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "[OK] ZIP を作成しました: $zipPath" -ForegroundColor Green

# 内容一覧を表示
Write-Host ""
Write-Host "── ZIP 内容 ────────────────────────────────────────────────────" -ForegroundColor Cyan
$entries = [System.IO.Compression.ZipFile]::OpenRead($zipPath).Entries
$entries | Select-Object FullName, @{N="Size(KB)"; E={[math]::Round($_.Length/1KB,1)}} |
    Format-Table -AutoSize
