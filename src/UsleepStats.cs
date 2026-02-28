// SPDX-License-Identifier: MIT

namespace Usleep.Win
{
    /// <summary>
    /// Thread-local counters collected during sleep operations.
    /// </summary>
    public readonly struct UsleepStats
    {
        /// <summary>
        /// Number of spin-hint operations used while waiting.
        /// </summary>
        public ulong SpinRelax { get; }

        /// <summary>
        /// Number of times SwitchToThread-based yielding occurred.
        /// </summary>
        public ulong YieldSwitch { get; }

        /// <summary>
        /// Number of times Sleep(0)-based yielding occurred.
        /// </summary>
        public ulong YieldSleep0 { get; }

        /// <summary>
        /// Number of times Sleep(1)-based yielding occurred.
        /// </summary>
        public ulong YieldSleep1 { get; }

        /// <summary>
        /// Number of waits handled by waitable timer.
        /// </summary>
        public ulong WaitableTimerUses { get; }

        /// <summary>
        /// Initializes a new statistics snapshot.
        /// </summary>
        /// <param name="spinRelax">Spin-hint count.</param>
        /// <param name="yieldSwitch">SwitchToThread yield count.</param>
        /// <param name="yieldSleep0">Sleep(0) yield count.</param>
        /// <param name="yieldSleep1">Sleep(1) yield count.</param>
        /// <param name="waitableTimerUses">Waitable timer use count.</param>
        public UsleepStats(ulong spinRelax, ulong yieldSwitch, ulong yieldSleep0, ulong yieldSleep1, ulong waitableTimerUses)
        {
            SpinRelax = spinRelax;
            YieldSwitch = yieldSwitch;
            YieldSleep0 = yieldSleep0;
            YieldSleep1 = yieldSleep1;
            WaitableTimerUses = waitableTimerUses;
        }
    }
}
