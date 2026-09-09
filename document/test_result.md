# テスト結果レポート

**実施日**: 2026-09-09
**対象バージョン**: 0.2.1（`pack/usleep_win_cs.nupkg.csproj` の `<Version>` を情報源とする）
**対象プロジェクト**: `tests/UsleepWin.Tests`、`tests/UsleepWin.UnityWindows.Tests`（2 プロジェクトとも `.sln` 未包含のためパス指定で個別実行）
**テストフレームワーク**: xUnit 2.x
**実行環境**: Intel(R) Core(TM) Ultra 9 285K（24 コア / 24 論理プロセッサ）, Windows 11 Home 10.0.26200, 電源プラン未計測, `InitTimerResolution` は明示初期化なし（テスト実行時の既定状態）

## 実行コマンド

```powershell
dotnet test tests\UsleepWin.Tests\UsleepWin.Tests.csproj --configuration Release
dotnet test tests\UsleepWin.UnityWindows.Tests\UsleepWin.UnityWindows.Tests.csproj --configuration Release
```

テスト一覧は `dotnet test <csproj> --list-tests` で取得。

## 集計（プロジェクト別）

| プロジェクト | 検証対象バリアント | ランタイム | 件数 | 成功 | 失敗 | 所要時間 |
|---|---|---|---|---|---|---|
| `tests/UsleepWin.Tests` | NuGet（`USLP_GENERATOR` + `USLP_WINDOWS`、`LibraryImport` source generator 経路） | CoreCLR (net10.0-windows) | 58 | 58 | 0 | 432 ms |
| `tests/UsleepWin.UnityWindows.Tests` | Unity Windows（`USLP_UNITY` + `USLP_WINDOWS`、`DllImport` + QPC 経路を `src` から直接コンパイル） | CoreCLR (net10.0-windows) | 14 | 14 | 0 | 138 ms |
| `tests/UnityEditor.Tests`（`.\run_unity_tests.ps1`） | Unity Windows（上記と同じ DLL） | **Mono**（Unity Editor 上） | — | — | — | **今回未実行**（Unity Editor の起動が必要なため） |

合計（実行分）: **72 件、全成功、0 失敗**。

失敗は無し。フレーキーの再現も確認されていない（各プロジェクトとも 1 回の実行で全件成功）。

---

## `tests/UsleepWin.Tests`（58 件）

NuGet バリアント（`pack/usleep_win_cs.nupkg.csproj` を `ProjectReference`、`USLP_GENERATOR` 経路）を CoreCLR 上で検証する。

### `UsleepWinTests`（既存 UsleepWin API、20 件）

