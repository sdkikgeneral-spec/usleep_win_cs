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

    /// <summary>
    /// WaitableTimer パスをキャンセル不能トークンで待つ。
    /// ネイティブ呼び出し失敗時に無効ハンドルを待って永久ハングした不具合の回帰防止。
    /// </summary>
    [Fact]
    public async Task WaitAsync_WaitableTimerPath_NonCancellableToken_DoesNotHang()
    {
        const int delayMs = 20;
        var sw = Stopwatch.StartNew();

        // ハングしたら WaitAsync(TimeSpan) がタイムアウト例外を投げてテストが失敗する。
        await PreciseDelay.WaitAsync(TimeSpan.FromMilliseconds(delayMs), CancellationToken.None)
                          .AsTask()
                          .WaitAsync(TimeSpan.FromSeconds(5));
        sw.Stop();

        // 上限だけでなく下限も見る（即 return する実装を通さないため）。
        Assert.True(sw.ElapsedMilliseconds >= delayMs / 2,
            $"delay={delayMs}ms, elapsed={sw.ElapsedMilliseconds}ms（早すぎる）");
        Assert.True(sw.ElapsedMilliseconds < 5_000,
            $"delay={delayMs}ms, elapsed={sw.ElapsedMilliseconds}ms（遅すぎる）");
    }

    /// <summary>
    /// WaitableTimer パスのキャンセルで、例外に渡したトークンが載っていること。
    /// TrySetCanceled() をトークン無しで呼んでいた不具合の回帰防止。
    /// </summary>
    [Fact]
    public async Task WaitAsync_WaitableTimerPath_Cancelled_CarriesToken()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await PreciseDelay.WaitAsync(TimeSpan.FromSeconds(10), cts.Token)
                              .AsTask()
                              .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(cts.Token, ex.CancellationToken);
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

    /// <summary>
    /// 既にキャンセル済みのトークンで &gt;5ms（WaitableTimer 経路）を待つと、
    /// カーネルオブジェクトを確保する前に、渡したトークン付きで即キャンセルされること。
    /// WaitableTimerAsync 冒頭の ThrowIfCancellationRequested の回帰防止。
    /// </summary>
    [Fact]
    public async Task WaitAsync_WaitableTimerPath_PreCancelled_CarriesToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await PreciseDelay.WaitAsync(TimeSpan.FromSeconds(10), cts.Token)
                              .AsTask()
                              .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(cts.Token, ex.CancellationToken);
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

    // ── スピン経路の上限付近の契約テスト ───────────────────────────
    //
    // ホイールは 1 スロット = 1µs。スロット数より長い deadline を入れると
    // 折り返して「過去のスロット」に落ち、要求より早く完了する。
    // スピン経路の上限（5ms）とホイールの表現範囲の関係が崩れると
    // ここが最初に壊れるので、境界付近を押さえておく。
    //
    // 注: これは過去の不具合の再現テストではない。旧実装は magic 除算の
    // 乗数が桁溢れしていて 1 スロットが約 2.67µs あり（実効範囲 ≒10.9ms）、
    // 5ms は折り返していなかった。旧実装の実害は早期完了ではなく
    // 「スロット粒度が 1µs でなく 2.67µs」という精度劣化。
    //
    // 上限だけのアサートでは即 return しても通ってしまうので、下限を主役にする。

    [Theory]
    [InlineData(4200)]
    [InlineData(4500)]
    [InlineData(5000)] // スピン経路の上限ちょうど
    public async Task WaitAsync_NearSpinPathUpperBound_DoesNotReturnEarly(int targetUs)
    {
        // 1 回の外れ値で落とさないよう、中央値で判定する
        const int iterations = 5;
        var samples = new long[iterations];

        for (int i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            await PreciseDelay.WaitAsync(TimeSpan.FromMicroseconds(targetUs));
            sw.Stop();
            samples[i] = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
        }

        Array.Sort(samples);
        long medianUs = samples[iterations / 2];

        // 下限: 早期完了の検出が目的。計測系の誤差ぶんだけ緩める
        Assert.True(medianUs >= targetUs - 100,
            $"要求 {targetUs}µs に対し中央値 {medianUs}µs で早期完了した"
            + $"（実測: {string.Join(", ", samples)}）");

        // 上限: 折り返しの反対側（一周待たされる）も検出する
        Assert.True(medianUs < targetUs + 2000,
            $"要求 {targetUs}µs に対し中央値 {medianUs}µs は遅すぎる"
            + $"（実測: {string.Join(", ", samples)}）");
    }

    [Fact]
    public async Task WaitAsync_LargeBurstOfShortWaits_AllComplete()
    {
        const int count = 3000;

        var tasks = new Task[count];
        for (int i = 0; i < count; i++)
            tasks[i] = PreciseDelay.WaitAsync(TimeSpan.FromMicroseconds(50)).AsTask();

        var all      = Task.WhenAll(tasks);
        var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(ReferenceEquals(finished, all),
            "10 秒以内に完了しなかった（待機項目の取りこぼしの疑い）");
        await all;
        Assert.All(tasks, t => Assert.Equal(TaskStatus.RanToCompletion, t.Status));
    }
}

