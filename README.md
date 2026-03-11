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
| OS | Windows 10 / Windows 11 / Windows Server 2019 以降 |
| アーキテクチャ | x64 / ARM64 |
| 名前空間 | `Usleep.Win` |

---

## インストール

```shell
dotnet add package usleep_win_cs
```

または Visual Studio の NuGet パッケージマネージャーで `usleep_win_cs` を検索してください。

---

## 概要

`usleep_win_cs` は Windows 上で実用的な精度と低負荷の短時間待機を実現するライブラリです。
以下の要素を組み合わせたハイブリッド待機方式を採用しています。

- **High-Resolution Waitable Timer** — OS タイマーによるメインの待機
- **QueryPerformanceCounter (QPC)** — 高精度な現在時刻取得
- **CPU ヒント命令**（x86 `PAUSE` / ARM64 `YIELD`）— 最終スピン区間の最適化
- **Sleep(0) / SwitchToThread() / Sleep(1)** — 公平性を保つ段階的なスレッド譲渡

> **注意:** Windows はハードリアルタイム OS ではありません。  
> マイクロ秒スリープの精度は電源管理・仮想化・バックグラウンド負荷などの影響を受けます。

---

## 使用例

### 基本のスリープ

```csharp
using Usleep.Win;

UsleepWin.SleepMicroseconds(500);    // 500 µs 待機
UsleepWin.SleepNanoseconds(200_000); // 200 µs 待機（ns 単位）
```

### 締切方式の定周期ループ（ドリフト抑制）

```csharp
using Usleep.Win;

const ulong periodUs = 1_000; // 1 ms 周期
ulong next = UsleepWin.NowSteadyMicroseconds();

for (;;)
{
    next += periodUs;
    UsleepWin.SleepUntilSteadyMicroseconds(next);

    // ... 処理 ...
}
```

### プロファイル設定

```csharp
using Usleep.Win;

UsleepWin.SetProfile(UsleepProfile.STRICT);    // 低ジッタ優先
UsleepWin.SetProfile(UsleepProfile.LOW_POWER); // 省電力優先
UsleepWin.SetProfile(UsleepProfile.BALANCED);  // 既定に戻す（バランス型）
```

### 細かなチューニング

```csharp
using Usleep.Win;

UsleepWin.SetTailSpinMicroseconds(300);                    // タイマー後スピン時間を 300 µs に設定
UsleepWin.SetYieldPolicy(UsleepYieldPolicy.SWITCH_THREAD); // スレッド譲渡方法を変更
```

### 統計確認

```csharp
using Usleep.Win;

UsleepStats st = UsleepWin.GetStats(reset: false);
Console.WriteLine($"timer={st.WaitableTimerUses}, spin={st.SpinRelax}, sleep0={st.YieldSleep0}");
```

### タイマー分解能の変更（必要時のみ）

```csharp
using Usleep.Win;

if (UsleepWin.InitTimerResolution(1)) // timeBeginPeriod(1) — システム全体に影響
{
    // 高分解能タイマー依存の処理
    UsleepWin.ShutdownTimerResolution();
}
```

> `timeBeginPeriod(1)` はシステム全体の消費電力に影響します。必要な場合のみ使用してください。  
> Windows 10 v1803 以降では `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` フラグが利用可能なため、多くの場合は不要です。

---

## API リファレンス

### `UsleepWin` 静的クラス