| テスト名 | 結果 | 概要 |
|---------|------|------|
| `NowSteadyMicroseconds_ReturnsIncreasingValues` | 成功 | 連続呼び出しで単調増加 |
| `NowSteadyMicroseconds_NonZero` | 成功 | タイムスタンプが 0 より大きい |
| `SleepMicroseconds_ActualElapsedIsAtLeastRequested(sleepUs: 500)` | 成功 | 実測 >= 要求値の 50% |
| `SleepMicroseconds_ActualElapsedIsAtLeastRequested(sleepUs: 1000)` | 成功 | 実測 >= 要求値の 50% |
| `SleepMicroseconds_ActualElapsedIsAtLeastRequested(sleepUs: 5000)` | 成功 | 実測 >= 要求値の 50% |
| `SleepMicroseconds_ZeroDoesNotThrow` | 成功 | 0µs 指定で例外なし |
| `SleepNanoseconds_ActualElapsedIsAtLeastRequested` | 成功 | 1ms（ns 指定）で実測 >= 要求値の 50% |
| `SleepUntilSteadyMicroseconds_FutureDeadline_Sleeps` | 成功 | 2ms 先のデッドラインまで待機し到達を確認 |
| `SleepUntilSteadyMicroseconds_PastDeadline_ReturnsImmediately` | 成功 | 過去デッドラインは 50ms 以内に即リターン |
| `SetProfile_DoesNotThrow` | 成功 | BALANCED / STRICT / LOW_POWER 全プロファイルで例外なし |
| `SetTailSpinMicroseconds_DoesNotThrow` | 成功 | 0 / 250 / 1000 / 上限値で例外なし（テスト後に既定値へ復元） |
| `SetTailSpinMicroseconds_AboveMaxThrows` | 成功 | 上限超過値と `uint.MaxValue` で `ArgumentOutOfRangeException` |
| `SetYieldPolicy_DoesNotThrow` | 成功 | NONE / SWITCH_THREAD / SLEEP0 / SLEEP1 全ポリシーで例外なし |
| `GetStats_ReturnsStruct` | 成功 | 統計構造体が取得でき `SpinRelax` / `WaitableTimerUses` にアクセス可能 |
| `ResetStats_DoesNotThrow` | 成功 | 統計リセットで例外なし |
| `GetStats_WithReset_ClearsCounters` | 成功 | `reset: true` で `SpinRelax` / `WaitableTimerUses` が 0 になる |
| `SetPowerMode_ValidMode_Succeeds(mode: DEFAULT)` | 成功 | `SetThreadInformation` 経由のモード設定が true を返す（終了時 DEFAULT へ復元） |
| `SetPowerMode_ValidMode_Succeeds(mode: PERF)` | 成功 | 同上 |
| `SetPowerMode_ValidMode_Succeeds(mode: ECO)` | 成功 | 同上 |
| `SetPowerMode_OutOfRange_ReturnsFalse` | 成功 | 未定義の enum 値（99）で false を返す |

### `InternalTimingTests`（internal API の境界・飽和検証、7 件）

`DeadlineFromNow` のオーバーフロー救済と `LOW_POWER` プロファイルの早期リターン防止を、公開 API 越しでは踏めない経路も含めて internal を直接叩いて検証する。

| テスト名 | 結果 | 概要 |
|---------|------|------|
| `DeadlineFromNow_HugeRequest_SaturatesInsteadOfWrapping` | 成功 | `ulong.MaxValue` 要求でラップアラウンドせず `ulong.MaxValue` に飽和 |
| `DeadlineFromNow_NearMaxRequest_DoesNotGoBackwards` | 成功 | `ulong.MaxValue - 1000` 要求でも deadline が現在時刻を下回らない |
| `DeadlineFromNow_NormalRequest_AddsRequestedAmount` | 成功 | 通常値では `now + 要求値` の範囲に収まる |
| `HasHighResolutionTimer_IsStableAcrossCalls` | 成功 | 8 回連続呼び出しで判定結果が変化しない |
| `HasHighResolutionTimer_TrueOnModernWindows` | 成功 | 実行環境で高分解能 WaitableTimer が利用可能（true） |
| `SleepMicroseconds_LowPowerProfile_NeverReturnsEarly(sleepUs: 2000)` | 成功 | LOW_POWER + tailSpin=0 で 5 回中いずれも要求値以上経過 |
| `SleepMicroseconds_LowPowerProfile_NeverReturnsEarly(sleepUs: 3500)` | 成功 | 同上 |

### `PreciseDelayLifecycleTests`（`[Collection("PreciseDelay")]`、5 件）

| テスト名 | 結果 | 概要 |
|---------|------|------|
| `WaitAsync_BeforeInitialize_ThrowsInvalidOperationException` | 成功 | Initialize 前の `WaitAsync` は `InvalidOperationException` |
| `Initialize_Core0_ThrowsArgumentException` | 成功 | コア 0 指定で `ArgumentException`、かつ失敗後も `IsInitialized == false` |
| `Initialize_Twice_ThrowsInvalidOperationException` | 成功 | 二重 `Initialize` は `InvalidOperationException` |
| `WaitAsync_AfterShutdown_ThrowsInvalidOperationException` | 成功 | `Shutdown` 後の `WaitAsync` は `InvalidOperationException` |
| `IsInitialized_ReflectsLifecycle` | 成功 | false → true（Initialize後）→ false（Shutdown後）の遷移 |

