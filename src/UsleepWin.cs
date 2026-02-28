// SPDX-License-Identifier: MIT

using System;
using static InternalTiming;
using static NativeMethods;

namespace Usleep.Win
{
    /// <summary>
    /// High-accuracy sleep utilities for Windows using timer and spin/yield hybrid waiting.
    /// </summary>
    public static class UsleepWin
    {
        [ThreadStatic] private static UsleepProfile _profile = UsleepProfile.BALANCED;
        [ThreadStatic] private static uint _tailSpinUs = 250;
        [ThreadStatic] private static UsleepYieldPolicy _yieldPolicy = UsleepYieldPolicy.SLEEP0;
        private static readonly object _timerResolutionLock = new();
        private static uint _timerResolutionMs;

#if USLP_GENERATOR
    /// <summary>
    /// Sleeps for the specified microseconds.
    /// </summary>
    /// <param name="usec">Sleep duration in microseconds.</param>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
#else
    /// <summary>
    /// Sleeps for the specified microseconds.
    /// </summary>
    /// <param name="usec">Sleep duration in microseconds.</param>
#endif
        public static void SleepMicroseconds(ulong usec)
        {
            if (usec == 0) { CoarseYield(UsleepYieldPolicy.SWITCH_THREAD); return; }

            long timerFirstUs, preferSpinBelow;
            switch (_profile)
            {
                case UsleepProfile.STRICT:    timerFirstUs = 1500; preferSpinBelow = 500; break;
                case UsleepProfile.LOW_POWER: timerFirstUs = 1000; preferSpinBelow = 0;   break;
                default:                      timerFirstUs = 2000; preferSpinBelow = 200; break;
            }

            bool lowPower = (_profile == UsleepProfile.LOW_POWER);
            if ((long)usec >= timerFirstUs || (long)usec > preferSpinBelow)
                SleepByTimer((long)usec, _tailSpinUs, _yieldPolicy, lowPower);
            else
                SpinWithPeriodicYield(NowUs() + usec, lowPower ? 0U : _tailSpinUs,
                                      lowPower ? UsleepYieldPolicy.SLEEP1 : _yieldPolicy);
        }

        /// <summary>
        /// Sleeps for the specified nanoseconds.
        /// </summary>
        /// <param name="nsec">Sleep duration in nanoseconds.</param>
        public static void SleepNanoseconds(ulong nsec) => SleepMicroseconds(nsec / 1000);

        /// <summary>
        /// Gets current monotonic timestamp in microseconds.
        /// </summary>
        /// <returns>Current steady-clock timestamp in microseconds.</returns>
        public static ulong NowSteadyMicroseconds() => NowUs();

#if USLP_GENERATOR
    /// <summary>
    /// Sleeps until the specified steady-clock deadline.
    /// </summary>
    /// <param name="targetUs">Target timestamp in microseconds.</param>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
#else
    /// <summary>
    /// Sleeps until the specified steady-clock deadline.
    /// </summary>
    /// <param name="targetUs">Target timestamp in microseconds.</param>
#endif
        public static void SleepUntilSteadyMicroseconds(ulong targetUs)
        {
            var now = NowUs(); if (targetUs > now) SleepMicroseconds(targetUs - now);
        }

    /// <summary>
    /// Applies a preset profile for sleep behavior.
    /// </summary>
    /// <param name="profile">Profile to apply.</param>
        public static void SetProfile(UsleepProfile profile)
        {
            _profile = profile;
            if (_profile == UsleepProfile.LOW_POWER)
            {
                _tailSpinUs = 0; _yieldPolicy = UsleepYieldPolicy.SLEEP1;
            }
            else if (_profile == UsleepProfile.STRICT)
            {
                if (_tailSpinUs < 300) _tailSpinUs = 400; _yieldPolicy = UsleepYieldPolicy.SWITCH_THREAD;
            }
            else
            {
                _tailSpinUs = 250; _yieldPolicy = UsleepYieldPolicy.SLEEP0;
            }
        }

        /// <summary>
        /// Sets tail spin duration used in the final wait phase.
        /// </summary>
        /// <param name="tailSpinUs">Tail spin duration in microseconds.</param>
        public static void SetTailSpinMicroseconds(uint tailSpinUs) => _tailSpinUs = tailSpinUs;

        /// <summary>
        /// Sets cooperative yield policy used while waiting.
        /// </summary>
        /// <param name="policy">Yield policy.</param>
        public static void SetYieldPolicy(UsleepYieldPolicy policy) => _yieldPolicy = policy;

        /// <summary>
        /// Sets thread power throttling mode when supported.
        /// </summary>
        /// <param name="mode">Requested power mode.</param>
        /// <returns>True if the mode is applied; otherwise false.</returns>
        public static bool SetPowerMode(UsleepPowerMode mode)
        {
#if USLP_WINDOWS || USLP_GENERATOR
            if (!Platform.IsWindows) return false;
            var state = new THREAD_POWER_THROTTLING_STATE
            {
                Version = THREAD_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = THREAD_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask   = (mode == UsleepPowerMode.ECO) ? THREAD_POWER_THROTTLING_EXECUTION_SPEED : 0u
            };
            return SetThreadInformation(GetCurrentThread(), ThreadPowerThrottling,
                ref state, (uint)System.Runtime.InteropServices.Marshal.SizeOf<THREAD_POWER_THROTTLING_STATE>());
#else
            return false;
#endif
        }

        /// <summary>
        /// Requests timer resolution change in milliseconds.
        /// </summary>
        /// <param name="ms">Requested period in milliseconds.</param>
        /// <returns>True if the request succeeds; otherwise false.</returns>
        public static bool InitTimerResolution(uint ms)
        {
#if USLP_WINDOWS || USLP_GENERATOR
            if (!Platform.IsWindows) return false;
            if (ms == 0) return false;
            lock (_timerResolutionLock)
            {
                if (_timerResolutionMs == ms) return true;

                if (_timerResolutionMs != 0)
                {
                    timeEndPeriod(_timerResolutionMs);
                    _timerResolutionMs = 0;
                }

                if (timeBeginPeriod(ms) == 0)
                {
                    _timerResolutionMs = ms;
                    return true;
                }
                return false;
            }
#else
            return false;
#endif
        }

        /// <summary>
        /// Releases timer resolution request created by <see cref="InitTimerResolution(uint)"/>.
        /// </summary>
        public static void ShutdownTimerResolution()
        {
#if USLP_WINDOWS || USLP_GENERATOR
            if (!Platform.IsWindows) return;
            lock (_timerResolutionLock)
            {
                if (_timerResolutionMs != 0)
                {
                    timeEndPeriod(_timerResolutionMs);
                    _timerResolutionMs = 0;
                }
            }
#endif
        }

        /// <summary>
        /// Gets current thread-local statistics.
        /// </summary>
        /// <param name="reset">When true, counters are reset after retrieval.</param>
        /// <returns>Statistics snapshot.</returns>
        public static UsleepStats GetStats(bool reset = false)
        {
            var s = new UsleepStats(tSpinRelax, tYieldSwitch, tYieldSleep0, tYieldSleep1, tHrTimerUses);
            if (reset) ResetStats();
            return s;
        }

        /// <summary>
        /// Resets thread-local statistics counters.
        /// </summary>
        public static void ResetStats()
        {
            tSpinRelax = tYieldSwitch = tYieldSleep0 = tYieldSleep1 = tHrTimerUses = 0;
        }
    }
}
