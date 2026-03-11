# テスト結果レポート

**初回実施日**: 2026-03-11
**直近再実施日**: 2026-03-11（NativeClock 統合・P/Invoke 重複排除後）
**対象プロジェクト**: `tests/UsleepWin.Tests/UsleepWin.Tests.csproj`
**ターゲットフレームワーク**: `net10.0-windows`
**テストフレームワーク**: xUnit 2.x

## 集計

| 項目 | 数 |
|------|----|
| テスト合計 | 33 |
| 成功 | 33 |
| 失敗 | 0 |
| 合計時間 | 0.221 秒 |

---

## UsleepWin 既存 API テスト（15件）

| テスト名 | 結果 | 概要 |
|---------|------|------|
| `NowSteadyMicroseconds_NonZero` | 成功 | タイムスタンプが0より大きい |
| `NowSteadyMicroseconds_ReturnsIncreasingValues` | 成功 | 連続呼び出しで単調増加 |
| `SleepMicroseconds_ActualElapsedIsAtLeastRequested(500μs)` | 成功 | 実測 >= 要求値の50% |
| `SleepMicroseconds_ActualElapsedIsAtLeastRequested(1000μs)` | 成功 | 実測 >= 要求値の50% |
| `SleepMicroseconds_ActualElapsedIsAtLeastRequested(5000μs)` | 成功 | 実測 >= 要求値の50% |
| `SleepMicroseconds_ZeroDoesNotThrow` | 成功 | 0μs指定で例外なし |
| `SleepNanoseconds_ActualElapsedIsAtLeastRequested` | 成功 | 1ms(ns指定)の実測確認 |
| `SleepUntilSteadyMicroseconds_FutureDeadline_Sleeps` | 成功 | 将来デッドラインまで待機 |
| `SleepUntilSteadyMicroseconds_PastDeadline_ReturnsImmediately` | 成功 | 過去デッドラインは即リターン |
| `SetProfile_DoesNotThrow` | 成功 | 全プロファイルで例外なし |
| `SetTailSpinMicroseconds_DoesNotThrow` | 成功 | 各値で例外なし |
| `SetYieldPolicy_DoesNotThrow` | 成功 | 全ポリシーで例外なし |
| `GetStats_ReturnsStruct` | 成功 | 統計構造体を取得できる |
| `ResetStats_DoesNotThrow` | 成功 | 統計リセットで例外なし |
| `GetStats_WithReset_ClearsCounters` | 成功 | reset=true で全カウンタが0になる |

---

## PreciseDelay 新 API テスト（18件）

### ライフサイクル（5件）

| テスト名 | 結果 | 概要 |
|---------|------|------|
| `WaitAsync_BeforeInitialize_ThrowsInvalidOperationException` | 成功 | Initialize前はIOE |
| `Initialize_Core0_ThrowsArgumentException` | 成功 | コア0指定でAE、IsInitialized=false |
| `Initialize_Twice_ThrowsInvalidOperationException` | 成功 | 二重InitializeはIOE |
| `WaitAsync_AfterShutdown_ThrowsInvalidOperationException` | 成功 | Shutdown後はIOE |
| `IsInitialized_ReflectsLifecycle` | 成功 | false→true→false の遷移 |

### 動作・精度（13件）

| テスト名 | 結果 | 概要 |
|---------|------|------|
| `WaitAsync_ZeroDelay_CompletesImmediately` | 成功 | 0ms指定で即完了（<50ms） |
| `WaitAsync_NegativeDelay_CompletesImmediately` | 成功 | 負値指定で即完了（<50ms） |
| `WaitAsync_SpinPath_ElapsedAtLeastRequested(200μs)` | 成功 | スピンパス実測 >= 100μs |
| `WaitAsync_SpinPath_ElapsedAtLeastRequested(500μs)` | 成功 | スピンパス実測 >= 250μs |
| `WaitAsync_SpinPath_ElapsedAtLeastRequested(1000μs)` | 成功 | スピンパス実測 >= 500μs |
| `WaitAsync_SpinPath_ElapsedAtLeastRequested(2000μs)` | 成功 | スピンパス実測 >= 1000μs |
| `WaitAsync_WaitableTimerPath_ElapsedAtLeastRequested(10ms)` | 成功 | WaitableTimerパス実測 >= 5ms |
| `WaitAsync_WaitableTimerPath_ElapsedAtLeastRequested(20ms)` | 成功 | WaitableTimerパス実測 >= 10ms |
| `WaitAsync_WaitableTimerPath_ElapsedAtLeastRequested(50ms)` | 成功 | WaitableTimerパス実測 >= 25ms |
| `WaitAsync_Cancelled_ThrowsOperationCanceledException` | 成功 | 事前キャンセル済みトークンでOCE |
| `WaitAsync_CancelledDuringWait_ThrowsOperationCanceledException` | 成功 | 待機中キャンセル(20ms後)でOCE |
| `WaitAsync_500us_AverageErrorWithin50us` | 成功 | 500μs×100回の平均誤差 ≤50μs |
| `WaitAsync_MultipleConcurrentCalls_AllComplete` | 成功 | 10タスク並列で全て正常完了 |