### `PreciseDelayWaitTests`（`[Collection("PreciseDelay")]` + `IClassFixture`、23 件）

| テスト名 | 結果 | 概要 |
|---------|------|------|
| `WaitAsync_ZeroDelay_CompletesImmediately` | 成功 | `TimeSpan.Zero` 指定で 50ms 以内に完了 |
| `WaitAsync_NegativeDelay_CompletesImmediately` | 成功 | 負値指定で 50ms 以内に完了 |
| `WaitAsync_SpinPath_ElapsedAtLeastRequested(delayUs: 200)` | 成功 | スピン経路（≤5ms）実測 >= 要求値の 50% |
| `WaitAsync_SpinPath_ElapsedAtLeastRequested(delayUs: 500)` | 成功 | 同上 |
| `WaitAsync_SpinPath_ElapsedAtLeastRequested(delayUs: 1000)` | 成功 | 同上 |
| `WaitAsync_SpinPath_ElapsedAtLeastRequested(delayUs: 2000)` | 成功 | 同上 |
| `WaitAsync_WaitableTimerPath_ElapsedAtLeastRequested(delayMs: 10)` | 成功 | WaitableTimer 経路（>5ms）実測 >= 要求値の 50% |
| `WaitAsync_WaitableTimerPath_ElapsedAtLeastRequested(delayMs: 20)` | 成功 | 同上 |
| `WaitAsync_WaitableTimerPath_ElapsedAtLeastRequested(delayMs: 50)` | 成功 | 同上 |
| `WaitAsync_WaitableTimerPath_NonCancellableToken_DoesNotHang` | 成功 | `CancellationToken.None` で 20ms 待機が 5 秒以内に完了（ネイティブ呼び出し失敗時の永久ハング回帰防止） |
| `WaitAsync_WaitableTimerPath_Cancelled_CarriesToken` | 成功 | WaitableTimer 経路のキャンセル例外に渡したトークンが載る |
| `WaitAsync_Cancelled_ThrowsOperationCanceledException` | 成功 | 事前キャンセル済みトークンで `OperationCanceledException` |
| `WaitAsync_WaitableTimerPath_PreCancelled_CarriesToken` | 成功 | 事前キャンセル済みで >5ms 待機時、カーネルオブジェクト確保前に渡したトークン付きで即キャンセル |
| `WaitAsync_SpinPath_PreCancelled_CarriesToken` | 成功 | 事前キャンセル済みで ≤5ms 待機時、渡したトークン付きでキャンセル（例外型を厳密一致で経路をピン留め） |
| `WaitAsync_SpinPath_PastDeadline_PreCancelled_CarriesToken` | 成功 | 締切が既に過去の要求でもキャンセル済みなら渡したトークン付きでキャンセル（`Enqueue()` 側の即時完了分岐の回帰防止） |
| `WaitAsync_SpinPath_CancelledDuringWait_DoesNotReturnEarly` | 成功 | スピン経路は待機中キャンセルしてもデッドライン到達まで早期に打ち切られない（下限のみ判定） |
| `WaitAsync_CancelledDuringWait_ThrowsOperationCanceledException` | 成功 | 待機中キャンセル（20ms 後）で `OperationCanceledException` |
| `WaitAsync_500us_AverageErrorWithin50us` | 成功 | 500µs × 100 回の平均誤差 ≤50µs |
| `WaitAsync_MultipleConcurrentCalls_AllComplete` | 成功 | 10 タスク並列で全て `RanToCompletion` |
| `WaitAsync_NearSpinPathUpperBound_DoesNotReturnEarly(targetUs: 4200)` | 成功 | スピン経路上限付近（ホイール折り返し境界）で中央値が早期完了・遅延過多いずれもしない |
| `WaitAsync_NearSpinPathUpperBound_DoesNotReturnEarly(targetUs: 4500)` | 成功 | 同上 |
| `WaitAsync_NearSpinPathUpperBound_DoesNotReturnEarly(targetUs: 5000)` | 成功 | 同上（スピン経路の上限ちょうど） |
| `WaitAsync_LargeBurstOfShortWaits_AllComplete` | 成功 | 50µs 待機 3000 タスクが 10 秒以内に全完了（取りこぼし検出） |

