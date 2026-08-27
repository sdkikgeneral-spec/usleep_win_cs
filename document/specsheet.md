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
| `USLP_UNITY` | Unity DLL（両バリアント） | `PreciseDelay` 関連の4ファイルをコンパイルから除外する（`#if !USLP_UNITY`） |

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
  `THREAD_POWER_THROTTLING_STATE` 構造体を `ref` で渡す。スレッド電力スロットリングの設定に使用。詳細は 3.4 節。

### 3.2 Unity Windows ビルド：`DllImport`（`USLP_WINDOWS`）

旧来の `DllImport` 方式。`[SuppressUnmanagedCodeSecurity]` を付与してセキュリティチェックを省略し、呼び出しオーバーヘッドを削減する。

### 3.3 Unity Generic ビルド

P/Invoke なし。Win32 分岐はすべてコンパイル除外。

### 3.4 `SetPowerMode(UsleepPowerMode)` の実装

`THREAD_INFORMATION_CLASS` の `ThreadPowerThrottling` は列挙値 **3** である
（`src/Interop/NativeMethods.Partial.cs` の `ThreadPowerThrottling` 定数）。

> 過去にこの定数を誤って `11` としていた時期があり、`SetThreadInformation` が
> `ERROR_INVALID_PARAMETER` で失敗し、`SetPowerMode` は指定モードによらず常に `false`
> を返していた。C++ 版移植元では別途修正済みだったが C# 側への移植が漏れていた。

範囲外の `enum` 値は `Enum.IsDefined(typeof(UsleepPowerMode), mode)` で弾き、即 `false` を
返す。Windows 以外のビルドでは常に `false`。

`THREAD_POWER_THROTTLING_STATE` の `ControlMask`（どのスロットリングを自前で制御するか）と
`StateMask`（それを有効にするか）を、モードごとに次のように設定する。

| モード | `ControlMask` | `StateMask` | 挙動 |
|---|---|---|---|
| `ECO` | `THREAD_POWER_THROTTLING_EXECUTION_SPEED` | `THREAD_POWER_THROTTLING_EXECUTION_SPEED` | 実行速度スロットリングを自前で有効化 |
| `PERF` | `THREAD_POWER_THROTTLING_EXECUTION_SPEED` | `0` | 自前で制御し、スロットリングは無効化（性能優先） |
| `DEFAULT` | `0` | `0` | 自前制御を解除し OS 既定の挙動に戻す |

> 以前は `DEFAULT` でも `ControlMask` を立てたままにしていたため、OS 既定へ戻らず
> `PERF` と同じ扱いになっていた。3 モードを明確に区別する分岐へ修正済み。

**適用範囲は呼び出しスレッドのみ。** `PreciseDelay` のスピンスレッド（`SpinCoreEngine`）や
他のスレッドには一切影響しない。

---

## 4. 時刻取得（`NowUs()`）

`InternalTiming.NowUs()` はモノトニッククロックの現在時刻をマイクロ秒で返す。

### 優先順位

```
【USLP_GENERATOR（NuGet ビルド）】

1. Stopwatch パス（Stopwatch.IsHighResolution == true）
   Stopwatch.GetTimestamp() * _tickToUs
   ※ Stopwatch.GetTimestamp() は内部で QPC を呼ぶ（実測 ~20 ns/call）

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

【いずれの定数も未定義（Generic ビルド）】

上記 USLP_WINDOWS ブロックの 2〜3（Stopwatch パス→TickCount フォールバック）と同じ。
```

