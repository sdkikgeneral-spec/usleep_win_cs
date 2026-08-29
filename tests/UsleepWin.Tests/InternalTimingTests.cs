// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using Usleep.Win;
using Xunit;

namespace UsleepWin.Tests;

/// <summary>
/// InternalTiming のオーバーフロー・飽和・高分解能タイマー可用性判定のテスト。
/// これらは公開 API 越しには到達させられない（要求値を飽和させるような待機は
/// 実時間で完了しない）ため internal を直接叩く。
/// </summary>
public class InternalTimingTests
{
    // ── DeadlineFromNow ─────────────────────────────────────────────
    //
    // 以前は now + usec を素で足していたため、大きな usec でラップアラウンドし
    // 「deadline はもう過ぎている」と誤判定して即座に返っていた。

    [Fact]
    public void DeadlineFromNow_HugeRequest_SaturatesInsteadOfWrapping()
    {
        Assert.Equal(ulong.MaxValue, InternalTiming.DeadlineFromNow(ulong.MaxValue));
    }

    [Fact]
    public void DeadlineFromNow_NearMaxRequest_DoesNotGoBackwards()
    {
        ulong now = InternalTiming.NowUs();
        ulong deadline = InternalTiming.DeadlineFromNow(ulong.MaxValue - 1000UL);
        Assert.True(deadline >= now, $"deadline({deadline}) が現在時刻({now}) より前になっている");
    }

    [Fact]
    public void DeadlineFromNow_NormalRequest_AddsRequestedAmount()
    {
        ulong before = InternalTiming.NowUs();
        ulong deadline = InternalTiming.DeadlineFromNow(10_000UL);
        ulong after = InternalTiming.NowUs();

        Assert.True(deadline >= before + 10_000UL);
        Assert.True(deadline <= after + 10_000UL);
    }

    // ── HasHighResolutionTimer ──────────────────────────────────────

    [Fact]
    public void HasHighResolutionTimer_IsStableAcrossCalls()
    {
        // プロセス全体で一度だけ確定させる契約。呼ぶたびに結果が変わってはならない。
        bool first = InternalTiming.HasHighResolutionTimer();
        for (int i = 0; i < 8; i++)
            Assert.Equal(first, InternalTiming.HasHighResolutionTimer());
    }

    [Fact]
    public void HasHighResolutionTimer_TrueOnModernWindows()
    {
        // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION は Windows 10 1803 以降。
        // テストが走る環境ではこれが取れないと精度の前提が崩れる。
        Assert.True(InternalTiming.HasHighResolutionTimer(),
            "高分解能 WaitableTimer が利用できない環境で実行されている");
    }

    // ── LOW_POWER の早期リターン ────────────────────────────────────
    //
    // LOW_POWER は tailSpin を使わないため、タイマー経路が使えず Sleep(ms) へ
    // 劣化したときに「ms へ切り捨てたぶん最大 999µs 早く返る」契約違反があった。
    // 高分解能タイマーのある環境では劣化経路に入らないので、これは劣化経路が
    // 生きている環境向けの回帰ガードとして残す。

    [Theory]
    [InlineData(2_000UL)]
    [InlineData(3_500UL)]
    public void SleepMicroseconds_LowPowerProfile_NeverReturnsEarly(ulong sleepUs)
    {
        var savedProfile = UsleepProfile.BALANCED;
        try
        {
            Usleep.Win.UsleepWin.SetProfile(UsleepProfile.LOW_POWER);
            Usleep.Win.UsleepWin.SetTailSpinMicroseconds(0);

            for (int i = 0; i < 5; i++)
            {
                ulong start = Usleep.Win.UsleepWin.NowSteadyMicroseconds();
                Usleep.Win.UsleepWin.SleepMicroseconds(sleepUs);
                ulong elapsed = Usleep.Win.UsleepWin.NowSteadyMicroseconds() - start;

                Assert.True(elapsed >= sleepUs,
                    $"要求 {sleepUs}µs に対して {elapsed}µs で復帰した（早期リターン）");
            }
        }
        finally
        {
            Usleep.Win.UsleepWin.SetProfile(savedProfile);
            Usleep.Win.UsleepWin.SetTailSpinMicroseconds(250);
        }
    }
}