### `TimerWheelBoundaryTests`（`[Collection("PreciseDelay")]`、3 件）

`PreciseDelay` 越しでは踏めない `TimerWheel` の境界条件を直接検証する。

| テスト名 | 結果 | 概要 |
|---------|------|------|
| `Enqueue_ManyPastDeadlines_CompletesAllWithoutThrowing` | 成功 | 過去締切 3000 件（`MaxSlotCapacity`=1024 超）を投入しても `GrowSlot` の例外なく即完了 |
| `Enqueue_DeadlineWithinSpan_DoesNotCompleteBeforeAdvance` | 成功 | 範囲内の deadline は `Advance` 前に完了せず、締切超過後に完了 |
| `Constructor_RequiredSpanTooLarge_Throws` | 成功 | 表現不能な要求スパンで `NotSupportedException` |

---

## `tests/UsleepWin.UnityWindows.Tests`（14 件）

Unity Windows バリアント（`USLP_UNITY` + `USLP_WINDOWS`、`DllImport` + QPC 経路）を `src` から直接コンパイルし、CoreCLR 上でスモークテストする。`build_unity_windows.bat` の不具合（`%3B` がバッチ引数展開される問題）で長らくビルドが通っていなかった経緯があり、ビルド可否だけでなく実行時の正しさをここで担保する。**CoreCLR 上の検証であり、Unity の Mono / IL2CPP は検証していない。**

### `UnityWindowsSmokeTests`（14 件）

| テスト名 | 結果 | 概要 |
|---------|------|------|
| `NowSteadyMicroseconds_IsNonZeroAndMonotonic` | 成功 | QPC 直呼び経路が非ゼロかつ単調増加の値を返す |
| `NowSteadyMicroseconds_AdvancesAtRealTimeRate` | 成功 | 50ms Sleep 前後の差分が実測 Stopwatch と ±10% で一致（QPC 換算係数の破綻検出） |
| `NowSteadyMicroseconds_AbsoluteValueMatchesSystemUptime` | 成功 | 絶対値が `Environment.TickCount64` 基準のシステム稼働時間と 60 秒以内で一致（long 乗算オーバーフロー検出） |
| `SleepMicroseconds_WaitsAtLeastRequested(sleepUs: 500)` | 成功 | 実測 >= 要求値の 90%（緩い 50% 判定だとクロック換算 2 倍バグを見逃すため厳しめ） |
| `SleepMicroseconds_WaitsAtLeastRequested(sleepUs: 2000)` | 成功 | 同上 |
| `SleepMicroseconds_Zero_DoesNotThrow` | 成功 | 0µs 指定（`SwitchToThread` の DllImport 経路）で例外なし |
| `SleepMicroseconds_LongWait_UsesWaitableTimer` | 成功 | 3000µs（timerFirstUs 超）で `WaitableTimerUses` 統計が増加し、WaitableTimer 経路の DllImport 成功を確認 |
| `SleepMicroseconds_ShortWait_UsesSpin` | 成功 | 100µs（preferSpinBelow 以下）で `SpinRelax` > 0 かつ `WaitableTimerUses == 0`、スピン経路のみを通ったことを確認 |
| `SetPowerMode_ValidMode_Succeeds(mode: DEFAULT)` | 成功 | `SetThreadInformation` の DllImport 経路が true を返し DEFAULT へ復元できる |
| `SetPowerMode_ValidMode_Succeeds(mode: PERF)` | 成功 | 同上 |
| `SetPowerMode_ValidMode_Succeeds(mode: ECO)` | 成功 | 同上 |
| `TimerResolution_InitAndShutdown_Succeeds` | 成功 | `timeBeginPeriod(1)`（winmm.dll DllImport）成功、`Shutdown` で復元 |
| `InitTimerResolution_Zero_ReturnsFalse` | 成功 | 0 指定で false を返す |
| `SetProfile_ChangesWaitBehaviourWithoutThrowing` | 成功 | STRICT / LOW_POWER 切り替えで例外なく待機できる |