| メソッド | 説明 |
|---|---|
| `SleepMicroseconds(ulong usec)` | 指定マイクロ秒待機 |
| `SleepNanoseconds(ulong nsec)` | 指定ナノ秒待機（内部でマイクロ秒に変換） |
| `SleepUntilSteadyMicroseconds(ulong targetUs)` | 指定モノトニック時刻まで待機（ドリフト抑制） |
| `NowSteadyMicroseconds()` | 現在のモノトニック時刻をマイクロ秒で取得 |
| `SetProfile(UsleepProfile)` | プロファイルを適用（スレッドローカル） |
| `SetTailSpinMicroseconds(uint)` | タイマー後スピン時間を設定（スレッドローカル） |
| `SetYieldPolicy(UsleepYieldPolicy)` | スレッド譲渡ポリシーを設定（スレッドローカル） |
| `SetPowerMode(UsleepPowerMode)` | スレッドの電力スロットリングモードを設定 |
| `InitTimerResolution(uint ms)` | `timeBeginPeriod` でタイマー分解能を要求 |
| `ShutdownTimerResolution()` | `timeEndPeriod` でタイマー分解能要求を解除 |
| `GetStats(bool reset)` | スレッドローカル統計スナップショットを取得 |
| `ResetStats()` | スレッドローカル統計カウンタをリセット |

### `UsleepProfile` 列挙型

| 値 | 説明 |
|---|---|
| `BALANCED`（既定） | 低ジッタと公平性のバランス |
| `STRICT` | 低ジッタ優先（CPU 消費増えやすい） |
| `LOW_POWER` | 省電力優先（ジッタ増えやすい） |

### `UsleepYieldPolicy` 列挙型

| 値 | 説明 |
|---|---|
| `NONE` | スピンのみ（明示的なスレッド譲渡なし） |
| `SWITCH_THREAD` | `SwitchToThread()` による譲渡 |
| `SLEEP0` | `Sleep(0)` による譲渡（`BALANCED` 既定） |
| `SLEEP1` | `Sleep(1)` による譲渡（`LOW_POWER` 既定） |

### `UsleepStats` 構造体

| プロパティ | 説明 |
|---|---|
| `SpinRelax` | スピンヒント命令の使用回数 |
| `YieldSwitch` | `SwitchToThread` 系譲渡回数 |
| `YieldSleep0` | `Sleep(0)` 系譲渡回数 |
| `YieldSleep1` | `Sleep(1)` 系譲渡回数 |
| `WaitableTimerUses` | WaitableTimer 経由の待機回数 |

---

## チューニングの目安

- **既定は `BALANCED`** — 一般用途に適切
- **低ジッタ優先:** `STRICT` + `SetTailSpinMicroseconds(300〜500)`
- **省電力優先:** `LOW_POWER`（スピンなし、タイマーのみ）
- 目標周期・許容遅着・CPU 使用率を実測しながら調整することを推奨します

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
| OS | Windows 10 / Windows 11 / Windows Server 2019+ |
| Architecture | x64 / ARM64 |
| Namespace | `Usleep.Win` |

---

## Installation

```shell
dotnet add package usleep_win_cs
```

Or search for `usleep_win_cs` in the Visual Studio NuGet Package Manager.

---

## Overview

`usleep_win_cs` achieves practical precision and low-overhead short waits on Windows
using a hybrid approach combining:

- **High-Resolution Waitable Timer** — primary OS timer wait
- **QueryPerformanceCounter (QPC)** — high-accuracy current-time measurement
- **CPU hint instructions** (x86 `PAUSE` / ARM64 `YIELD`) — final-phase spin optimization
- **Sleep(0) / SwitchToThread() / Sleep(1)** — staged cooperative yielding for fairness

> **Note:** Windows is not a hard real-time OS.  
> Microsecond sleep accuracy is affected by power settings, virtualization, and background load.

---

## Usage

### Basic sleep

```csharp
using Usleep.Win;

UsleepWin.SleepMicroseconds(500);    // sleep 500 µs
UsleepWin.SleepNanoseconds(200_000); // sleep 200 µs (ns unit)
```

### Deadline-based periodic loop (drift-resistant)

```csharp
using Usleep.Win;

const ulong periodUs = 1_000; // 1 ms period
ulong next = UsleepWin.NowSteadyMicroseconds();

for (;;)
{
    next += periodUs;
    UsleepWin.SleepUntilSteadyMicroseconds(next);

    // ... work ...
}
```

### Profile selection

