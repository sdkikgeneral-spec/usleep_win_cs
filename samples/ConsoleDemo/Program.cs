// SPDX-License-Identifier: MIT

using System;
using Usleep.Win;

class Program
{
    static void Main()
    {
        // Recommended baseline: BALANCED profile + short tail spin + Sleep(0) yield.
        UsleepWin.SetProfile(UsleepProfile.BALANCED);
        UsleepWin.SetTailSpinMicroseconds(250);
        UsleepWin.SetYieldPolicy(UsleepYieldPolicy.SLEEP0);
        PreciseDelay.Initialize(dedicatedCpuCore: 3);

        // Deadline-based 1ms loop to reduce drift.
        const ulong tickUs = 1000;
        ulong next = UsleepWin.NowSteadyMicroseconds();

        for (int i = 0; i < 50; i++)
        {
            next += tickUs;
            UsleepWin.SleepUntilSteadyMicroseconds(next);

            // Observe lateness relative to the intended deadline.
            ulong now = UsleepWin.NowSteadyMicroseconds();
            long late = (long)(now > next ? now - next : 0);
            Console.WriteLine($"tick {i} late={late}us");

            // Re-sync when overrun is large.
            if (late > 5000) next = now;
        }

        PreciseDelay.Shutdown();
    }
}
