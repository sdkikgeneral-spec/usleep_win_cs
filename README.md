# Overview

This repository provides a minimal, open-source C# implementation of `usleep` for Windows,
maintained as a supporting utility within a broader cross-platform and education-oriented software ecosystem.

# usleep_win_cs

[![NuGet](https://img.shields.io/nuget/v/usleep_win_cs)](https://www.nuget.org/packages/usleep_win_cs)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/sdkikgeneral-spec/usleep_win_cs/blob/main/LICENSE)

**高精度・低ジッタな Windows 向けマイクロ秒スリープライブラリ（pure C#）**

[English section below ↓](#english)

---

## 要件

| 項目 | 値 |
|---|---|
| ランタイム | .NET 10.0+（Windows） |
| OS | Windows 10 / 11 / Server 2019 以降 |
| アーキテクチャ | x64 / ARM64 |
| 名前空間 | `Usleep.Win` |

---

## インストール

対象は **`net10.0-windows`（Windows 専用）** の NuGet パッケージです。

```shell
dotnet add package usleep_win_cs
```

Unity プロジェクトへの組み込みには NuGet パッケージではなく、
[GitHub Releases](https://github.com/sdkikgeneral-spec/usleep_win_cs/releases) が配布する
zip から Unity Windows / Unity Generic 向けの DLL を取得して `Assets` 配下に配置してください
（Unity 汎用 DLL は `netstandard2.1`、P/Invoke なし）。

---

## 概要

WaitableTimer HR + CPU ヒント命令（`PAUSE`/`YIELD`）+ 段階的スレッド譲渡を組み合わせたハイブリッド待機方式。
時刻源は、NuGet ビルド（`net10.0-windows`）が `Stopwatch.GetTimestamp()`（内部で QPC）、Unity ビルドが QPC 直呼びです。

> Windows はハードリアルタイム OS ではありません。精度は電源管理・仮想化・負荷の影響を受けます。

---

## 使用例

```csharp
using Usleep.Win;

// 基本
UsleepWin.SleepMicroseconds(500);    // 500 µs
UsleepWin.SleepNanoseconds(200_000); // 200 µs（ns 指定）

// 締切方式の定周期ループ（ドリフト抑制）
const ulong periodUs = 1_000;
ulong next = UsleepWin.NowSteadyMicroseconds();
for (;;) { next += periodUs; UsleepWin.SleepUntilSteadyMicroseconds(next); /* ... */ }

// プロファイル・チューニング
UsleepWin.SetProfile(UsleepProfile.STRICT);             // 低ジッタ優先
UsleepWin.SetTailSpinMicroseconds(300);                 // タイマー後スピン 300 µs
UsleepWin.SetYieldPolicy(UsleepYieldPolicy.SLEEP0);     // スレッド譲渡方法

// 統計
UsleepStats st = UsleepWin.GetStats(reset: false);
Console.WriteLine($"timer={st.WaitableTimerUses}, spin={st.SpinRelax}");
```

> タイマー分解能の変更が必要な場合は `UsleepWin.InitTimerResolution(1)` / `ShutdownTimerResolution()` を使用してください（`timeBeginPeriod(1)` はシステム全体に影響します。Windows 10 v1803+ では通常不要）。

---

## 高精度非同期タイマー（`PreciseDelay`）

±1〜3 µs 精度が必要な箇所専用。専用スピンスレッド + タイマーホイール + `IValueTaskSource` プールによるゼロアロケーション実装。

> `net10.0-windows` の NuGet ターゲット専用。Unity（`netstandard2.1`）では `#if !USLP_UNITY` によりコンパイル除外。

```csharp
// 起動時に1回（コア0は禁止）
PreciseDelay.Initialize(dedicatedCpuCore: 3);

// ≤5ms → 専用スピンスレッド、>5ms → WaitableTimer HR
await PreciseDelay.WaitAsync(TimeSpan.FromMicroseconds(500));
await PreciseDelay.WaitAsync(TimeSpan.FromMicroseconds(500), cancellationToken);

// 終了時に1回
PreciseDelay.Shutdown();
```

> **注意:** `await PreciseDelay.WaitAsync(...)` 以降の継続は、精度を優先する設計上、
> 専用スピンスレッド上でインライン実行されうる。`await` の直後でブロッキング処理
> （`lock`、ファイル / ネットワーク I/O、同期待ち、重いロギング）を行うと、
> 同時に待機している他の待機項目の精度が損なわれる。重い処理は `Task.Run` などへ逃がすこと。
> 詳細は [`document/specsheet.md`](https://github.com/sdkikgeneral-spec/usleep_win_cs/blob/main/document/specsheet.md) の 14.3 節を参照。

---

## API リファレンス

### `PreciseDelay` 静的クラス

| メソッド・プロパティ | 説明 |
|---|---|
| `Initialize(int dedicatedCpuCore = 3)` | 起動時に1回。コア0は禁止（`ArgumentException`） |
| `Shutdown()` | 終了時に1回 |
| `WaitAsync(TimeSpan, CancellationToken)` | 高精度非同期待機。未初期化時は `InvalidOperationException` |
| `IsInitialized` | 初期化済みかどうか |

### `UsleepWin` 静的クラス

| メソッド | 説明 |
|---|---|
| `SleepMicroseconds(ulong usec)` | 指定マイクロ秒待機 |
| `SleepNanoseconds(ulong nsec)` | 指定ナノ秒待機（内部でマイクロ秒に変換） |
| `SleepUntilSteadyMicroseconds(ulong targetUs)` | 指定モノトニック時刻まで待機 |
| `NowSteadyMicroseconds()` | 現在のモノトニック時刻をマイクロ秒で取得 |
| `SetProfile(UsleepProfile)` | プロファイル適用（スレッドローカル） |
| `SetTailSpinMicroseconds(uint)` | タイマー後スピン時間設定（スレッドローカル）。`MaxTailSpinMicroseconds`(10000µs) 超で `ArgumentOutOfRangeException` |
| `SetYieldPolicy(UsleepYieldPolicy)` | スレッド譲渡ポリシー設定（スレッドローカル） |
| `MaxTailSpinMicroseconds` | テールスピン長の上限定数（10000µs = 10ms） |
| `SetPowerMode(UsleepPowerMode)` | スレッドの電力スロットリングモード設定 |
| `InitTimerResolution(uint ms)` | `timeBeginPeriod` でタイマー分解能要求 |
| `ShutdownTimerResolution()` | `timeEndPeriod` でタイマー分解能要求解除 |
| `GetStats(bool reset)` | スレッドローカル統計取得 |
| `ResetStats()` | スレッドローカル統計リセット |

### 列挙型・構造体

| 型 | 値 |
|---|---|
| `UsleepProfile` | `BALANCED`（既定）/ `STRICT`（低ジッタ） / `LOW_POWER`（省電力） |
| `UsleepYieldPolicy` | `NONE` / `SWITCH_THREAD` / `SLEEP0`（既定） / `SLEEP1` |
| `UsleepStats` | `SpinRelax` / `YieldSwitch` / `YieldSleep0` / `YieldSleep1` / `WaitableTimerUses` |

---

## チューニングの目安

- **低ジッタ優先:** `STRICT` + `SetTailSpinMicroseconds(300〜500)`
- **省電力優先:** `LOW_POWER`（スピンなし、タイマーのみ）
- 目標周期・許容遅着・CPU 使用率を実測しながら調整してください

---

## ライセンス

MIT License — 詳細は [LICENSE](https://github.com/sdkikgeneral-spec/usleep_win_cs/blob/main/LICENSE) を参照してください。

---
---

<a name="english"></a>

# English

[![NuGet](https://img.shields.io/nuget/v/usleep_win_cs)](https://www.nuget.org/packages/usleep_win_cs)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/sdkikgeneral-spec/usleep_win_cs/blob/main/LICENSE)

**High-accuracy, low-jitter microsecond sleep for Windows — pure C#, .NET 10+**

---

## Requirements

| Item | Value |
|---|---|
| Runtime | .NET 10.0+ (Windows) |
| OS | Windows 10 / 11 / Server 2019+ |
| Architecture | x64 / ARM64 |
| Namespace | `Usleep.Win` |

---

## Installation

This targets the **`net10.0-windows`, Windows-only** NuGet package.

```shell
dotnet add package usleep_win_cs
```

For Unity projects, do not use the NuGet package. Instead, get the Unity Windows /
Unity Generic DLL from the zip published on
[GitHub Releases](https://github.com/sdkikgeneral-spec/usleep_win_cs/releases) and drop it
into `Assets` (the Unity Generic DLL targets `netstandard2.1` with no P/Invoke).

---

## Overview

Hybrid approach combining WaitableTimer HR + CPU hint instructions (`PAUSE`/`YIELD`) + staged cooperative yielding.
Timestamps come from `Stopwatch.GetTimestamp()` (QPC internally) in NuGet builds (`net10.0-windows`), and from a direct QPC call in Unity builds.

> Windows is not a hard real-time OS. Accuracy is affected by power settings, virtualization, and background load.

---

## Usage

```csharp
using Usleep.Win;

// Basic
UsleepWin.SleepMicroseconds(500);    // 500 µs
UsleepWin.SleepNanoseconds(200_000); // 200 µs (ns unit)

// Deadline-based periodic loop (drift-resistant)
const ulong periodUs = 1_000;
ulong next = UsleepWin.NowSteadyMicroseconds();
for (;;) { next += periodUs; UsleepWin.SleepUntilSteadyMicroseconds(next); /* ... */ }

// Profile & tuning
UsleepWin.SetProfile(UsleepProfile.STRICT);             // lower jitter
UsleepWin.SetTailSpinMicroseconds(300);                 // spin 300 µs after timer wakeup
UsleepWin.SetYieldPolicy(UsleepYieldPolicy.SLEEP0);     // yield method

// Statistics
UsleepStats st = UsleepWin.GetStats(reset: false);
Console.WriteLine($"timer={st.WaitableTimerUses}, spin={st.SpinRelax}");
```

> To change timer resolution use `UsleepWin.InitTimerResolution(1)` / `ShutdownTimerResolution()` (`timeBeginPeriod(1)` is system-wide; usually unnecessary on Windows 10 v1803+).

---

## High-Precision Async Timer (`PreciseDelay`)

For cases requiring ±1–3 µs precision. Zero-allocation implementation using a dedicated spin thread, timer wheel, and `IValueTaskSource` pool.

> NuGet target (`net10.0-windows`) only. Excluded from Unity (`netstandard2.1`) builds via `#if !USLP_UNITY`.

```csharp
// Call once at startup (core 0 is prohibited)
PreciseDelay.Initialize(dedicatedCpuCore: 3);

// ≤5ms → dedicated spin thread, >5ms → WaitableTimer HR
await PreciseDelay.WaitAsync(TimeSpan.FromMicroseconds(500));
await PreciseDelay.WaitAsync(TimeSpan.FromMicroseconds(500), cancellationToken);

// Call once at shutdown
PreciseDelay.Shutdown();
```

> **Note:** To keep precision, continuations after `await PreciseDelay.WaitAsync(...)`
> may run inline on the dedicated spin thread. Blocking right after the `await`
> (`lock`, file/network I/O, synchronous waits, heavy logging) degrades the accuracy of
> every other wait pending at that moment. Move heavy work off with `Task.Run` or similar.
> See section 14.3 of [`document/specsheet_en.md`](https://github.com/sdkikgeneral-spec/usleep_win_cs/blob/main/document/specsheet_en.md) for details.

---

## API Reference

### `PreciseDelay` static class

| Method / Property | Description |
|---|---|
| `Initialize(int dedicatedCpuCore = 3)` | Call once at startup. Core 0 is prohibited (`ArgumentException`). |
| `Shutdown()` | Call once at shutdown. |
| `WaitAsync(TimeSpan, CancellationToken)` | High-precision async wait. Throws `InvalidOperationException` if not initialized. |
| `IsInitialized` | Whether the engine is initialized. |

### `UsleepWin` static class

| Method | Description |
|---|---|
| `SleepMicroseconds(ulong usec)` | Sleep for the specified microseconds |
| `SleepNanoseconds(ulong nsec)` | Sleep for the specified nanoseconds (converted to µs internally) |
| `SleepUntilSteadyMicroseconds(ulong targetUs)` | Sleep until the specified monotonic timestamp |
| `NowSteadyMicroseconds()` | Get current monotonic timestamp in microseconds |
| `SetProfile(UsleepProfile)` | Apply a profile (thread-local) |
| `SetTailSpinMicroseconds(uint)` | Set post-timer spin duration (thread-local). Throws `ArgumentOutOfRangeException` above `MaxTailSpinMicroseconds` (10000 µs) |
| `SetYieldPolicy(UsleepYieldPolicy)` | Set cooperative yield method (thread-local) |
| `MaxTailSpinMicroseconds` | Upper bound constant for tail spin duration (10000 µs = 10 ms) |
| `SetPowerMode(UsleepPowerMode)` | Set thread power throttling mode |
| `InitTimerResolution(uint ms)` | Request timer resolution via `timeBeginPeriod` |
| `ShutdownTimerResolution()` | Release timer resolution via `timeEndPeriod` |
| `GetStats(bool reset)` | Retrieve thread-local statistics snapshot |
| `ResetStats()` | Reset thread-local statistics counters |

### Enums & Struct

| Type | Values |
|---|---|
| `UsleepProfile` | `BALANCED` (default) / `STRICT` (low jitter) / `LOW_POWER` (power-saving) |
| `UsleepYieldPolicy` | `NONE` / `SWITCH_THREAD` / `SLEEP0` (default) / `SLEEP1` |
| `UsleepStats` | `SpinRelax` / `YieldSwitch` / `YieldSleep0` / `YieldSleep1` / `WaitableTimerUses` |

---

## Tuning Guide

- **Lower jitter:** `STRICT` + `SetTailSpinMicroseconds(300–500)`
- **Lower power:** `LOW_POWER` (timer-only, no spin)
- Measure your target period, acceptable late-arrival rate, and CPU budget while adjusting.

---

## License

MIT License — see [LICENSE](https://github.com/sdkikgeneral-spec/usleep_win_cs/blob/main/LICENSE) for details.

---

## Contributing

Issues and PRs are welcome. Benchmark results and tuning insights are also appreciated.
