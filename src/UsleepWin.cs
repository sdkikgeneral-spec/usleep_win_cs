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
#if USLP_WINDOWS || USLP_GENERATOR
        // タイマー分解能はプロセス全体の設定なので、スレッドローカルにしない。
        // Win32 を呼ばないバリアント（Unity Generic）では参照されないため、
        // CS0169（未使用フィールド）を避けて宣言ごと除外する。
        private static readonly object _timerResolutionLock = new object();
        private static uint _timerResolutionMs;
#endif

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

            ulong timerFirstUs, preferSpinBelow;
            switch (_profile)
            {
                case UsleepProfile.STRICT:    timerFirstUs = 1500; preferSpinBelow = 500; break;
                case UsleepProfile.LOW_POWER: timerFirstUs = 1000; preferSpinBelow = 0;   break;
                default:                      timerFirstUs = 2000; preferSpinBelow = 200; break;
            }

            bool lowPower = (_profile == UsleepProfile.LOW_POWER);

            // preferSpinBelow 側の条件は「高分解能タイマーが実際に使えるとき」だけ成立させる
            // （C++ 版 do_sleep_us() の can_hr 項）。この項が無いと preferSpinBelow==0 の
            // LOW_POWER が常にタイマー経路へ吸われ、HR タイマーが無い環境向けの
            // SLEEP1 スピン経路（下の else）が到達不能になる。
            bool canHr = HasHighResolutionTimer();
            if (usec >= timerFirstUs || (canHr && usec > preferSpinBelow))
                SleepByTimer(usec, _tailSpinUs, _yieldPolicy, lowPower);
            else
                SpinWithPeriodicYield(DeadlineFromNow(usec), lowPower ? 0U : _tailSpinUs,
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
        /// Maximum tail spin duration in microseconds accepted by
        /// <see cref="SetTailSpinMicroseconds"/> (10 ms).
        /// </summary>
        /// <remarks>
        /// テールスピン区間は 1 コアを 100% 占有するため、上限が無いと
        /// あらゆる待機が純ビジースピンに化けて待機 API の契約が壊れる。
        /// Windows 既定のタイマー分解能（約 15.6ms）を超える末尾スピンは
        /// 「タイマーの粗さをスピンで隠す」という設計目的を超えて意味がなく、
        /// 既定クォンタム（約 20〜30ms）を超えるとプリエンプトされて逆に精度が落ちる。
        /// その下側にあたる 10ms を上限とする（C++ 版 <c>USLEEP_SPIN_LAST_US_MAX</c> と同値）。
        /// 既定値および各プロファイルが設定する値（0 / 250 / 400）はすべて範囲内。
        /// </remarks>
        public const uint MaxTailSpinMicroseconds = 10000;

        /// <summary>
        /// Sets tail spin duration used in the final wait phase.
        /// </summary>
        /// <param name="tailSpinUs">Tail spin duration in microseconds.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="tailSpinUs"/> exceeds <see cref="MaxTailSpinMicroseconds"/>.
        /// 設定は変更されない。
        /// </exception>
        public static void SetTailSpinMicroseconds(uint tailSpinUs)
        {
            if (tailSpinUs > MaxTailSpinMicroseconds)
                throw new ArgumentOutOfRangeException(
                    nameof(tailSpinUs), tailSpinUs,
                    $"tailSpinUs must be <= {MaxTailSpinMicroseconds}.");

            _tailSpinUs = tailSpinUs;
        }

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
        /// <remarks>
        /// Applies to the calling thread only. It does not affect the
        /// <c>PreciseDelay</c> spin thread or any other thread.
        /// Requires Windows 10 1709 or later; returns false where thread power
        /// throttling is unavailable.
        /// </remarks>
        public static bool SetPowerMode(UsleepPowerMode mode)
        {
            if (!Enum.IsDefined(typeof(UsleepPowerMode), mode)) return false;
#if USLP_WINDOWS || USLP_GENERATOR
            if (!Platform.IsWindows) return false;

            // ControlMask は「どのスロットリングを自分で制御するか」、StateMask は
            // 「それを有効にするか」。DEFAULT で ControlMask を立てたままにすると
            // OS 既定に戻らず PERF と同じ扱いになるため、3 モードを区別する。
            uint controlMask, stateMask;
            switch (mode)
            {
                case UsleepPowerMode.ECO:
                    controlMask = THREAD_POWER_THROTTLING_EXECUTION_SPEED;
                    stateMask   = THREAD_POWER_THROTTLING_EXECUTION_SPEED;
                    break;
                case UsleepPowerMode.PERF:
                    controlMask = THREAD_POWER_THROTTLING_EXECUTION_SPEED;
                    stateMask   = 0u;
                    break;
                default: // DEFAULT: OS 既定の挙動に戻す
                    controlMask = 0u;
                    stateMask   = 0u;
                    break;
            }

            var state = new THREAD_POWER_THROTTLING_STATE
            {
                Version     = THREAD_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = controlMask,
                StateMask   = stateMask
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
