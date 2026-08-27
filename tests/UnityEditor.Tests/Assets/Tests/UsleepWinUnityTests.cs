// SPDX-License-Identifier: MIT
//
// Unity Editor（Mono）上で usleep_win_cs.unity.dll を実際に動かす EditMode テスト。
// CoreCLR 上の DllImport 検証では確かめられない Mono のマーシャリングを見る。

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Usleep.Win;

public class UsleepWinUnityTests
{
    // Environment.TickCount64 は netstandard2.1 に無いので直接呼ぶ。
    // ブートからの経過ミリ秒。TickCount(int) と違い 49.7 日でラップしない。
    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    private static long SystemUptimeMicroseconds()
    {
        return (long)GetTickCount64() * 1000L;
    }

    [Test]
    public void NowSteadyMicroseconds_IsNonZeroAndMonotonic()
    {
        ulong t1 = UsleepWin.NowSteadyMicroseconds();
        Assert.Greater(t1, 0UL, "QPC 経路が時刻を返していない");

        ulong t2 = UsleepWin.NowSteadyMicroseconds();
        Assert.GreaterOrEqual(t2, t1, "単調増加でない");
    }

    // 基準には Environment.TickCount64（ブートからの経過 ms）を使う。
    // Stopwatch と比べてはいけない: CoreCLR の Stopwatch.GetTimestamp() は QPC
    // そのものを返すのでブート基準だが、Unity の Mono ではプロセス基準であり
    // 一致しない（実測: QPC 95,936 秒 vs Stopwatch 4.9 秒）。
    [Test]
    public void NowSteadyMicroseconds_AbsoluteValueMatchesSystemUptime()
    {
        ulong reported = UsleepWin.NowSteadyMicroseconds();
        long  uptimeUs = SystemUptimeMicroseconds();
        long  diff     = Math.Abs((long)reported - uptimeUs);

        Assert.Less(diff, 60_000_000L,
            string.Format("絶対値がシステム稼働時間と乖離: {0} vs {1}（差 {2}us）",
                reported, uptimeUs, diff));
    }

    // Mono と CoreCLR で Stopwatch の基準が違うこと自体を記録しておく。
    // QPC が失敗して Stopwatch フォールバックへ落ちると、NowSteadyMicroseconds の
    // 基準がブートからプロセスへ変わり、値が大きく後退する（モノトニック性は
    // 保たれるが SleepUntilSteadyMicroseconds の締切計算が壊れる）。
    // _qpcFreq はプロセス起動時に一度決まるため実運用で切り替わることはない。
    [Test]
    public void MonoStopwatch_HasDifferentEpochThanQpc_Documented()
    {
        long qpcBasedUs   = (long)UsleepWin.NowSteadyMicroseconds();
        long swBasedUs    = Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency;
        long uptimeUs     = SystemUptimeMicroseconds();

        UnityEngine.Debug.Log(string.Format(
            "NowSteadyMicroseconds(QPC)={0}us  Stopwatch={1}us  TickCount64={2}us",
            qpcBasedUs, swBasedUs, uptimeUs));

        // QPC 経路はブート基準であること
        Assert.Less(Math.Abs(qpcBasedUs - uptimeUs), 60_000_000L,
            "QPC 経路がブート基準でない");
    }

    [Test]
    public void NowSteadyMicroseconds_AdvancesAtRealTimeRate()
    {
        ulong before = UsleepWin.NowSteadyMicroseconds();
        var   sw     = Stopwatch.StartNew();
        System.Threading.Thread.Sleep(50);
        sw.Stop();
        ulong after = UsleepWin.NowSteadyMicroseconds();

        long reportedUs = (long)(after - before);
        long actualUs   = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;

        Assert.Greater(reportedUs, actualUs * 9 / 10, "QPC 換算レートが遅すぎる");
        Assert.Less(reportedUs, actualUs * 11 / 10, "QPC 換算レートが速すぎる");
    }

