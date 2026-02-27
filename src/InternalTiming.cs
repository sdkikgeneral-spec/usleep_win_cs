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
#endif

    // Stats (thread-local)
    [ThreadStatic] internal static ulong tSpinRelax, tYieldSwitch, tYieldSleep0, tYieldSleep1, tHrTimerUses;

    private static long InitQpc()
    {
#if USLP_WINDOWS || USLP_GENERATOR
        if (_isWin && QueryPerformanceFrequency(out var f)) return f.QuadPart;
        return 0;
#else
        return 0;
#endif
    }

#if USLP_GENERATOR
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
#endif
    internal static ulong NowUs()
    {
#if USLP_WINDOWS || USLP_GENERATOR
        if (_isWin && _qpcFreq > 0 && QueryPerformanceCounter(out var c))
            return (ulong)((c.QuadPart * 1_000_000L) / _qpcFreq);
#endif
        if (_hires)
            return (ulong)(Stopwatch.GetTimestamp() * _tickToUs);
        return (ulong)(uint)Environment.TickCount * 1000UL;
    }

    private static long UsTo100nsNeg(long us) => -(us * 10L);

    private static IntPtr GetTimer()
    {
#if USLP_WINDOWS || USLP_GENERATOR
        if (!_isWin) return IntPtr.Zero;
        if (_tTimer != IntPtr.Zero) return _tTimer;
        _tTimer = CreateWaitableTimerEx(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (_tTimer == IntPtr.Zero)
            _tTimer = CreateWaitableTimerEx(IntPtr.Zero, null, 0, TIMER_ALL_ACCESS);
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
    internal static void SleepByTimer(long usec, uint tailSpinUs, Usleep.Win.UsleepYieldPolicy policy, bool lowPowerProfile)
    {
        var targetUs = NowUs() + (ulong)usec;

#if USLP_WINDOWS || USLP_GENERATOR
        if (_isWin && usec > 0)
        {
            var h = GetTimer();
            if (h != IntPtr.Zero)
            {
                long coarseUs = usec;
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
                var ms = (uint)(usec / 1000);
                if (ms == 0) ms = 1;
                Sleep(ms); tYieldSleep1++;
                if (tailSpinUs > 0) SpinWithPeriodicYield(targetUs, 0, Usleep.Win.UsleepYieldPolicy.NONE);
                return;
            }
        }
#endif
        SpinWithPeriodicYield(targetUs, tailSpinUs, policy);
    }
}
