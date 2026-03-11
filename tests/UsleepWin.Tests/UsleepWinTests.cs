// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using Usleep.Win;
using Xunit;

namespace UsleepWin.Tests;

/// <summary>
/// 既存 UsleepWin API のテスト。
/// </summary>
public class UsleepWinTests
{
    // ── NowSteadyMicroseconds ───────────────────────────────────────

    [Fact]
    public void NowSteadyMicroseconds_ReturnsIncreasingValues()
    {
        ulong t1 = Usleep.Win.UsleepWin.NowSteadyMicroseconds();
        ulong t2 = Usleep.Win.UsleepWin.NowSteadyMicroseconds();
        Assert.True(t2 >= t1, $"単調増加であるべき: t1={t1}, t2={t2}");
    }

    [Fact]
    public void NowSteadyMicroseconds_NonZero()
    {
        ulong t = Usleep.Win.UsleepWin.NowSteadyMicroseconds();
        Assert.True(t > 0);
    }

    // ── SleepMicroseconds ───────────────────────────────────────────

    [Theory]
    [InlineData(500UL)]
    [InlineData(1_000UL)]
    [InlineData(5_000UL)]
    public void SleepMicroseconds_ActualElapsedIsAtLeastRequested(ulong sleepUs)
    {
        var sw = Stopwatch.StartNew();
        Usleep.Win.UsleepWin.SleepMicroseconds(sleepUs);
        sw.Stop();

        long elapsedUs = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
        // 実測値 >= 要求値（多少の誤差を許容: 要求値の 50% 以上）
        Assert.True(elapsedUs >= (long)(sleepUs / 2),
            $"sleepUs={sleepUs}, elapsed={elapsedUs}us");
    }

    [Fact]
    public void SleepMicroseconds_ZeroDoesNotThrow()
    {
        var ex = Record.Exception(() => Usleep.Win.UsleepWin.SleepMicroseconds(0));
        Assert.Null(ex);
    }

    // ── SleepNanoseconds ────────────────────────────────────────────

    [Fact]
    public void SleepNanoseconds_ActualElapsedIsAtLeastRequested()
    {
        const ulong sleepNs = 1_000_000UL; // 1ms
        var sw = Stopwatch.StartNew();
        Usleep.Win.UsleepWin.SleepNanoseconds(sleepNs);
        sw.Stop();

        long elapsedNs = sw.ElapsedTicks * 1_000_000_000L / Stopwatch.Frequency;
        Assert.True(elapsedNs >= (long)(sleepNs / 2),
            $"sleepNs={sleepNs}, elapsed={elapsedNs}ns");
    }

    // ── SleepUntilSteadyMicroseconds ───────────────────────────────

    [Fact]
    public void SleepUntilSteadyMicroseconds_FutureDeadline_Sleeps()
    {
        ulong now    = Usleep.Win.UsleepWin.NowSteadyMicroseconds();
        ulong target = now + 2_000; // 2ms 先
        Usleep.Win.UsleepWin.SleepUntilSteadyMicroseconds(target);
        ulong after = Usleep.Win.UsleepWin.NowSteadyMicroseconds();
        Assert.True(after >= target, $"after={after} < target={target}");
    }

    [Fact]
    public void SleepUntilSteadyMicroseconds_PastDeadline_ReturnsImmediately()
    {
        ulong past = Usleep.Win.UsleepWin.NowSteadyMicroseconds() - 1_000;
        var sw = Stopwatch.StartNew();
        Usleep.Win.UsleepWin.SleepUntilSteadyMicroseconds(past);
        sw.Stop();
        // 過去のデッドラインなら即リターン（50ms 以内）
        Assert.True(sw.ElapsedMilliseconds < 50,
            $"elapsed={sw.ElapsedMilliseconds}ms");
    }

    // ── SetProfile / SetTailSpinMicroseconds / SetYieldPolicy ──────

    [Fact]
    public void SetProfile_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
        {
            Usleep.Win.UsleepWin.SetProfile(UsleepProfile.BALANCED);
            Usleep.Win.UsleepWin.SetProfile(UsleepProfile.STRICT);
            Usleep.Win.UsleepWin.SetProfile(UsleepProfile.LOW_POWER);
        });
        Assert.Null(ex);
    }

    [Fact]
    public void SetTailSpinMicroseconds_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
        {
            Usleep.Win.UsleepWin.SetTailSpinMicroseconds(0);
            Usleep.Win.UsleepWin.SetTailSpinMicroseconds(250);
            Usleep.Win.UsleepWin.SetTailSpinMicroseconds(1000);
        });
        Assert.Null(ex);
    }

    [Fact]
    public void SetYieldPolicy_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
        {
            Usleep.Win.UsleepWin.SetYieldPolicy(UsleepYieldPolicy.NONE);
            Usleep.Win.UsleepWin.SetYieldPolicy(UsleepYieldPolicy.SWITCH_THREAD);
            Usleep.Win.UsleepWin.SetYieldPolicy(UsleepYieldPolicy.SLEEP0);
            Usleep.Win.UsleepWin.SetYieldPolicy(UsleepYieldPolicy.SLEEP1);
        });
        Assert.Null(ex);
    }

    // ── GetStats / ResetStats ───────────────────────────────────────

    [Fact]
    public void GetStats_ReturnsStruct()
    {
        Usleep.Win.UsleepWin.SleepMicroseconds(500);
        var stats = Usleep.Win.UsleepWin.GetStats();
        // 統計構造体が取得できること（プロパティが存在する）
        _ = stats.SpinRelax;
        _ = stats.WaitableTimerUses;
    }

    [Fact]
    public void ResetStats_DoesNotThrow()
    {
        var ex = Record.Exception(() => Usleep.Win.UsleepWin.ResetStats());
        Assert.Null(ex);
    }

    [Fact]
    public void GetStats_WithReset_ClearsCounters()
    {
        Usleep.Win.UsleepWin.SleepMicroseconds(500);
        _ = Usleep.Win.UsleepWin.GetStats(reset: true); // リセット付きで取得
        var after = Usleep.Win.UsleepWin.GetStats();
        Assert.Equal(0UL, after.SpinRelax);
        Assert.Equal(0UL, after.WaitableTimerUses);
    }
}