> 以前は NuGet ビルドで `KUSER_SHARED_DATA`（`0x7FFE0000`）を直接読む `NativeClock` を
> 優先経路として使っていたが撤去した（`src/NativeClock.cs` は削除済み）。実測したところ
> `NativeClock` が「単調増加するカウンタ」として読んでいたオフセット `0x3B8`
> （QpcBias のつもり）は 50 ms 経過しても差分が 0 で、shift として読んでいた `0x3C4` は
> `ActiveGroupCount` であって本来の `QpcShift`（`0x3C7`）ではなかった。このため起動時の
> 信頼性検証が常に失敗し、実際には毎回 `Stopwatch.GetTimestamp()` へフォールバックして
> いた。検証に使っていた `Interlocked.MemoryBarrierProcessWide()` 自体も実測 88 ns/call と
> `Stopwatch.GetTimestamp()` の実測 19.7 ns/call より遅く、「~1 ns・P/Invoke ゼロ」という
> 前提が成立していなかった。

### 精度

- QPC（Unity Windows）：通常 ±1 µs 以下。周波数は起動時に `_qpcFreq` へキャッシュ
- Stopwatch（高分解能、NuGet ビルドはこちらのみ）：QPC と同等（内部的に QPC を使用する環境が多い。実測 ~20 ns/call）
- TickCount フォールバック：1 ms 粒度（`Stopwatch.IsHighResolution == false` の環境でのみ使用）

いずれの経路も **精度を保証するものではない**。Windows はハードリアルタイム OS ではなく、
スケジューラ・電源管理・仮想化の影響で実測値は変動する。

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
- **整数オーバーフロー（Unity/USLP_WINDOWS）**: `NowUs()` の QPC 計算 `c.QuadPart * 1_000_000L / _qpcFreq` は `long` 演算。QPC カウンタが `long.MaxValue / 1_000_000` を超えるのは数千年後であり実用上問題なし。NuGet ビルド（`USLP_GENERATOR`）では `Stopwatch.GetTimestamp() * _tickToUs`（浮動小数点乗算）を使用するため同様に整数オーバーフローは発生しない
- **テスト用の内部可視化**: `USLP_GENERATOR` ビルドのみ、`src/AssemblyAttributes.cs` で `[assembly: InternalsVisibleTo("UsleepWin.Tests")]` を宣言している。`TimerWheel` / `PreciseWaitItem` は `internal` のままだが、境界条件（過去 deadline の大量投入、ホイール範囲外の deadline）は `PreciseDelay` 越しの結合テストでは再現できないため、テストアセンブリから直接操作できるようにしている。公開 API の可視性には影響しない

---

---

## 14. PreciseDelay 高精度非同期タイマー

### 14.1 概要

`PreciseDelay` は `UsleepWin` では達成できない **±1〜3 µs** 精度の非同期待機を提供する静的クラス。
専用スピンスレッド・タイマーホイール・`IValueTaskSource` プールの組み合わせにより、**ゼロアロケーション**での高精度スケジューリングを実現する。

NuGet ターゲット（`net10.0-windows`）専用。Unity DLL ビルドでは `#if !USLP_UNITY` により全4ファイルがコンパイルから除外される。

### 14.2 クラス構成

| クラス / ファイル | 役割 |
| --- | --- |
| `PreciseWaitItem` (`src/PreciseWaitItem.cs`) | `IValueTaskSource` + `ObjectPool<T>` による待機アイテム。`ManualResetValueTaskSourceCore<bool>` でゼロアロケーション `ValueTask` を発行 |
| `TimerWheel` (`src/TimerWheel.cs`) | スロット数 8192 のタイマーホイール。素直な `long` 除算で O(1) スロット計算 |
| `SpinCoreEngine` (`src/SpinCoreEngine.cs`) | 専用 CPU コアに固定されたスピンスレッド。`NtSetTimerResolution(1)` でシステムタイマー分解能を最小化し、`TimerWheel.Advance()` をタイトループで呼び出す |
| `PreciseDelay` (`src/PreciseDelay.cs`) | 公開 API。≤5 ms はスピンパス、>5 ms は WaitableTimer HR パスに自動振り分け |

時刻源には `Stopwatch.GetTimestamp()` を使う。以前は `KUSER_SHARED_DATA` を直読みする
`NativeClock`（`src/NativeClock.cs`）が存在したが撤去済み。理由は第 4 章「時刻取得」を参照。

### 14.3 PreciseWaitItem のプール返却タイミング

