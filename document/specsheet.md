# usleep_win_cs 内部仕様書

> バージョン: 0.2.x
> 対象: ライブラリ開発者・コントリビューター向け

[English version](specsheet_en.md)

---

## 目次

1. [設計方針](#1-設計方針)
2. [ビルドバリアント（プリプロセッサ定数）](#2-ビルドバリアントプリプロセッサ定数)
3. [P/Invoke 戦略](#3-pinvoke-戦略)
4. [時刻取得（`NowUs()`）](#4-時刻取得nowus)
5. [WaitableTimer の管理](#5-waitabletimer-の管理)
6. [スリープアルゴリズム](#6-スリープアルゴリズム)
7. [CPU ヒント命令（`SpinHints`）](#7-cpu-ヒント命令spinhints)
8. [タイマー分解能管理](#8-タイマー分解能管理)
9. [スレッドローカル状態](#9-スレッドローカル状態)
10. [プロファイル別動作詳細](#10-プロファイル別動作詳細)
11. [統計カウンタ](#11-統計カウンタ)
12. [Unity 向けビルドの差異](#12-unity-向けビルドの差異)
13. [セキュリティ・安全性](#13-セキュリティ安全性)
14. [PreciseDelay 高精度非同期タイマー](#14-precisedelay-高精度非同期タイマー)

---

## 1. 設計方針

### 1.1 目標

- Windows 上でマイクロ秒オーダーの短時間待機を **実用的な精度** で実現する
- CPU 負荷とジッタのバランスをプロファイルで切り替えられるようにする
- unsafe ブロックを使わず **pure C#** で実装し、P/Invoke のみで OS API を呼び出す
- 同一ソースを NuGet（net10.0-windows）と Unity（netstandard2.1）両方向けにビルドできる構造を維持する

### 1.2 制約

- **ハードリアルタイム特性は保証しない**。Windows はソフトリアルタイム OS であり、スケジューラ・電源管理・仮想化の影響を受ける
- タイマー分解能変更（`timeBeginPeriod`）はシステム全体の設定変更を伴う
- スレッドローカル設計のため、設定はスレッドをまたいで継承されない

---

## 2. ビルドバリアント（プリプロセッサ定数）

コンパイル時定数により、ターゲット環境に応じて実装が切り替わる。

| 定数 | 適用ビルド | 効果 |
|---|---|---|
| `USLP_GENERATOR` | NuGet（net10.0-windows） | `LibraryImport` source generator を使用。`AggressiveOptimization`・`SkipLocalsInit` 有効 |
| `USLP_WINDOWS` | Unity Windows-only DLL | `DllImport` + `SuppressUnmanagedCodeSecurity` を使用 |
| `USLP_X64_ONLY` | NuGet x64 専用ビルド（オプション） | `X86Base.Pause()` を実行時分岐なしで直接呼び出す |
| `USLP_NUGET` | NuGet ビルド識別子 | 現状は `USLP_GENERATOR` と併用。将来の条件分岐用 |
| `USLP_UNITY` | Unity DLL（両バリアント） | `PreciseDelay` 関連の5ファイルをコンパイルから除外する（`#if !USLP_UNITY`） |

**Generic ビルド（どちらも未定義）:**  
すべての Win32 API 呼び出しがコンパイルから除外される。`Platform.IsWindows` は常に `false` を返すため、WaitableTimer・QPC・Sleep 系 API は一切呼ばれず、`Thread.Yield()` / `Thread.Sleep()` / `Stopwatch` ベースのフォールバック実装になる。

### 定数の組み合わせとターゲット対応

| ターゲット | 実際に定義される定数 |
|---|---|
| NuGet（net10.0-windows） | `USLP_WINDOWS` + `USLP_NUGET` + `USLP_GENERATOR` |
| Unity Windows-only DLL | `USLP_WINDOWS` |
| Unity Generic DLL | （なし）|

---

## 3. P/Invoke 戦略

### 3.1 NuGet ビルド：`LibraryImport`（`USLP_GENERATOR`）

.NET 7 以降で利用可能な source generator 方式。コンパイル時にマーシャリングコードが生成される。

- **`QueryPerformanceCounter` / `QueryPerformanceFrequency`**  
  `[SuppressGCTransition]` を付与。GC 安全な状態への遷移コストをなくし、ホットパスでの呼び出しオーバーヘッドを最小化する。

- **`CreateWaitableTimerEx`**  
  エントリポイント `CreateWaitableTimerExW`（Unicode）を明示指定。`StringMarshalling.Utf16` を指定。

- **`SetWaitableTimer`**
  `ref LARGE_INTEGER` による duetime 渡し（100ns 単位の負値 = 相対時間）。

- **`CreateWaitableTimerExSafe` / `SetWaitableTimerSafe`**（`SafeWaitHandle` オーバーロード）
  `PreciseDelay.WaitableTimerAsync` が使用する非同期パス専用。`SafeWaitHandle` を返す / 受け取るバリアント。
  エントリポイントはそれぞれ `CreateWaitableTimerExW` / `SetWaitableTimer` と同一（`EntryPoint` 属性で明示）。

- **`SetThreadInformation`**
  `THREAD_POWER_THROTTLING_STATE` 構造体を `ref` で渡す。スレッド電力スロットリングの設定に使用。

### 3.2 Unity Windows ビルド：`DllImport`（`USLP_WINDOWS`）

旧来の `DllImport` 方式。`[SuppressUnmanagedCodeSecurity]` を付与してセキュリティチェックを省略し、呼び出しオーバーヘッドを削減する。

### 3.3 Unity Generic ビルド

P/Invoke なし。Win32 分岐はすべてコンパイル除外。

---

## 4. 時刻取得（`NowUs()`）

`InternalTiming.NowUs()` はモノトニッククロックの現在時刻をマイクロ秒で返す。

### 優先順位

```
【USLP_GENERATOR（NuGet ビルド）】

1. NativeClock パス（Stopwatch.IsHighResolution == true）
   NativeClock.GetTimestamp() * _tickToUs
   ※ KUSER_SHARED_DATA 直読み（~1 ns）。非対応環境では内部で Stopwatch にフォールバック済み

2. TickCount フォールバック（Stopwatch.IsHighResolution == false）
   (ulong)(uint)Environment.TickCount * 1000UL

【USLP_WINDOWS（Unity Windows ビルド）】

1. QPC パス（_isWin == true かつ _qpcFreq > 0）
   QueryPerformanceCounter(out c)
   → (ulong)(c.QuadPart * 1_000_000L / _qpcFreq)

2. Stopwatch パス（Stopwatch.IsHighResolution == true）
   Stopwatch.GetTimestamp() * _tickToUs

3. TickCount フォールバック（Stopwatch.IsHighResolution == false）
   (ulong)(uint)Environment.TickCount * 1000UL
```

### 精度

- NativeClock（NuGet）：QPC と同等の精度（±1 µs 以下）かつ P/Invoke ゼロ（~1 ns オーバーヘッド）
- QPC（Unity Windows）：通常 ±1 µs 以下。周波数は起動時に `_qpcFreq` へキャッシュ
- Stopwatch（高分解能）：QPC と同等（内部的に QPC を使用する環境が多い）
- TickCount フォールバック：1 ms 粒度（非 Windows 環境でのみ使用）

---

## 5. WaitableTimer の管理

### 5.1 スレッドローカルハンドル

`_tTimer` は `[ThreadStatic]` フィールド（`InternalTiming` 内）。各スレッドが独立したタイマーハンドルを保持するため、スレッド間の競合がない。

> ハンドルのクローズ（`CloseHandle`）はスレッド終了時に OS が回収するため、明示的なクローズは実装されていない。長期稼働スレッドでの再生成は行われない設計。

### 5.2 取得フロー（`GetTimer()`）

```
if _tTimer != IntPtr.Zero → キャッシュ済みハンドルを返す

_createWaitableTimerExState の確認:
  >= 0 → CreateWaitableTimerEx を試みる
  < 0  → CreateWaitableTimerEx は使用不可（EntryPointNotFoundException を過去に捕捉済み）

1. CreateWaitableTimerEx(NULL, NULL, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS)
   成功 → _tTimer に保存し、_createWaitableTimerExState = 1

2. 失敗 → CreateWaitableTimerEx(NULL, NULL, 0, TIMER_ALL_ACCESS)（フラグなし）

3. EntryPointNotFoundException 捕捉 → _createWaitableTimerExState = -1

最終フォールバック:
   _tTimer == IntPtr.Zero → CreateWaitableTimer(NULL, false, NULL)
```

`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`（`0x00000002`）は Windows 10 バージョン 1803（RS4）以降で利用可能。このフラグにより、タイマーの待機精度が向上する。

### 5.3 タイマーの使い方

```csharp
// due は 100ns 単位の負値（相対時間）
var due = new LARGE_INTEGER { QuadPart = -(coarseUs * 10L) };
SetWaitableTimer(h, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
WaitForSingleObject(h, 0xFFFFFFFF); // INFINITE
```

`tailSpinUs > 0` の場合は `coarseUs = usec - tailSpinUs` として、タイマーを目標より早めに起こし、残りをスピンで補完することで遅着を抑制する。

---

## 6. スリープアルゴリズム

### 6.1 `SleepMicroseconds(usec)` フロー

```
usec == 0 → CoarseYield(SWITCH_THREAD) して return

プロファイル別しきい値の決定:
  STRICT:    timerFirstUs=1500, preferSpinBelow=500
  LOW_POWER: timerFirstUs=1000, preferSpinBelow=0
  BALANCED:  timerFirstUs=2000, preferSpinBelow=200

if (usec >= timerFirstUs) OR (usec > preferSpinBelow):
    SleepByTimer(usec, tailSpinUs, policy, lowPower)
else:
    SpinWithPeriodicYield(NowUs() + usec, tailSpinUs, policy)
```

**判定の意図:**
- `usec > preferSpinBelow`：ある程度の長さならタイマーを活用してスピン浪費を避ける
- `usec >= timerFirstUs`：十分長ければ純タイマー待機（スピン区間より長い）
- それ以下の短い待機：純スピンでオーバーヘッドを避ける

### 6.2 `SleepByTimer(usec, tailSpinUs, policy, lowPower)` フロー

```
targetUs = NowUs() + usec
h = GetTimer()

if h != IntPtr.Zero:
    coarseUs = (tailSpinUs > 0 && usec > tailSpinUs) ? usec - tailSpinUs : usec
    due = -(coarseUs * 10)
    if SetWaitableTimer(h, ref due, ...):
        tHrTimerUses++
        WaitForSingleObject(h, INFINITE)

        if tailSpinUs > 0:
            SpinWithPeriodicYield(targetUs, 0, NONE)  // ピュアスピンで残時間を補完
        elif !lowPower:
            while NowUs() < targetUs: HintOnce()      // 短スピン（tail なし・非省電力）
        return

// タイマー取得失敗または SetWaitableTimer 失敗
if usec >= 1000:
    Sleep(usec / 1000)  // ms 単位切り捨て
    tYieldSleep1++
    if tailSpinUs > 0: SpinWithPeriodicYield(targetUs, ...)
    return

SpinWithPeriodicYield(targetUs, tailSpinUs, policy)
```

### 6.3 `SpinWithPeriodicYield(targetUs, tailSpinUs, policy)` フロー

```
ctr = 0
loop:
    now = NowUs()
    if now >= targetUs: break
    remain = targetUs - now

    if remain > tailSpinUs:          // まだ tail spin 区間に入っていない
        if (++ctr & 63) == 0:        // 64 反復ごとに 1 回 OS 譲渡
            CoarseYield(policy)
        else:
            HintFewTimes(3)          // 3 回スピンヒント
            tSpinRelax += 3
    else:                            // tail spin 区間（目標直前）
        HintOnce()                   // 最小スピン
        tSpinRelax++
```

**64 反復ごとに 1 回 CoarseYield** という頻度は、スピンによる CPU 独占を防ぎながらジッタも最小化するバランス点として設計されている。

---

## 7. CPU ヒント命令（`SpinHints`）

### 7.1 分岐マトリクス

| ビルド | `X86Base.IsSupported` | `ArmBase.IsSupported` | 実行 |
|---|---|---|---|
| `USLP_GENERATOR` + `USLP_X64_ONLY` | — | — | `X86Base.Pause()` 直接 |
| `USLP_GENERATOR`（汎用） | true | — | `X86Base.Pause()` |
| `USLP_GENERATOR`（汎用） | false | true | `ArmBase.Yield()` |
| `USLP_GENERATOR`（汎用） | false | false | `SpinWait.SpinOnce()` |
| その他（Unity 等） | — | — | `SpinWait.SpinOnce()` |

### 7.2 各命令の効果

- **`x86 PAUSE`**: SMT（ハイパースレッディング）環境でスピンループの電力消費と競合ペナルティを削減する。ループ検出のヒントを CPU に与える
- **`ARM64 YIELD`**: x86 PAUSE に相当。同コア上の別スレッドへの実行ヒント
- **`SpinWait.SpinOnce()`**: .NET ランタイムが環境に応じて適切なヒントを発行するフォールバック

### 7.3 `AggressiveOptimization` 属性

`USLP_GENERATOR` ビルドでは以下のメソッドに `MethodImplOptions.AggressiveOptimization` を適用する：
- `UsleepWin.SleepMicroseconds`
- `UsleepWin.SleepUntilSteadyMicroseconds`
- `InternalTiming.NowUs`
- `InternalTiming.SleepByTimer`
- `InternalTiming.SpinWithPeriodicYield`
- `InternalTiming.CoarseYield`
- `SpinHints.HintOnce`
- `SpinHints.HintFewTimes`

JIT コンパイラによるループ最適化・インライン展開を促し、スピンループのオーバーヘッドを低減する。

### 7.4 `SkipLocalsInit`

`USLP_GENERATOR` ビルドでは `AssemblyAttributes.cs` にて `[module: SkipLocalsInit]` を適用。ローカル変数のゼロ初期化をスキップし、ホットパスの初期化コストを削減する。

---

## 8. タイマー分解能管理

### 8.1 API

- `timeBeginPeriod(ms)` / `timeEndPeriod(ms)`：`winmm.dll` の API
- システム全体のタイマー割り込み周期を変更する（例: 既定 15.6 ms → 1 ms）

### 8.2 実装の安全策

- `_timerResolutionMs`（グローバル）と `_timerResolutionLock` で状態を保護
- 同じ値で `InitTimerResolution` が再度呼ばれた場合は即 `true` を返す
- 値が異なる場合は一度 `timeEndPeriod` で解除してから再設定
- `ShutdownTimerResolution()` で `timeEndPeriod` を呼び、`_timerResolutionMs = 0` にリセット

### 8.3 副作用・注意点

- `timeBeginPeriod(1)` はプロセスをまたいでシステム全体の消費電力を増加させる
- Windows 11 では `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` フラグが利用可能なため、多くのユースケースで `timeBeginPeriod` は不要
- バッテリー駆動環境では利用を控えることを推奨する

---

## 9. スレッドローカル状態

以下のフィールドはすべて `[ThreadStatic]`。スレッドごとに独立した状態を持つ。

### `UsleepWin`（公開設定）

| フィールド | 型 | 既定値 | 説明 |
|---|---|---|---|
| `_profile` | `UsleepProfile` | `BALANCED` | 現在のプロファイル |
| `_tailSpinUs` | `uint` | `250` | タイマー後スピン時間（µs） |
| `_yieldPolicy` | `UsleepYieldPolicy` | `SLEEP0` | 協調的スレッド譲渡方法 |

### `InternalTiming`（内部状態）

| フィールド | 型 | 説明 |
|---|---|---|
| `_tTimer` | `IntPtr` | スレッドごとの WaitableTimer ハンドル |
| `tSpinRelax` | `ulong` | スピンヒント使用回数（統計） |
| `tYieldSwitch` | `ulong` | SwitchToThread 系譲渡回数（統計） |
| `tYieldSleep0` | `ulong` | Sleep(0) 系譲渡回数（統計） |
| `tYieldSleep1` | `ulong` | Sleep(1) 系譲渡回数（統計） |
| `tHrTimerUses` | `ulong` | WaitableTimer 使用回数（統計） |

### グローバル（プロセス共有）

| フィールド | 型 | 説明 |
|---|---|---|
| `_timerResolutionMs` | `uint` | 現在要求中のタイマー分解能（ms）。0 = 未設定 |
| `_timerResolutionLock` | `object` | `timeBeginPeriod` の排他制御ロック |
| `_qpcFreq` | `long` | QPC 周波数（起動時にキャッシュ）。`USLP_WINDOWS` ビルドのみ使用。NuGet ビルドでは 0 固定 |
| `_isWin` | `bool` | Windows 環境かどうか（起動時に確定） |
| `_hires` | `bool` | `Stopwatch.IsHighResolution` |
| `_tickToUs` | `double` | Stopwatch tick → µs 変換係数 |
| `_createWaitableTimerExState` | `int` | `CreateWaitableTimerEx` の利用可能性（0: 未確認、1: 成功、-1: 不可） |

---

## 10. プロファイル別動作詳細

### `BALANCED`（既定）

| パラメータ | 値 | 意味 |
|---|---|---|
| `timerFirstUs` | 2000 µs | 2 ms 以上でタイマーを優先使用 |
| `preferSpinBelow` | 200 µs | 200 µs 超であればタイマー使用を検討 |
| `tailSpinUs` | 250 µs | タイマー後のスピン補完時間 |
| `yieldPolicy` | `SLEEP0` | 待機中のスレッド譲渡は `Sleep(0)` |

### `STRICT`

| パラメータ | 値 | 意味 |
|---|---|---|
| `timerFirstUs` | 1500 µs | 1.5 ms 以上でタイマー使用（より積極的） |
| `preferSpinBelow` | 500 µs | 500 µs 超でもタイマー使用を試みる |
| `tailSpinUs` | 400 µs（最低 300） | スピン補完時間を長めに設定 |
| `yieldPolicy` | `SWITCH_THREAD` | より応答性の高いスレッド譲渡 |

> `SetProfile(STRICT)` 時、`_tailSpinUs` が 300 未満だった場合は 400 に強制設定される。

### `LOW_POWER`

| パラメータ | 値 | 意味 |
|---|---|---|
| `timerFirstUs` | 1000 µs | 1 ms 以上でタイマーを使用 |
| `preferSpinBelow` | 0 µs | スピンを優先する区間ゼロ |
| `tailSpinUs` | 0 µs | タイマー後スピンなし |
| `yieldPolicy` | `SLEEP1` | 節電寄りの `Sleep(1)` |

`lowPower == true` の場合、タイマー待機後は `while (NowUs() < targetUs)` の超過チェックも行わない（即 return）。

---

## 11. 統計カウンタ

各カウンタはスレッドローカルな `ulong` 型。オーバーフローは実用上発生しない想定（数兆回まで安全）。

| カウンタ | インクリメントタイミング |
|---|---|
| `tSpinRelax` | `HintOnce()` 呼び出しごとに +1、`HintFewTimes(n)` 呼び出しで +n |
| `tYieldSwitch` | `CoarseYield(SWITCH_THREAD)` 時に +1 |
| `tYieldSleep0` | `CoarseYield(SLEEP0)` 時に +1 |
| `tYieldSleep1` | `CoarseYield(SLEEP1)` 時または `Sleep(ms)` フォールバック時に +1 |
| `tHrTimerUses` | `SetWaitableTimer` + `WaitForSingleObject` の呼び出し成功時に +1 |

`GetStats(reset: true)` を渡すと取得と同時に全カウンタをゼロリセットできる。

---

## 12. Unity 向けビルドの差異

| 項目 | NuGet (`USLP_GENERATOR`) | Unity Windows (`USLP_WINDOWS`) | Unity Generic |
|---|---|---|---|
| フレームワーク | `net10.0-windows` | `netstandard2.1` | `netstandard2.1` |
| P/Invoke | `LibraryImport` | `DllImport` | なし |
| Win32 コードパス | 有効 | 有効 | 無効 |
| `AggressiveOptimization` | 有効 | 無効 | 無効 |
| `SkipLocalsInit` | 有効 | 無効 | 無効 |
| CPU 固有命令（PAUSE/YIELD） | 有効（実行時分岐） | `SpinWait` | `SpinWait` |
| `SuppressGCTransition` | 宣言あり（QPC 系）/ ホットパスでは不使用 | 無効 | — |
| `SuppressUnmanagedCodeSecurity` | 無効（不要） | 有効 | — |

Unity 向けでは `USLP_X64_ONLY` は通常指定しない（マルチプラットフォーム DLL のため）。

---

## 13. セキュリティ・安全性

- **P/Invoke 先のライブラリ**: `kernel32.dll`・`winmm.dll` のみ。いずれも Windows 標準システム DLL
- **文字列引数**: `CreateWaitableTimerEx` / `CreateWaitableTimer` に渡す `lpTimerName` は常に `null`（名前付きタイマーは使用しない）
- **例外の内部捕捉**: `EntryPointNotFoundException` は `GetTimer()` 内で捕捉済み。呼び出し元への漏洩なし
- **タイマーハンドルの共有なし**: `[ThreadStatic]` により各スレッドが独立したハンドルを保持
- **タイマー分解能の重複設定防止**: `_timerResolutionLock` による排他制御で `timeBeginPeriod` の二重呼び出しを防止
- **整数オーバーフロー（Unity/USLP_WINDOWS）**: `NowUs()` の QPC 計算 `c.QuadPart * 1_000_000L / _qpcFreq` は `long` 演算。QPC カウンタが `long.MaxValue / 1_000_000` を超えるのは数千年後であり実用上問題なし。NuGet ビルドでは `NativeClock.GetTimestamp() * _tickToUs`（浮動小数点乗算）を使用するため整数オーバーフローは発生しない

---

---

## 14. PreciseDelay 高精度非同期タイマー

### 14.1 概要

`PreciseDelay` は `UsleepWin` では達成できない **±1〜3 µs** 精度の非同期待機を提供する静的クラス。
専用スピンスレッド・タイマーホイール・`IValueTaskSource` プールの組み合わせにより、**ゼロアロケーション**での高精度スケジューリングを実現する。

NuGet ターゲット（`net10.0-windows`）専用。Unity DLL ビルドでは `#if !USLP_UNITY` により全5ファイルがコンパイルから除外される。

### 14.2 クラス構成

| クラス / ファイル | 役割 |
| --- | --- |
| `NativeClock` (`src/NativeClock.cs`) | `KUSER_SHARED_DATA`（アドレス `0x7FFE0000`）直読みによる ~1 ns タイムスタンプ取得。P/Invoke ゼロのホットパス。OS または値が非信頼の場合は `Stopwatch.GetTimestamp()` にフォールバック |
| `PreciseWaitItem` (`src/PreciseWaitItem.cs`) | `IValueTaskSource` + `ObjectPool<T>` による待機アイテム。`ManualResetValueTaskSourceCore<bool>` でゼロアロケーション `ValueTask` を発行 |
| `TimerWheel` (`src/TimerWheel.cs`) | スロット数 4096 のタイマーホイール。`Math.BigMul` を使ったマジックナンバー除算で O(1) スロット計算 |
| `SpinCoreEngine` (`src/SpinCoreEngine.cs`) | 専用 CPU コアに固定されたスピンスレッド。`NtSetTimerResolution(1)` でシステムタイマー分解能を最小化し、`TimerWheel.Advance()` をタイトループで呼び出す |
| `PreciseDelay` (`src/PreciseDelay.cs`) | 公開 API。≤5 ms はスピンパス、>5 ms は WaitableTimer HR パスに自動振り分け |

### 14.3 NativeClock の実装

`KUSER_SHARED_DATA` 構造体のオフセット `0x320`（`InterruptTime`）を `unsafe` ポインタで直接読む。
64 ビット値が 2 回の 32 ビット読み取りにまたがるため、High → Low → High の順に読み取り、High が一致するまでリトライする（ティア読み取りパターン）。

```text
address = (byte*)0x7FFE0000 + 0x320
loop:
    hi1 = *(uint*)(address + 4)
    lo  = *(uint*)(address)
    hi2 = *(uint*)(address + 4)
    if hi1 == hi2: return (long)((ulong)hi1 << 32 | lo)
```

値は 100 ns 単位。`Stopwatch.Frequency` ベースの係数でスケールし、`Stopwatch.GetTimestamp()` と同じ単位に変換する。

### 14.4 TimerWheel の設計

#### スロット計算（O(1) マジックナンバー除算）

`ticksPerSlot = Stopwatch.Frequency / 1_000_000`（1 µs あたりのティック数）を構築時に計算し、その逆数に相当するマジックマルチプライヤを `ComputeMagicNumbers()` で導出する。

```text
slot = (diff × magicMultiplier) >> (magicShift - 64)  [上位 64 ビット]
     & SlotMask
```

`diff = timestamp - _baseTimestamp`（`_baseTimestamp` は構築時に固定）。
`long` の範囲内でオーバーフローするまでは **約 29,000 年**（QPC 周波数 ~10 MHz 基準）であり実用上問題なし。

#### `ResetBase()` を削除した理由

仕様書の初期版では `_baseTimestamp` をリセットする `ResetBase()` メソッドが存在したが、**既にキュー済みのアイテムのスロットインデックスが旧 base 基準で格納されているため、リセット後に最大 ~4 ms の遅延が発生するバグ**が判明した。`_baseTimestamp` を `readonly` にして構築時1回だけ設定することで問題を根本解消した。

### 14.5 WaitAsync のルーティング

```text
delay ≤ 0          → 即完了（ValueTask.CompletedTask 相当）
0 < delay ≤ 5 ms   → SpinCoreEngine キューに enqueue（スピンパス）
delay > 5 ms       → WaitableTimerAsync（WaitableTimer HR パス）
```

スピンパスでは `PreciseWaitItemPool.Rent()` でアイテムを取得し、`TimerWheel.Enqueue()` でデッドライン付きでキューイングする。
SpinCoreEngine のスピンループが `TimerWheel.Advance()` を呼び出してスロットを消化し、`PreciseWaitItem.Complete()` → `IValueTaskSource.SetResult()` で待機側を再開する。

WaitableTimer HR パスでは `ThreadPool.RegisterWaitForSingleObject` を使用する。`SafeWaitHandle` を `EventWaitHandle` でラップして `WaitHandle` 型要件を満たす。

### 14.6 ライフサイクルと安全性

| 操作 | 条件 | 例外 |
| --- | --- | --- |
| `Initialize(cpuCore)` | `cpuCore == 0` | `ArgumentException` |
| `Initialize(cpuCore)` | 既に初期化済み | `InvalidOperationException` |
| `WaitAsync(...)` | 未初期化 / Shutdown 後 | `InvalidOperationException` |
| `WaitAsync(..., ct)` | `ct` が既にキャンセル済み | `OperationCanceledException` |

`Initialize()` 内では `SpinCoreEngine` の一時変数に代入してから `Initialize()` を呼び、成功した場合のみ `_engine` フィールドに代入する（例外時に `IsInitialized` が `true` になるバグを防止）。

### 14.7 関連ファイル

| ファイル | 内容 |
| --- | --- |
| [src/NativeClock.cs](../src/NativeClock.cs) | KUSER_SHARED_DATA 直読みタイムスタンプ |
| [src/PreciseWaitItem.cs](../src/PreciseWaitItem.cs) | IValueTaskSource + ObjectPool 待機アイテム |
| [src/TimerWheel.cs](../src/TimerWheel.cs) | O(1) マジックナンバー除算タイマーホイール |
| [src/SpinCoreEngine.cs](../src/SpinCoreEngine.cs) | 専用コアスピンスレッドエンジン |
| [src/PreciseDelay.cs](../src/PreciseDelay.cs) | 公開 API（Initialize / Shutdown / WaitAsync） |
| [tests/UsleepWin.Tests/PreciseDelayTests.cs](../tests/UsleepWin.Tests/PreciseDelayTests.cs) | 全 18 件のテスト |
| [document/test_result.md](test_result.md) | テスト結果レポート（全 33 件合格） |

---

*本仕様書は `src/` 以下のソースコードを参照しています。仕様と実装に差異がある場合はソースコードを正とします。*
