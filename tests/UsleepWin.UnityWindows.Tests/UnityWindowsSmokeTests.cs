// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using Usleep.Win;
using Xunit;

namespace UsleepWin.UnityWindows.Tests;

/// <summary>
/// Unity Windows バリアント（<c>USLP_UNITY</c> + <c>USLP_WINDOWS</c>）のスモークテスト。
///
/// このバリアントは <c>build_unity_windows.bat</c> の <c>%3B</c> がバッチ引数として
/// 展開される不具合により、長らくビルドすら通っていなかった。そのため
/// <c>DllImport</c> 版の P/Invoke と QPC 経路は一度も実行されたことがない。
/// ビルドが通るだけでは実行時の正しさは分からないので、ここで実際に踏ませる。
///
/// nupkg バリアント（<c>USLP_GENERATOR</c>）のテストは
/// <c>tests/UsleepWin.Tests</c> にある。両者は別の P/Invoke 実装を通る。
/// </summary>
public class UnityWindowsSmokeTests
{
    // ── 時刻源（QPC 直呼び経路） ───────────────────────────────────

    [Fact]
    public void NowSteadyMicroseconds_IsNonZeroAndMonotonic()
    {
        ulong t1 = Usleep.Win.UsleepWin.NowSteadyMicroseconds();
        Assert.True(t1 > 0, "QPC 経路が時刻を返していない");

        ulong t2 = Usleep.Win.UsleepWin.NowSteadyMicroseconds();
        Assert.True(t2 >= t1, $"単調増加でない: t1={t1}, t2={t2}");
    }

    [Fact]
    public void NowSteadyMicroseconds_AdvancesAtRealTimeRate()
    {
        // QPC の周波数換算が壊れていると、進み方が実時間とずれる。
        // 単調性チェックだけでは検出できないのでレートを見る。
        ulong before = Usleep.Win.UsleepWin.NowSteadyMicroseconds();
        var   sw     = Stopwatch.StartNew();
        System.Threading.Thread.Sleep(50);
        sw.Stop();
        ulong after = Usleep.Win.UsleepWin.NowSteadyMicroseconds();

        long reportedUs = (long)(after - before);
        long actualUs   = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;

        // ±10% を許容。Sleep(50) 自体の精度は問わない（実測値と突き合わせるため）。
        // 見たいのは換算係数なので、ここを緩めすぎると 1.5 倍のスケールずれを見逃す。
        Assert.True(reportedUs > actualUs * 9 / 10 && reportedUs < actualUs * 11 / 10,
            $"QPC の換算レートがずれている: 実測 {actualUs}µs に対し {reportedUs}µs を報告");
    }

    [Fact]
    public void NowSteadyMicroseconds_AbsoluteValueMatchesSystemUptime()
    {
        // レート（差分）の検証だけでは絶対値の破綻を検出できない。
        // Unity Windows パスの `c.QuadPart * 1_000_000L / _qpcFreq` は long 演算で、
        // QPC 10MHz ならブートから約 10.7 日で乗算がオーバーフローする。
        // そのとき差分は正常に見えるのに絶対値だけが桁違いになるため、ここで押さえる。
        //
        // 基準には Environment.TickCount64（ブートからの経過 ms）を使う。
        // Stopwatch と比べてはいけない: CoreCLR の Stopwatch.GetTimestamp() は QPC
        // そのものを返すのでブート基準だが、**Unity の Mono ではプロセス基準**であり
        // 一致しない（Unity Editor で実測: QPC 95,936 秒 vs Stopwatch 4.9 秒）。
        // TickCount64 はどちらの環境でもブート基準なので、共通の物差しになる。
        ulong reported = Usleep.Win.UsleepWin.NowSteadyMicroseconds();
        long  uptimeUs = Environment.TickCount64 * 1000L;

        // TickCount64 の粒度は 15.6ms 程度。桁違いの破綻を見たいので 60 秒を許容する。
        long diff = Math.Abs((long)reported - uptimeUs);
        Assert.True(diff < 60_000_000L,
            $"NowSteadyMicroseconds の絶対値がシステム稼働時間と乖離: "
            + $"{reported}µs vs {uptimeUs}µs（差 {diff}µs）。"
            + "稼働時間が長い環境では QPC 乗算のオーバーフローを疑うこと");
    }

    // ── 待機（WaitableTimer / Sleep / SwitchToThread の DllImport 経路） ──

    [Theory]
    [InlineData(500UL)]
    [InlineData(2_000UL)]
    public void SleepMicroseconds_WaitsAtLeastRequested(ulong sleepUs)
    {
        var sw = Stopwatch.StartNew();
        Usleep.Win.UsleepWin.SleepMicroseconds(sleepUs);
        sw.Stop();

        long elapsedUs = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;

        // 下限を主役にする。上限だけだと即 return しても通ってしまう。
        // かつ `sleepUs / 2` のような緩い下限にしない。2 倍速のクロック換算バグが
        // ちょうど通り抜けてしまう（500µs 要求 → 250µs 復帰 → 250 >= 250 で合格）。
        // SpinWithPeriodicYield は NowUs() >= targetUs まで回るので、実装が正しければ
        // 必ず要求値以上になる。計測系の誤差ぶんだけ 10% 緩める。
        Assert.True(elapsedUs >= (long)sleepUs * 9 / 10,
            $"要求 {sleepUs}µs に対し {elapsedUs}µs で戻った");
    }