`Complete()` / `CompleteAsCancelled()` の完了時点ではプールへ返却**しない**。返却は
`GetResult()`（＝呼び出し元が `await` を通じて結果を受け取った時点）で行う。

```csharp
public void GetResult(short token)
{
    try { _vtsc.GetResult(token); }
    finally { PreciseWaitItemPool.Return(this); }
}
```

**理由:** 完了時点（`Complete()`）で返却すると、呼び出し元がまだ `await` していない間に
別スレッドが同じインスタンスを `Rent()` して `Reset()` を呼ぶ可能性がある。`Reset()` は
`ManualResetValueTaskSourceCore<bool>.Reset()` を呼ぶため `_vtsc.Version` が変わり、
未 `await` の `ValueTask` が保持していた古いトークンと不一致になって壊れる。
返却を `GetResult()` まで遅らせることで、呼び出し元が確実に結果を受け取り終えてから
インスタンスが再利用される。

`Complete()` / `CompleteAsCancelled()` は `SpinLoop`（スピンスレッド単独）からのみ呼ぶ設計で、
`Interlocked` は使用しない。`IsInitialized` フラグで use-after-free を防止する。

### 14.4 TimerWheel の設計

#### スロット計算（素直な long 除算）

`_ticksPerSlot = Stopwatch.Frequency / 1_000_000`（1 µs あたりのティック数）を構築時に計算し、
スロット番号は `diff / _ticksPerSlot` の単純な `long` 除算で求める。

```text
diff = timestamp - _baseTimestamp   （_baseTimestamp は構築時に固定）
slot = diff / _ticksPerSlot
```

`diff` は `long` の範囲内でオーバーフローするまで **約 29,000 年**（QPC 周波数 ~10 MHz 基準）
であり実用上問題ない。

> **Magic Number 除算（`Math.BigMul`）を撤去した理由**
>
> 以前は逆数に相当するマジックマルチプライヤを `ComputeMagicNumbers()` で求め、
> `(diff × magicMultiplier) >> (magicShift - 64)` で除算を回避していた。しかしこの
> 乗数の計算式 `2^(64+log2(d)+1) / d` は `d = 10`（1µs あたり 10 ティック。
> `Stopwatch.Frequency = 10 MHz` は Windows で最も一般的な値であり、例外的な環境ではない）
> のとき `2.95 × 10^19` となり `ulong`（最大 `1.84 × 10^19`）を溢れ、下位 64 ビットだけが
> 残っていた。実効係数は `1/10 = 0.1` ではなく `0.0375` となり、1 スロットは 1 µs ではなく
> **約 2.67 µs** として動作していた。
>
> したがってホイールの実効範囲は狭まるのではなく逆に約 2.67 倍（4096 スロットで約 10.9 ms）に
> 広がっていた。実害は範囲不足による早期完了ではなく、**待機の粒度が 2.67 µs に量子化され、
> `PreciseDelay` が目標とする ±1〜3 µs が成立しない**ことである。
> 素直な `long` 除算は 1 回あたり数 ns で、スピンループ 1 反復（`Stopwatch.GetTimestamp()`
> 実測 ~20 ns を含む）に対して支配的でないため、正しさを優先して単純な除算に戻した。

#### コンストラクタでの範囲検証

`TimerWheel` のコンストラクタは扱う必要のある最大待機時間 `requiredSpanMicroseconds`（µs）を
引数に取る。実際に表現できる範囲は `Stopwatch.Frequency` に依存するため（`_ticksPerSlot` は
切り捨て演算のため、周波数が 1 MHz の倍数でない環境では 1 スロットが 1 µs 未満になりうる）、
構築時に `spanUs = SlotCount * _ticksPerSlot * 1_000_000 / Stopwatch.Frequency` を計算し、
`requiredSpanMicroseconds` に満たなければ `NotSupportedException` を投げる。
`Debug.Assert` は使わない（Release ビルドで消えるうえ、.NET では失敗時にプロセスが即死するため）。

