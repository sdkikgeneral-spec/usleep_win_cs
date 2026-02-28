// SPDX-License-Identifier: MIT

namespace Usleep.Win
{
    /// <summary>
    /// Preset behavior profile for microsecond sleep.
    /// </summary>
    public enum UsleepProfile
    {
        /// <summary>
        /// Balanced profile for general workloads.
        /// </summary>
        BALANCED = 0,

        /// <summary>
        /// Lower jitter profile with more aggressive waiting.
        /// </summary>
        STRICT = 1,

        /// <summary>
        /// Lower power profile with less aggressive waiting.
        /// </summary>
        LOW_POWER = 2
    }

    /// <summary>
    /// Yield behavior used during cooperative wait phases.
    /// </summary>
    public enum UsleepYieldPolicy
    {
        /// <summary>
        /// Do not perform explicit yielding.
        /// </summary>
        NONE = 0,

        /// <summary>
        /// Yield using SwitchToThread semantics.
        /// </summary>
        SWITCH_THREAD = 1,

        /// <summary>
        /// Yield using Sleep(0).
        /// </summary>
        SLEEP0 = 2,

        /// <summary>
        /// Yield using Sleep(1).
        /// </summary>
        SLEEP1 = 3
    }

    /// <summary>
    /// Power preference for the current thread.
    /// </summary>
    public enum UsleepPowerMode
    {
        /// <summary>
        /// Keep platform default power behavior.
        /// </summary>
        DEFAULT = 0,

        /// <summary>
        /// Prefer performance over power saving.
        /// </summary>
        PERF = 1,

        /// <summary>
        /// Prefer energy saving mode.
        /// </summary>
        ECO = 2
    }
}