    [Fact]
    public void SleepMicroseconds_Zero_DoesNotThrow()
    {
        // usec==0 は SwitchToThread() の DllImport を踏む
        var ex = Record.Exception(() => Usleep.Win.UsleepWin.SleepMicroseconds(0));
        Assert.Null(ex);
    }

    // ── 待機経路を踏んだことを統計カウンタで証明する ───────────────
    //
    // 設定値と入力値だけでは、実装がどの分岐を通ったか分からない。
    // WaitableTimer（CreateWaitableTimerEx / SetWaitableTimer /
    // WaitForSingleObject）の DllImport が実際に成功したかを差分で見る。

    [Fact]
    public void SleepMicroseconds_LongWait_UsesWaitableTimer()
    {
        Usleep.Win.UsleepWin.ResetStats();
        Usleep.Win.UsleepWin.SleepMicroseconds(3_000); // timerFirstUs(2000) 超

        var stats = Usleep.Win.UsleepWin.GetStats();
        Assert.True(stats.WaitableTimerUses > 0,
            "WaitableTimer 経路を踏んでいない（DllImport が失敗している可能性）");
    }

    [Fact]
    public void SleepMicroseconds_ShortWait_UsesSpin()
    {
        Usleep.Win.UsleepWin.SetProfile(UsleepProfile.BALANCED);
        Usleep.Win.UsleepWin.ResetStats();
        Usleep.Win.UsleepWin.SleepMicroseconds(100); // preferSpinBelow(200) 以下

        var stats = Usleep.Win.UsleepWin.GetStats();

        // SpinRelax > 0 だけではスピン経路の証明にならない。SleepByTimer も
        // tailSpin 区間で tSpinRelax を加算するため、タイマー経路でも成立しうる
        // （現状はタイマーのオーバーシュートで tail-spin が 0 回転になり
        // たまたま判別できているだけ）。タイマーを使っていないことを併せて示す。
        Assert.True(stats.SpinRelax > 0, "スピン経路を踏んでいない");
        Assert.Equal(0UL, stats.WaitableTimerUses);
    }

    // ── SetPowerMode（SetThreadInformation の DllImport 経路） ──────
    //
    // ThreadPowerThrottling の列挙値が誤っていると全モードで false になる。

    // 注: THREAD_POWER_THROTTLING_EXECUTION_SPEED は Windows 10 1709 以降の機能。
    // それ未満や一部の仮想化環境では正当に false が返るため、その場合は
    // 回帰ではなく環境要因。ここは対応環境で回す前提のテストとする。

    [Theory]
    [InlineData(UsleepPowerMode.DEFAULT)]
    [InlineData(UsleepPowerMode.PERF)]
    [InlineData(UsleepPowerMode.ECO)]
    public void SetPowerMode_ValidMode_Succeeds(UsleepPowerMode mode)
    {
        try
        {
            Assert.True(Usleep.Win.UsleepWin.SetPowerMode(mode),
                $"SetPowerMode({mode}) が false を返した"
                + "（Windows 10 1709 未満など非対応環境では環境要因）");
        }
        finally
        {
            // 復元の成否も見る。失敗するとスロットリングが残ったスレッドで
            // 後続の計測系テストが走ってしまう。
            Assert.True(Usleep.Win.UsleepWin.SetPowerMode(UsleepPowerMode.DEFAULT),
                "電力モードを DEFAULT に復元できなかった");
        }
    }

    // ── タイマー分解能（winmm.dll の DllImport 経路） ───────────────

    [Fact]
    public void TimerResolution_InitAndShutdown_Succeeds()
    {
        try
        {
            Assert.True(Usleep.Win.UsleepWin.InitTimerResolution(1),
                "timeBeginPeriod(1) が失敗した");
        }
        finally
        {
            // システム全体の設定なので必ず戻す
            Usleep.Win.UsleepWin.ShutdownTimerResolution();
        }
    }

    [Fact]
    public void InitTimerResolution_Zero_ReturnsFalse()
    {
        Assert.False(Usleep.Win.UsleepWin.InitTimerResolution(0));
    }

    // ── プロファイル設定 ───────────────────────────────────────────

    [Fact]
    public void SetProfile_ChangesWaitBehaviourWithoutThrowing()
    {
        try
        {
            Usleep.Win.UsleepWin.SetProfile(UsleepProfile.STRICT);
            Usleep.Win.UsleepWin.SleepMicroseconds(500);

            Usleep.Win.UsleepWin.SetProfile(UsleepProfile.LOW_POWER);
            Usleep.Win.UsleepWin.SleepMicroseconds(500);
        }
        finally
        {
            Usleep.Win.UsleepWin.SetProfile(UsleepProfile.BALANCED);
        }
    }
}