`SpinCoreEngine.Initialize()` は `PreciseDelay.SpinPathMaxMilliseconds`（5 ms）を渡してこの
検証を行う。

#### 絶対スロット番号と Advance / Enqueue

`_currentSlot` は折り返しのない絶対スロット番号として保持する（マスク後の値だけでは
前後関係を判別できないため）。`Advance()` は現在時刻に対応するスロットまで 1 スロットずつ
進めて消化する。1 周以上遅れている場合（スピンスレッド起動直後やプリエンプト明けなど）は
全スロットを 1 回だけ掃いて追いつく。

`Enqueue()` で締切が既に現在スロット以前（過去）の場合は、キューに入れずその場で
`Complete()` / `CompleteAsCancelled()` を呼んで完了させる。「現在スロットへまとめて入れる」
方式は採らない。過去 deadline がバーストで届いた場合に 1 スロットへ集中し、
`MaxSlotCapacity`（1024）超過で `GrowSlot()` が例外を投げてスピンスレッドごとプロセスが
落ちるため。`Complete()` はスピンスレッド単独で呼ぶ規約だが、`Enqueue()` の呼び出し元は
`SpinLoop` のみなのでこの規約は保たれている。

締切がホイールの表現範囲（`SlotCount` スロット分）を超えている場合は、折り返して過去の
スロットに落ちて早期完了するのを避けるため、表現できる最遠のスロットへ丸める。
`SpinLoop` は `_incoming` の drain ループ中でも `Advance()` を呼んで `_currentSlot` を
最新に保つため、通常この分岐には到達しない。

#### Dispose

`TimerWheel.Dispose()` は `_disposed` フラグを立てるのみ。呼び出し元（`SpinCoreEngine`）は
スピンスレッドの `Thread.Join()` が成功したときだけこれを呼ぶ（詳細は 14.6 節）。

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

`SpinCoreEngine.Dispose()` は `_running = false` を立てたのち、スピンスレッドの
`Thread.Join(TimeSpan.FromSeconds(1))` が **成功したときだけ** `TimerWheel.Dispose()` を
呼ぶ。まだスピンスレッドが動いている状態で `_disposed` を立てた `TimerWheel` に触らせると、
次の `Enqueue()` が `ObjectDisposedException` を投げてスピンスレッドごとプロセスが落ちるため。
`Join` がタイムアウトした場合はホイールを解放せず GC に委ねる。

### 14.7 関連ファイル

| ファイル | 内容 |
| --- | --- |
| [src/PreciseWaitItem.cs](../src/PreciseWaitItem.cs) | IValueTaskSource + ObjectPool 待機アイテム |
| [src/TimerWheel.cs](../src/TimerWheel.cs) | O(1) タイマーホイール（8192 スロット、long 除算） |
| [src/SpinCoreEngine.cs](../src/SpinCoreEngine.cs) | 専用コアスピンスレッドエンジン |
| [src/PreciseDelay.cs](../src/PreciseDelay.cs) | 公開 API（Initialize / Shutdown / WaitAsync） |
| [tests/UsleepWin.Tests/PreciseDelayTests.cs](../tests/UsleepWin.Tests/PreciseDelayTests.cs) | `PreciseDelay` の結合テスト + `TimerWheel` 境界条件の直接テスト（`InternalsVisibleTo` 経由） |
| [tests/UsleepWin.UnityWindows.Tests](../tests/UsleepWin.UnityWindows.Tests) | Unity Windows バリアント（`USLP_UNITY` + `USLP_WINDOWS`）のスモークテスト。`DllImport` 版 P/Invoke と QPC 経路を実行時に踏ませる。CoreCLR 上での検証であり Mono / IL2CPP の検証ではない |
| [document/test_result.md](test_result.md) | テスト結果レポート（初回実施時点のスナップショット。最新のテスト件数はテストプロジェクトを参照） |

---

*本仕様書は `src/` 以下のソースコードを参照しています。仕様と実装に差異がある場合はソースコードを正とします。*
