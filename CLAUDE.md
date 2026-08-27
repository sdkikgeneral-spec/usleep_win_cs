# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## プロジェクト概要

`usleep_win_cs` — Windows 向けの高精度・低ジッタなマイクロ秒スリープライブラリ（pure C#、名前空間 `Usleep.Win`）。
単一のソースツリー `src/` を、**3 種類のビルドバリアント**（NuGet / Unity Windows / Unity Generic）に条件コンパイルで振り分ける構成が本リポジトリの中心的な設計。

## ビルド・テスト

すべてリポジトリルートから実行する。

```bat
build_net10.bat            :: NuGet パッケージ (net10.0-windows) を dotnet pack
build_net10_x64.bat        :: 同上 + PlatformTarget=x64 / UslpX64Only=true
build_unity_generic.bat    :: Unity 汎用 DLL (netstandard2.1, P/Invoke なし)
build_unity_windows.bat    :: Unity Windows 専用 DLL (DefineConstants に USLP_WINDOWS を追加)
build_all.bat              :: 上記のうち net10 / unity_generic / unity_windows を順に実行
clean.bat                  :: bin/obj と dotnet clean
```

```powershell
.\build_and_zip.ps1        # 全ターゲットをビルドし usleep_win_cs_<version>.zip を生成（.nupkg は含まない）
```

テスト（**`.sln` にテストプロジェクトは含まれていない**ため、パス指定が必須）:

```powershell
dotnet test tests\UsleepWin.Tests\UsleepWin.Tests.csproj
dotnet test tests\UsleepWin.Tests\UsleepWin.Tests.csproj --filter "FullyQualifiedName~PreciseDelay"
dotnet test tests\UsleepWin.Tests\UsleepWin.Tests.csproj --filter "DisplayName~SleepMicroseconds_ZeroDoesNotThrow"
```

サンプル実行: `dotnet run --project samples\ConsoleDemo`

バージョンは `pack/usleep_win_cs.nupkg.csproj` の `<Version>` が唯一の情報源（`build_and_zip.ps1` がここから読む）。

## ビルドバリアントと条件コンパイル定数

`src/**/*.cs` は `pack/` と `unity/` の両 csproj から `<Compile Include="..\src\**\*.cs" />` で共有される。
**src 配下を編集する際は、常に 3 バリアントすべてでの挙動を意識すること。**

| ターゲット | TFM | 定義される定数 |
|---|---|---|
| NuGet | `net10.0-windows` | `USLP_WINDOWS` + `USLP_NUGET` + `USLP_GENERATOR`（+ 任意で `USLP_X64_ONLY`） |
| Unity Windows | `netstandard2.1` | `USLP_WINDOWS` のみ |
| Unity Generic | `netstandard2.1` | `USLP_UNITY`（Win32 分岐は全除外） |

- `USLP_GENERATOR` … `LibraryImport` source generator、`AggressiveOptimization`、`SkipLocalsInit`、`NativeClock` パスを有効化
- `USLP_WINDOWS` … 従来の `DllImport` + `SuppressUnmanagedCodeSecurity`
- `USLP_UNITY` … `PreciseDelay` 系 5 ファイル（`PreciseDelay` / `SpinCoreEngine` / `TimerWheel` / `PreciseWaitItem` / `NativeClock`）を `#if !USLP_UNITY` で丸ごと除外
- `USLP_X64_ONLY` … `X86Base.Pause()` を実行時分岐なしで直接発行

## アーキテクチャ

2 系統の独立した待機 API が同居している。

### 系統 1: `UsleepWin`（同期スリープ、全バリアント）

```
UsleepWin (public API / スレッドローカル設定)
  └─ InternalTiming (NowUs / SleepByTimer / SpinWithPeriodicYield / CoarseYield)
       ├─ NativeClock   … KUSER_SHARED_DATA 直読み（USLP_GENERATOR のみ）
       ├─ SpinHints     … PAUSE / YIELD / SpinWait
       └─ NativeMethods … P/Invoke（3 分岐 partial クラス）
```

