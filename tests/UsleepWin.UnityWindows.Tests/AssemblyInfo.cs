// SPDX-License-Identifier: MIT

using Xunit;

// このアセンブリのテストは直列実行する。
//
// 現状はテストクラスが 1 個だけなので xUnit も直列に走らせるが、2 つ目の
// クラスを足した瞬間に並列化され、以下が競合する:
//   - `_timerResolutionMs`（プロセスグローバル。片方が InitTimerResolution(1) 中に
//     他方が ShutdownTimerResolution() を呼ぶと timeEndPeriod が先に走る）
//   - `_profile` / `_tailSpinUs` / 統計カウンタ（[ThreadStatic]。xUnit は
//     テストごとにスレッドを固定しないため、設定と計測が別スレッドに割れうる）
[assembly: CollectionBehavior(DisableTestParallelization = true)]
