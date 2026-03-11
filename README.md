# usleep_win_cs

[![NuGet](https://img.shields.io/nuget/v/usleep_win_cs)](https://www.nuget.org/packages/usleep_win_cs)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

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

```shell
dotnet add package usleep_win_cs
```

---

## 概要

WaitableTimer HR + QPC + CPU ヒント命令（`PAUSE`/`YIELD`）+ 段階的スレッド譲渡を組み合わせたハイブリッド待機方式。

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
| `SetTailSpinMicroseconds(uint)` | タイマー後スピン時間設定（スレッドローカル） |
| `SetYieldPolicy(UsleepYieldPolicy)` | スレッド譲渡ポリシー設定（スレッドローカル） |
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

MIT License — 詳細は [LICENSE](./LICENSE) を参照してください。

---
---

<a name="english"></a>

# English

[![NuGet](https://img.shields.io/nuget/v/usleep_win_cs)](https://www.nuget.org/packages/usleep_win_cs)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

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

```shell
dotnet add package usleep_win_cs
```

---

## Overview

Hybrid approach combining WaitableTimer HR + QPC + CPU hint instructions (`PAUSE`/`YIELD`) + staged cooperative yielding.

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
| `SetTailSpinMicroseconds(uint)` | Set post-timer spin duration (thread-local) |
| `SetYieldPolicy(UsleepYieldPolicy)` | Set cooperative yield method (thread-local) |
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

MIT License — see [LICENSE](./LICENSE) for details.

---

## Contributing

Issues and PRs are welcome. Benchmark results and tuning insights are also appreciated.