---

## `tests/UnityEditor.Tests`（未実行）

Unity Editor 上（**Mono** ランタイム）で同一 DLL を実行するテストプロジェクトが `tests/UnityEditor.Tests` に存在し、`.\run_unity_tests.ps1` で実行できる。CoreCLR と Mono は `Stopwatch.GetTimestamp()` の基準（CoreCLR: ブート基準の QPC そのもの、Mono: プロセス基準）などに実差があるため独立した検証対象だが、**Unity Editor の起動が必要なため今回のレポートでは未実行**。件数・結果ともに未計測。

---

## 過去のバグ修正・アーキテクチャ変更の記録（アーカイブ）

以下は 2026-03-11 の初回実施時に発見・修正されたバグと、その後撤回されたアーキテクチャ変更の記録。現状のテストには影響しないが経緯として残す。

### バグ1: `PreciseDelay.Initialize()` の不完全な例外処理（修正済み）

**症状**: `Initialize(cpuCore: 0)` で `ArgumentException` を投げた後、`IsInitialized` が `true` のままになる。
**原因**: `_engine = new SpinCoreEngine()` で代入した後に `engine.Initialize()` が例外を投げていた。
**修正**: 例外が出ない場合のみ `_engine` に代入するよう変更（[src/PreciseDelay.cs](../src/PreciseDelay.cs)）。現在は `PreciseDelayLifecycleTests.Initialize_Core0_ThrowsArgumentException` が回帰を防止している。

### バグ2: `TimerWheel.ResetBase()` によるスロットマッピング破壊（修正済み）

**症状**: `ResetBase()` 呼び出し後、既存のキューアイテムが正しい時刻に完了しない（最大4ms超の遅延）。
**原因**: `_baseTimestamp` を更新・`_currentSlot = 0` にリセットすると、既にキュー済みのアイテムのスロットインデックス（旧 base 基準）が無効になる。
**修正**: `_baseTimestamp` を構築時1回だけ設定し、`ResetBase()` を削除（[src/TimerWheel.cs](../src/TimerWheel.cs)）。

### 撤回されたアーキテクチャ変更: `NativeClock` 統合（KUSER_SHARED_DATA 直読み）

2026-03-11 時点で `InternalTiming.NowUs()`（NuGet ビルド）を `NativeClock.GetTimestamp()`（KUSER_SHARED_DATA 直読み）に統一する変更が行われたが、**2026-08-27（コミット `ad6a1d9`）以降の実測により前提が誤っていたことが判明し撤回された**。読んでいた QpcBias が単調増加カウンタでなく常にフォールバックしていたため。`NativeClock` は削除済みで、`InternalTiming.NowUs()`（NuGet ビルド）は `Stopwatch.GetTimestamp()` に統一されている。**復活させないこと**（`CLAUDE.md` 参照）。詳細な経緯は [document/specsheet.md](specsheet.md) を参照。

### テストプロジェクトの 2 分割の経緯

`build_unity_windows.bat` の不具合（バッチ内の `%3B` がバッチ引数として展開され、定数が `USLP_UNITYBUSLP_WINDOWS` に化けていた）により、Unity Windows バリアントは長らくビルドすら通っておらず、`DllImport` 版の P/Invoke と QPC 経路は一度も実行されたことがなかった。そのため `tests/UsleepWin.UnityWindows.Tests` を新設し、`USLP_UNITY` + `USLP_WINDOWS` で `src` を直接コンパイルするスモークテストを追加した（現在 14 件）。

---

## ベンチマーク実測値（計測日 2026-03-11 時点、未再計測）

過去の実施時点でベンチマークの実測値セクションは本ファイルに存在しなかった。試行回数・中央値・p99・最大値などの定量的な精度計測は、本レポート作成時点（2026-09-09）でも別途 `timing-benchmark` によるセッションでの実測が必要であり、**未計測**。数値を推定で埋めることはしない。