/// <summary>
/// TimerWheel の境界条件を直接検証する。
/// PreciseDelay 越しではスピンスレッドのプリエンプトを再現できず、
/// 過去 deadline の大量投入やホイール範囲外の経路を踏めないため。
/// </summary>
public class TimerWheelBoundaryTests
{
    private const long RequiredSpanUs = PreciseDelay.SpinPathMaxMilliseconds * 1000L;

    // ── 過去 deadline の大量投入 ───────────────────────────────────
    //
    // スピンスレッドがプリエンプトされている間に待機要求が溜まると、
    // 再開後の drain で「既に締切を過ぎた」項目が大量に処理される。
    // これらを 1 スロットへまとめて押し込む実装だと MaxSlotCapacity(1024) を
    // 超えて GrowSlot が InvalidOperationException を投げ、呼び出し元である
    // スピンスレッドごとプロセスが落ちる。

    [Fact]
    public void Enqueue_ManyPastDeadlines_CompletesAllWithoutThrowing()
    {
        var  wheel = new TimerWheel(RequiredSpanUs);
        long now   = Stopwatch.GetTimestamp();
        wheel.Advance(now);

        const int count      = 3000; // MaxSlotCapacity(1024) を十分に超える
        long      oneMsTicks = Stopwatch.Frequency / 1000;

        var items = new PreciseWaitItem[count];
        var waits = new ValueTask[count];

        for (int i = 0; i < count; i++)
        {
            items[i] = PreciseWaitItemPool.Rent(default);
            waits[i] = items[i].AsValueTask();
            // すべて 1ms 前 = 既に締切を過ぎている
            wheel.Enqueue(items[i], now - oneMsTicks);
        }

        // 過ぎた締切はその場で完了していること（次の Advance を待たない）
        for (int i = 0; i < count; i++)
            Assert.True(waits[i].IsCompleted, $"{i} 件目が完了していない");
    }

    // ── ホイール範囲内の deadline は早期完了しない ─────────────────

    [Fact]
    public void Enqueue_DeadlineWithinSpan_DoesNotCompleteBeforeAdvance()
    {
        var  wheel = new TimerWheel(RequiredSpanUs);
        long now   = Stopwatch.GetTimestamp();
        wheel.Advance(now);

        long deadline = now + RequiredSpanUs * (Stopwatch.Frequency / 1_000_000L);

        var item = PreciseWaitItemPool.Rent(default);
        var wait = item.AsValueTask();
        wheel.Enqueue(item, deadline);

        // 締切前は完了しない
        wheel.Advance(deadline - Stopwatch.Frequency / 1000); // 1ms 手前
        Assert.False(wait.IsCompleted, "締切前に完了した（折り返しの疑い）");

        // 締切を過ぎたら完了する
        wheel.Advance(deadline + Stopwatch.Frequency / 1000); // 1ms 過ぎ
        Assert.True(wait.IsCompleted, "締切を過ぎても完了しない");
    }

    // ── 必要スパンを満たせない場合は構築に失敗する ─────────────────

    [Fact]
    public void Constructor_RequiredSpanTooLarge_Throws()
    {
        // 現実的にありえない広さを要求して、実行時検証が働くことを確認する
        Assert.Throws<NotSupportedException>(
            () => new TimerWheel(long.MaxValue / 2));
    }
}