```csharp
using Usleep.Win;

UsleepWin.SetProfile(UsleepProfile.STRICT);    // lower jitter, higher CPU
UsleepWin.SetProfile(UsleepProfile.LOW_POWER); // power-saving, higher jitter
UsleepWin.SetProfile(UsleepProfile.BALANCED);  // restore default
```

### Fine-grained tuning

```csharp
using Usleep.Win;

UsleepWin.SetTailSpinMicroseconds(300);                    // spin 300 µs after timer wakeup
UsleepWin.SetYieldPolicy(UsleepYieldPolicy.SWITCH_THREAD); // cooperative yield method
```

### Statistics

```csharp
using Usleep.Win;

UsleepStats st = UsleepWin.GetStats(reset: false);
Console.WriteLine($"timer={st.WaitableTimerUses}, spin={st.SpinRelax}, sleep0={st.YieldSleep0}");
```

### Timer resolution (only when needed)

```csharp
using Usleep.Win;

if (UsleepWin.InitTimerResolution(1)) // timeBeginPeriod(1) — system-wide effect
{
    // work that requires high-resolution timer
    UsleepWin.ShutdownTimerResolution();
}
```

> `timeBeginPeriod(1)` increases system-wide power consumption. Use only when necessary.  
> On Windows 10 v1803+, `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` is available and usually sufficient.

---

## API Reference

### `UsleepWin` static class

| Method | Description |
|---|---|
| `SleepMicroseconds(ulong usec)` | Sleep for the specified microseconds |
| `SleepNanoseconds(ulong nsec)` | Sleep for the specified nanoseconds (converted to µs internally) |
| `SleepUntilSteadyMicroseconds(ulong targetUs)` | Sleep until the specified monotonic-clock timestamp |
| `NowSteadyMicroseconds()` | Get current monotonic timestamp in microseconds |
| `SetProfile(UsleepProfile)` | Apply a preset profile (thread-local) |
| `SetTailSpinMicroseconds(uint)` | Set post-timer spin duration (thread-local) |
| `SetYieldPolicy(UsleepYieldPolicy)` | Set cooperative yield method (thread-local) |
| `SetPowerMode(UsleepPowerMode)` | Set thread power throttling mode |
| `InitTimerResolution(uint ms)` | Request timer resolution via `timeBeginPeriod` |
| `ShutdownTimerResolution()` | Release timer resolution via `timeEndPeriod` |
| `GetStats(bool reset)` | Retrieve thread-local statistics snapshot |
| `ResetStats()` | Reset thread-local statistics counters |

### `UsleepProfile` enum

| Value | Description |
|---|---|
| `BALANCED` (default) | Balanced low jitter and fairness |
| `STRICT` | Lower-jitter focused (typically higher CPU usage) |
| `LOW_POWER` | Power-saving focused (typically higher jitter) |

### `UsleepYieldPolicy` enum

| Value | Description |
|---|---|
| `NONE` | Spin only (no explicit yield) |
| `SWITCH_THREAD` | Yield via `SwitchToThread()` |
| `SLEEP0` | Yield via `Sleep(0)` (default for `BALANCED`) |
| `SLEEP1` | Yield via `Sleep(1)` (default for `LOW_POWER`) |

### `UsleepStats` struct

| Property | Description |
|---|---|
| `SpinRelax` | Spin-hint instruction usage count |
| `YieldSwitch` | `SwitchToThread`-based yield count |
| `YieldSleep0` | `Sleep(0)`-based yield count |
| `YieldSleep1` | `Sleep(1)`-based yield count |
| `WaitableTimerUses` | Waitable timer usage count |

---

## Tuning Guide

- **Default is `BALANCED`** — suitable for most workloads
- **Lower jitter:** `STRICT` + `SetTailSpinMicroseconds(300–500)`
- **Lower power:** `LOW_POWER` (timer-only, no spin)
- Measure your target period, acceptable late-arrival rate, and CPU budget while adjusting.

---

## License

MIT License — see [LICENSE](./LICENSE) for details.

---

## Contributing

Issues and PRs are welcome. Benchmark results and tuning insights are also appreciated.