    [TestCase(500UL)]
    [TestCase(2000UL)]
    public void SleepMicroseconds_WaitsAtLeastRequested(ulong sleepUs)
    {
        var sw = Stopwatch.StartNew();
        UsleepWin.SleepMicroseconds(sleepUs);
        sw.Stop();

        long elapsedUs = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
        Assert.GreaterOrEqual(elapsedUs, (long)sleepUs * 9 / 10,
            string.Format("要求 {0}us に対し {1}us で戻った", sleepUs, elapsedUs));
    }

    [Test]
    public void SleepMicroseconds_Zero_DoesNotThrow()
    {
        // SwitchToThread() の DllImport を踏む
        Assert.DoesNotThrow(() => UsleepWin.SleepMicroseconds(0));
    }

    [Test]
    public void SleepMicroseconds_LongWait_UsesWaitableTimer()
    {
        UsleepWin.ResetStats();
        UsleepWin.SleepMicroseconds(3000);

        UsleepStats stats = UsleepWin.GetStats(false);
        Assert.Greater(stats.WaitableTimerUses, 0UL,
            "WaitableTimer 経路を踏んでいない（Mono で DllImport が失敗している可能性）");
    }

    [Test]
    public void SleepMicroseconds_ShortWait_UsesSpin()
    {
        UsleepWin.SetProfile(UsleepProfile.BALANCED);
        UsleepWin.ResetStats();
        UsleepWin.SleepMicroseconds(100);

        UsleepStats stats = UsleepWin.GetStats(false);
        Assert.Greater(stats.SpinRelax, 0UL, "スピン経路を踏んでいない");
        Assert.AreEqual(0UL, stats.WaitableTimerUses, "タイマー経路を踏んでいる");
    }

    // SetThreadInformation は構造体を ref で渡す。Mono のマーシャリングが
    // CoreCLR と食い違うならここで露見する。
    [TestCase(UsleepPowerMode.DEFAULT)]
    [TestCase(UsleepPowerMode.PERF)]
    [TestCase(UsleepPowerMode.ECO)]
    public void SetPowerMode_ValidMode_Succeeds(UsleepPowerMode mode)
    {
        try
        {
            Assert.IsTrue(UsleepWin.SetPowerMode(mode),
                string.Format("SetPowerMode({0}) が false を返した", mode));
        }
        finally
        {
            UsleepWin.SetPowerMode(UsleepPowerMode.DEFAULT);
        }
    }

    [Test]
    public void SetPowerMode_OutOfRange_ReturnsFalse()
    {
        Assert.IsFalse(UsleepWin.SetPowerMode((UsleepPowerMode)99));
    }

    // winmm.dll の DllImport
    [Test]
    public void TimerResolution_InitAndShutdown_Succeeds()
    {
        try
        {
            Assert.IsTrue(UsleepWin.InitTimerResolution(1), "timeBeginPeriod(1) が失敗した");
        }
        finally
        {
            UsleepWin.ShutdownTimerResolution();
        }
    }

    [Test]
    public void SetProfile_AllProfiles_DoNotThrow()
    {
        try
        {
            UsleepWin.SetProfile(UsleepProfile.STRICT);
            UsleepWin.SleepMicroseconds(500);
            UsleepWin.SetProfile(UsleepProfile.LOW_POWER);
            UsleepWin.SleepMicroseconds(500);
        }
        finally
        {
            UsleepWin.SetProfile(UsleepProfile.BALANCED);
        }
    }

    // PreciseDelay 系が USLP_UNITY で除外されていることの確認。
    // 型が残っていると Microsoft.Extensions.ObjectPool が無い Unity で
    // 実行時に TypeLoadException になる。
    [Test]
    public void PreciseDelayTypes_AreExcludedFromUnityBuild()
    {
        var asm = typeof(UsleepWin).Assembly;
        Assert.IsNull(asm.GetType("Usleep.Win.PreciseDelay"),
            "PreciseDelay が Unity ビルドに含まれている");
        Assert.IsNull(asm.GetType("Usleep.Win.TimerWheel"),
            "TimerWheel が Unity ビルドに含まれている");
        Assert.IsNull(asm.GetType("Usleep.Win.SpinCoreEngine"),
            "SpinCoreEngine が Unity ビルドに含まれている");
    }
}
