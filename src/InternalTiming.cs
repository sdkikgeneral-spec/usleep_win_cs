// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using System.Diagnostics;
using static NativeMethods;

internal static class InternalTiming
{
    private static readonly bool _isWin = Platform.IsWindows;
    private static readonly long _qpcFreq = InitQpc();
    private static readonly bool _hires = Stopwatch.IsHighResolution;
    private static readonly double _tickToUs = _hires ? (1_000_000.0 / Stopwatch.Frequency) : 1000.0; // fallback coefficient

#if USLP_WINDOWS || USLP_GENERATOR
    [ThreadStatic] private static IntPtr _tTimer;

    // 高分解能 WaitableTimer の可用性。プロセス全体で一度だけ確定させる。
    // -1 = 利用不可（API 欠落 or 作成失敗） / 0 = 未判定 / 1 = 利用可。
    // 競合しても複数スレッドが同じ判定を行うだけで結果は同一なのでロックしない。
    private static int _hrTimerState;
#endif

    // Stats (thread-local)
    [ThreadStatic] internal static ulong tSpinRelax, tYieldSwitch, tYieldSleep0, tYieldSleep1, tHrTimerUses;

    private static long InitQpc()
    {
#if USLP_WINDOWS
        // NuGet ビルドは USLP_WINDOWS と USLP_GENERATOR を同時に定義するため、
        // ここは NuGet でも実行される（QueryPerformanceFrequency を 1 回呼ぶ）。
        // ただし NowUs() は USLP_GENERATOR 側の Stopwatch 経路を通るので、
        // 得られた _qpcFreq が読まれるのは Unity Windows ビルドのときだけ。
        if (_isWin && QueryPerformanceFrequency(out var f)) return f.QuadPart;
#endif
        return 0;
    }

#if USLP_GENERATOR
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
#endif
    internal static ulong NowUs()
    {
#if USLP_GENERATOR
        // Stopwatch.GetTimestamp() は内部で QPC を呼ぶ（実測 ~20 ns/call）。
        //
        // 以前はここで KUSER_SHARED_DATA(0x7FFE0000) の直読みを試み「~1 ns」と
        // 称していたが、読んでいた 0x3B8 は QpcBias で単調増加するカウンタではなく
        // （実測: 50 ms 経過しても差分 0）、shift として読んでいた 0x3C4 は
        // ActiveGroupCount だった（QpcShift は 0x3C7）。結果として起動時の
        // 信頼性検証が常に失敗し、実際には毎回この Stopwatch 経路へ
        // フォールバックしていた。直読みは撤去した。
        if (_hires)
            return (ulong)(Stopwatch.GetTimestamp() * _tickToUs);
#elif USLP_WINDOWS
        if (_isWin && _qpcFreq > 0 && QueryPerformanceCounter(out var c))
        {
            // 商と剰余に分けた純整数演算。素直に ticks * 1_000_000 とすると
            // QPC 10MHz ではブートから約 10.7 日で long がオーバーフローして
            // 時刻が巻き戻る。C++ 版 qpc_now_us() と同じ計算にしてある。
            ulong ticks = (ulong)c.QuadPart;
            ulong f = (ulong)_qpcFreq;
            ulong q = ticks / f;
            ulong r = ticks % f;
            if (q > ulong.MaxValue / 1_000_000UL) return ulong.MaxValue;
            ulong baseUs = q * 1_000_000UL;
            ulong remUs = (r * 1_000_000UL) / f;
            if (baseUs > ulong.MaxValue - remUs) return ulong.MaxValue;
            return baseUs + remUs;
        }
#endif
        if (_hires)
            return (ulong)(Stopwatch.GetTimestamp() * _tickToUs);
        return (ulong)(uint)Environment.TickCount * 1000UL;
    }

    /// <summary>
    /// 現在時刻に待機長を足して deadline を作る。ラップアラウンドさせると
    /// 「もう過ぎている」と誤判定して即座に返ってしまうため飽和させる。
    /// </summary>
    internal static ulong DeadlineFromNow(ulong usec)
    {
        var now = NowUs();
        return usec > ulong.MaxValue - now ? ulong.MaxValue : now + usec;
    }

    private static long UsTo100nsNeg(ulong us)
    {
        const long k = 10L;
        if (us > (ulong)(long.MaxValue / k)) us = (ulong)(long.MaxValue / k);
        return -((long)us * k);
    }