---

## テスト中に発見・修正したバグ

### バグ1: `PreciseDelay.Initialize()` の不完全な例外処理

**症状**: `Initialize(cpuCore: 0)` で `ArgumentException` を投げた後、`IsInitialized` が `true` のままになる。
**原因**: `_engine = new SpinCoreEngine()` で代入した後に `engine.Initialize()` が例外を投げていた。
**修正**: 例外が出ない場合のみ `_engine` に代入するよう変更（[src/PreciseDelay.cs](../src/PreciseDelay.cs)）。

```csharp
// Before
_engine = new SpinCoreEngine();
_engine.Initialize(dedicatedCpuCore);

// After
var engine = new SpinCoreEngine();
engine.Initialize(dedicatedCpuCore); // 例外が出た場合は _engine に代入しない
_engine = engine;
```

### バグ2: `TimerWheel.ResetBase()` によるスロットマッピング破壊

**症状**: `ResetBase()` 呼び出し後、既存のキューアイテムが正しい時刻に完了しない（最大4ms超の遅延）。
**原因**: `_baseTimestamp` を更新・`_currentSlot = 0` にリセットすると、既にキュー済みのアイテムのスロットインデックス（旧 base 基準）が無効になる。
**修正**: `_baseTimestamp` を構築時1回だけ設定し、`ResetBase()` を削除（[src/TimerWheel.cs](../src/TimerWheel.cs)）。
`diff = timestamp - _baseTimestamp` は int64 の範囲内で約29,000年分扱えるためオーバーフローは実用上問題なし。

---

## アーキテクチャ改善（NativeClock 統合・P/Invoke 整理）

2026-03-11 の再実施時に以下の内部改善を施し、全 33 テストの通過を確認した。

### 改善1: `InternalTiming.NowUs()` を NativeClock に統一（NuGet ビルド）

**変更前（USLP_GENERATOR）**: `QueryPerformanceCounter()` P/Invoke（~500 ns オーバーヘッド）
**変更後（USLP_GENERATOR）**: `NativeClock.GetTimestamp()`（KUSER_SHARED_DATA 直読み、~1 ns）
`NativeClock` は非対応環境で内部的に `Stopwatch.GetTimestamp()` へフォールバックするため後退互換性あり。
Unity ビルド（`USLP_WINDOWS`）は従来通り QPC を使用。

変更ファイル: [src/InternalTiming.cs](../src/InternalTiming.cs)

### 改善2: P/Invoke 宣言の重複排除

**変更前**: `PreciseDelay.cs` 内に `[DllImport]` 宣言が 2 つ・定数が 2 つ独立して存在していた。
**変更後**: `NativeMethods.Partial.cs` の `USLP_GENERATOR` ブロックに `SafeWaitHandle` 版 2 宣言を追加し、`PreciseDelay.cs` から重複コードを削除。

| 追加宣言 | エントリポイント |
| --- | --- |
| `CreateWaitableTimerExSafe(...)` → `SafeWaitHandle` | `CreateWaitableTimerExW` |
| `SetWaitableTimerSafe(SafeWaitHandle, ref long, ...)` | `SetWaitableTimer` |

変更ファイル: [src/Interop/NativeMethods.Partial.cs](../src/Interop/NativeMethods.Partial.cs), [src/PreciseDelay.cs](../src/PreciseDelay.cs)

---

## テストファイル

| ファイル | 内容 |
|---------|------|
| [tests/UsleepWin.Tests/UsleepWinTests.cs](../tests/UsleepWin.Tests/UsleepWinTests.cs) | 既存 UsleepWin API テスト |
| [tests/UsleepWin.Tests/PreciseDelayTests.cs](../tests/UsleepWin.Tests/PreciseDelayTests.cs) | PreciseDelay 新 API テスト |
| [tests/UsleepWin.Tests/TestCollections.cs](../tests/UsleepWin.Tests/TestCollections.cs) | xUnit コレクション定義（直列化） |
| [tests/UsleepWin.Tests/UsleepWin.Tests.csproj](../tests/UsleepWin.Tests/UsleepWin.Tests.csproj) | テストプロジェクト定義 |