- **設定はすべて `[ThreadStatic]`**（profile / tailSpin / yieldPolicy / 統計カウンタ）。スレッドをまたいで継承されない。`WaitableTimer` ハンドルもスレッドごとにキャッシュされる。
- `SleepMicroseconds` はプロファイルごとの閾値（`timerFirstUs` / `preferSpinBelow`）で「タイマー主体」か「スピン主体」かを分岐し、最後の `tailSpinUs` 区間は必ずスピンで詰める、という 2 段構成。
- `InternalTiming.NowUs()` の実装優先度はバリアントごとに異なる（NuGet: NativeClock → Stopwatch → TickCount / Unity Windows: QPC → Stopwatch → TickCount）。
- `NativeMethods` は `#if USLP_GENERATOR` / `#elif USLP_WINDOWS` / `#else`(空) の 3 分岐。**API を追加するときは Generator 版と DllImport 版の両方に、同じシグネチャで足す必要がある。**

### 系統 2: `PreciseDelay`（非同期、±1〜3 µs、NuGet ビルド専用）

```
PreciseDelay (public API)
  ├─ >5ms → WaitableTimer HR + RegisterWaitForSingleObject（省電力フォールバック）
  └─ ≤5ms → SpinCoreEngine
               ├─ 専用スピンスレッド（コア固定 + TIME_CRITICAL + NtSetTimerResolution(1)）
               ├─ TimerWheel   … 4096 スロット、Math.BigMul のマジックナンバー除算で O(1)
               └─ PreciseWaitItem(Pool) … IValueTaskSource でゼロアロケーション
```

守るべき不変条件:
- `SpinCoreEngine` の**ホットパスでは P/Invoke を一切呼ばない**。アフィニティ・優先度・タイマー分解能の設定は `SpinLoop` 冒頭で 1 回だけ。時刻取得は `NativeClock.GetTimestamp()`（P/Invoke ゼロ）。
- `PreciseWaitItem.Complete()` / `CompleteAsCancelled()` は**スピンスレッドのみ**が呼ぶ前提で `Interlocked` を使っていない。use-after-free は `IsInitialized` フラグで防いでいる。
- コア 0 の指定は `ArgumentException`、RealTime 優先度クラスでの実行は `SecurityException`。
- `PreciseDelay` は静的状態を持つため、テストは `[Collection("PreciseDelay")]`（`DisableParallelization = true`）で直列実行する。新しい `PreciseDelay` テストも必ずこのコレクションに入れること。

## ドキュメント

`document/specsheet.md`（日本語）/ `document/specsheet_en.md` が内部仕様書で、上記アルゴリズム・定数・P/Invoke 戦略の詳細を持つ。**src の挙動を変えたら specsheet の両言語版も更新すること。** 公開 API を変えた場合は `README.md` の API リファレンス表（日英両セクション）も同様。

`document/test_result.md` は実測ベンチマーク結果。

## サブエージェント

`.claude/agents/` に領域別のエージェントを定義してある。作業内容に応じて使い分ける。

| エージェント | 担当領域 |
|---|---|
| `project-leader` | 統括。方針決定・影響範囲・タスク分解・受け入れ判定。**実装はしない** |
| `csharp-implementer` | 実装担当。src / tests / samples のコードを書く |
| `upstream-cpp-reference` | 移植元 C++ 実装 `E:\Develop\Projects\usleep_win` を読み、差異と移植漏れを報告（参照先は読み取り専用） |
| `build-variant-guard` | 3 バリアントの条件コンパイル整合性とビルド通過 |
| `interop-concurrency-reviewer` | P/Invoke・unsafe・スレッド安全性のレビュー（読み取り専用） |
| `timing-benchmark` | 精度・ジッタの実測と xUnit テスト実行 |
| `docs-sync` | README(日/英)・specsheet(日/英) と実装の突き合わせ |

## 移植元プロジェクト

本リポジトリは C++ 実装 `usleep_win`（`E:\Develop\Projects\usleep_win`）の C# 移植版。
閾値・アルゴリズム・API 挙動の根拠を確認したいときは `upstream-cpp-reference` に調査させる。**参照先は絶対に変更しない。**

主な構造上の非対称:

- **C++ 側にのみ存在** — `DllMain` によるハンドル解放とタイマー分解能復帰、`usleep_*_nt_resolution` の公開 API、C ABI / `.rc` バージョン整合
- **C# 側にのみ存在** — `PreciseDelay` 系（非同期・専用スピンスレッド）、`NativeClock` の `KUSER_SHARED_DATA` 直読み、3 ビルドバリアント
- **同概念・別実装** — C++ の `qpc_now_us()` は浮動小数点を使わない純整数演算。C# の `NowUs()` は `_tickToUs`（double）を掛ける
- C++ の `t_timer` は `DLL_THREAD_DETACH` でクローズされるが、C# の `[ThreadStatic] _tTimer` はクローズされない（スレッド終了時にリーク）
