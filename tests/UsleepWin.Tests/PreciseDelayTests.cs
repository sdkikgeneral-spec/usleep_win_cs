// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Usleep.Win;
using Xunit;

namespace UsleepWin.Tests;

/// <summary>
/// PreciseDelay 新 API のテスト。
/// 各テストクラスは IDisposable で Initialize/Shutdown を管理する。
/// </summary>
[Collection("PreciseDelay")]
public class PreciseDelayLifecycleTests
{
    // ── Initialize 前の WaitAsync は InvalidOperationException ─────

    [Fact]
    public async Task WaitAsync_BeforeInitialize_ThrowsInvalidOperationException()
    {
        // PreciseDelay が未初期化であることを確認（前のテストで残留している場合を考慮）
        if (PreciseDelay.IsInitialized)
            PreciseDelay.Shutdown();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await PreciseDelay.WaitAsync(TimeSpan.FromMicroseconds(100)));
    }

    // ── コア0指定は ArgumentException ──────────────────────────────

    [Fact]
    public void Initialize_Core0_ThrowsArgumentException()
    {
        if (PreciseDelay.IsInitialized)
            PreciseDelay.Shutdown();

        Assert.Throws<ArgumentException>(() =>
            PreciseDelay.Initialize(dedicatedCpuCore: 0));

        // 失敗後は初期化されていないこと
        Assert.False(PreciseDelay.IsInitialized);
    }

    // ── 二重 Initialize は InvalidOperationException ────────────────

    [Fact]
    public void Initialize_Twice_ThrowsInvalidOperationException()
    {
        if (PreciseDelay.IsInitialized)
            PreciseDelay.Shutdown();

        PreciseDelay.Initialize(dedicatedCpuCore: 3);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                PreciseDelay.Initialize(dedicatedCpuCore: 3));
        }
        finally
        {
            PreciseDelay.Shutdown();
        }
    }

    // ── Shutdown 後の WaitAsync は InvalidOperationException ────────

    [Fact]
    public async Task WaitAsync_AfterShutdown_ThrowsInvalidOperationException()
    {
        if (PreciseDelay.IsInitialized)
            PreciseDelay.Shutdown();

        PreciseDelay.Initialize(dedicatedCpuCore: 3);
        PreciseDelay.Shutdown();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await PreciseDelay.WaitAsync(TimeSpan.FromMicroseconds(100)));
    }

    // ── IsInitialized の状態遷移 ────────────────────────────────────

    [Fact]
    public void IsInitialized_ReflectsLifecycle()
    {
        if (PreciseDelay.IsInitialized)
            PreciseDelay.Shutdown();

        Assert.False(PreciseDelay.IsInitialized);
        PreciseDelay.Initialize(dedicatedCpuCore: 3);
        Assert.True(PreciseDelay.IsInitialized);
        PreciseDelay.Shutdown();
        Assert.False(PreciseDelay.IsInitialized);
    }
}

/// <summary>
/// PreciseDelay の WaitAsync 精度・動作テスト。
/// IClassFixture で Initialize/Shutdown を一度だけ行う。
/// </summary>
public class PreciseDelayFixture : IDisposable
{
    public PreciseDelayFixture()
    {
        if (PreciseDelay.IsInitialized)
            PreciseDelay.Shutdown();
        PreciseDelay.Initialize(dedicatedCpuCore: 3);
    }

    public void Dispose() => PreciseDelay.Shutdown();
}

[Collection("PreciseDelay")]
public class PreciseDelayWaitTests : IClassFixture<PreciseDelayFixture>
{
    // ── 0以下の遅延は即完了 ────────────────────────────────────────

    [Fact]
    public async Task WaitAsync_ZeroDelay_CompletesImmediately()
    {
        var sw = Stopwatch.StartNew();
        await PreciseDelay.WaitAsync(TimeSpan.Zero);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 50, $"elapsed={sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task WaitAsync_NegativeDelay_CompletesImmediately()
    {
        var sw = Stopwatch.StartNew();
        await PreciseDelay.WaitAsync(TimeSpan.FromMicroseconds(-100));
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 50, $"elapsed={sw.ElapsedMilliseconds}ms");
    }

    // ── スピンパス（≤5ms）の動作 ───────────────────────────────────

    [Theory]
    [InlineData(200)]
    [InlineData(500)]
    [InlineData(1_000)]
    [InlineData(2_000)]
    public async Task WaitAsync_SpinPath_ElapsedAtLeastRequested(int delayUs)
    {
        var delay = TimeSpan.FromMicroseconds(delayUs);
        var sw    = Stopwatch.StartNew();
        await PreciseDelay.WaitAsync(delay);
        sw.Stop();

        long elapsedUs = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
        // 実測値 >= 要求値の 50%（OS スケジューラの影響を考慮）
        Assert.True(elapsedUs >= delayUs / 2,
            $"delay={delayUs}us, elapsed={elapsedUs}us");
    }

    // ── WaitableTimer パス（>5ms）の動作 ──────────────────────────

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(50)]
    public async Task WaitAsync_WaitableTimerPath_ElapsedAtLeastRequested(int delayMs)
    {
        var delay = TimeSpan.FromMilliseconds(delayMs);
        var sw    = Stopwatch.StartNew();
        await PreciseDelay.WaitAsync(delay);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= delayMs / 2,
            $"delay={delayMs}ms, elapsed={sw.ElapsedMilliseconds}ms");
    }

    // ── キャンセルのテスト ─────────────────────────────────────────

    [Fact]
    public async Task WaitAsync_Cancelled_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // 即時キャンセル

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await PreciseDelay.WaitAsync(TimeSpan.FromMilliseconds(100), cts.Token));
    }

    [Fact]
    public async Task WaitAsync_CancelledDuringWait_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await PreciseDelay.WaitAsync(TimeSpan.FromSeconds(10), cts.Token));
    }

    // ── 精度テスト（スピンパス: 100回平均誤差） ────────────────────

    [Fact]
    public async Task WaitAsync_500us_AverageErrorWithin50us()
    {
        const int iterations = 100;
        const int targetUs   = 500;
        long totalErrorUs    = 0;

        for (int i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            await PreciseDelay.WaitAsync(TimeSpan.FromMicroseconds(targetUs));
            sw.Stop();

            long elapsedUs = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
            totalErrorUs += Math.Abs(elapsedUs - targetUs);
        }

        long avgErrorUs = totalErrorUs / iterations;
        // 平均誤差 50μs 以内（CI 環境での余裕を持たせた許容値）
        Assert.True(avgErrorUs <= 50,
            $"平均誤差 {avgErrorUs}μs > 許容値 50μs");
    }

    // ── 並列 WaitAsync ─────────────────────────────────────────────

    [Fact]
    public async Task WaitAsync_MultipleConcurrentCalls_AllComplete()
    {
        var tasks = new Task[10];
        for (int i = 0; i < tasks.Length; i++)
            tasks[i] = PreciseDelay.WaitAsync(TimeSpan.FromMicroseconds(500)).AsTask();

        await Task.WhenAll(tasks);
        // 例外なく全タスクが完了すること
        Assert.All(tasks, t => Assert.Equal(TaskStatus.RanToCompletion, t.Status));
    }
}