    /// <summary>
    /// 高分解能 WaitableTimer が使えるかをプロセス全体で一度だけ判定する。
    /// 旧実装は「そのスレッドが一度長い待機をする」まで可用性が確定せず、
    /// ウォームアップ前のスレッドがタイマー経路に入れなかった。
    /// </summary>
    internal static bool HasHighResolutionTimer()
    {
#if USLP_WINDOWS || USLP_GENERATOR
        var s = Volatile.Read(ref _hrTimerState);
        if (s != 0) return s > 0;
        if (!_isWin) { Volatile.Write(ref _hrTimerState, -1); return false; }

        bool ok = false;
        try
        {
            var h = CreateWaitableTimerEx(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            if (h != IntPtr.Zero)
            {
                ok = true;
                // 試作したハンドルは捨てずにこのスレッドのタイマーとして使い回す。
                if (_tTimer == IntPtr.Zero) _tTimer = h;
                else CloseHandle(h);
            }
        }
        catch (EntryPointNotFoundException)
        {
            // CreateWaitableTimerExW を持たない古い Windows。
        }

        Volatile.Write(ref _hrTimerState, ok ? 1 : -1);
        return ok;
#else
        return false;
#endif
    }

    private static IntPtr GetTimer()
    {
#if USLP_WINDOWS || USLP_GENERATOR
        if (!_isWin) return IntPtr.Zero;
        if (_tTimer != IntPtr.Zero) return _tTimer;

        if (HasHighResolutionTimer())
        {
            // 可用性判定がこのスレッドのハンドルを確保している場合がある。
            if (_tTimer != IntPtr.Zero) return _tTimer;
            try
            {
                var h = CreateWaitableTimerEx(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
                if (h != IntPtr.Zero) return _tTimer = h;
            }
            catch (EntryPointNotFoundException) { }
        }

        // HR 不可、または HR ハンドル作成に失敗した場合は通常のタイマーで代替する。
        // ここでプロセス全体の _hrTimerState を書き換えてはならない（スコープが違う）。
        _tTimer = CreateWaitableTimer(IntPtr.Zero, false, null);
        return _tTimer;
#else
        return IntPtr.Zero;
#endif
    }

#if USLP_GENERATOR
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
#endif
    internal static void CoarseYield(Usleep.Win.UsleepYieldPolicy policy)
    {
        switch (policy)
        {
            case Usleep.Win.UsleepYieldPolicy.SWITCH_THREAD:
#if USLP_WINDOWS || USLP_GENERATOR
                if (_isWin)
                {
                    if (SwitchToThread()) tYieldSwitch++;
                    else { Thread.Yield(); tYieldSwitch++; }
                    break;
                }
#endif
                Thread.Yield();
                tYieldSwitch++;
                break;

            case Usleep.Win.UsleepYieldPolicy.SLEEP0:
#if USLP_WINDOWS || USLP_GENERATOR
                if (_isWin) { Sleep(0); tYieldSleep0++; break; }
#endif
                Thread.Yield(); tYieldSleep0++;
                break;

            case Usleep.Win.UsleepYieldPolicy.SLEEP1:
#if USLP_WINDOWS || USLP_GENERATOR
                if (_isWin) { Sleep(1); tYieldSleep1++; break; }
#endif
                Thread.Sleep(1); tYieldSleep1++;
                break;

            case Usleep.Win.UsleepYieldPolicy.NONE:
            default:
                SpinHints.HintOnce(); tSpinRelax++;
                break;
        }
    }

#if USLP_GENERATOR
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
#endif
    internal static void SpinWithPeriodicYield(ulong targetUs, uint tailSpinUs, Usleep.Win.UsleepYieldPolicy policy)
    {
        int ctr = 0;
        while (true)
        {
            var now = NowUs();
            if (now >= targetUs) break;
            var remain = targetUs - now;

            if (remain > tailSpinUs)
            {
                if ((++ctr & 63) == 0)
                    CoarseYield(policy);
                else
                {
                    SpinHints.HintFewTimes(3); tSpinRelax += 3;
                }
            }
            else
            {
                SpinHints.HintOnce(); tSpinRelax++;
            }
        }
    }

#if USLP_GENERATOR
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
#endif
    internal static void SleepByTimer(ulong usec, uint tailSpinUs, Usleep.Win.UsleepYieldPolicy policy, bool lowPowerProfile)
    {
        var targetUs = DeadlineFromNow(usec);

#if USLP_WINDOWS || USLP_GENERATOR
        if (_isWin && usec > 0)
        {
            var h = GetTimer();
            if (h != IntPtr.Zero)
            {
                ulong coarseUs = usec;
                if (tailSpinUs > 0 && usec > tailSpinUs) coarseUs = usec - tailSpinUs;
                var due = new LARGE_INTEGER { QuadPart = UsTo100nsNeg(coarseUs) };
                if (SetWaitableTimer(h, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                {
                    tHrTimerUses++;
                    WaitForSingleObject(h, 0xFFFFFFFF);
                    if (tailSpinUs > 0)
                        SpinWithPeriodicYield(targetUs, 0, Usleep.Win.UsleepYieldPolicy.NONE);
                    else if (!lowPowerProfile)
                    {
                        while (NowUs() < targetUs) { SpinHints.HintOnce(); tSpinRelax++; }
                    }
                    return;
                }
            }
            if (usec >= 1000)
            {
                ulong ms64 = usec / 1000UL;
                if (ms64 == 0) ms64 = 1;
                if (ms64 > uint.MaxValue) ms64 = uint.MaxValue;
                Sleep((uint)ms64); tYieldSleep1++;
                // Sleep(ms) は usec を ms へ切り捨てた値でしか眠らないため最大 999µs
                // 不足しうる。tailSpinUs の値に関わらず必ず target まで詰める
                // （tailSpinUs==0 の LOW_POWER で早期リターンさせないため。以前は
                // ここが if (tailSpinUs > 0) だったので LOW_POWER + タイマー不可の
                // 組み合わせで最大 999µs 早く返る契約違反があった）。
                // 詰める残りは定義上 1ms 未満で、Sleep(ms) は実際にはほぼ必ず
                // オーバーシュートするためこのループは通常 0 回転で抜ける。
                SpinWithPeriodicYield(targetUs, 0, Usleep.Win.UsleepYieldPolicy.NONE);
                return;
            }
        }
#endif
        SpinWithPeriodicYield(targetUs, tailSpinUs, policy);
    }
}
