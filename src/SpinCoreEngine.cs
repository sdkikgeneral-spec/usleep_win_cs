// SPDX-License-Identifier: MIT
#if !USLP_UNITY

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace Usleep.Win;

internal sealed class SpinCoreEngine : IDisposable
{
    private TimerWheel?   _wheel;
    private Thread?       _spinThread;
    private volatile bool _running;
    private bool          _disposed;

    private readonly ConcurrentQueue<(PreciseWaitItem Item, long Deadline)>
        _incoming = new();

    // P/Invoke（Initialize() 内で1回のみ使用）
    [DllImport("kernel32.dll")] static extern IntPtr  GetCurrentThread();
    [DllImport("kernel32.dll")] static extern UIntPtr SetThreadAffinityMask(IntPtr h, UIntPtr mask);
    [DllImport("kernel32.dll")] static extern bool    SetThreadPriority(IntPtr h, int priority);
    [DllImport("ntdll.dll")]    static extern int     NtSetTimerResolution(uint res, bool set, out uint cur);
    private const int THREAD_PRIORITY_TIME_CRITICAL = 15;

    public void Initialize(int cpuCore)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(SpinCoreEngine));

        // セキュリティ検証
        if ((uint)cpuCore >= (uint)Environment.ProcessorCount)
            throw new ArgumentOutOfRangeException(nameof(cpuCore),
                $"コア番号は 1〜{Environment.ProcessorCount - 1} を指定してください");
        if (cpuCore == 0)
            throw new ArgumentException(
                "コア0はOSが予約しているため使用できません", nameof(cpuCore));
        if (Process.GetCurrentProcess().PriorityClass == ProcessPriorityClass.RealTime)
            throw new SecurityException(
                "RealTime優先度クラスでの実行は禁止されています");

        _wheel   = new TimerWheel();
        _running = true;

        _spinThread = new Thread(SpinLoop)
        {
            IsBackground = true,
            Priority     = ThreadPriority.Highest,
            Name         = "PreciseTimer-SpinCore"
        };
        _spinThread.Start(cpuCore);
    }

    public ValueTask EnqueueWait(TimeSpan delay, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(SpinCoreEngine));
        var  item     = PreciseWaitItemPool.Rent(ct);
        long deadline = NativeClock.GetTimestamp()
                        + (long)(delay.TotalSeconds * Stopwatch.Frequency);
        _incoming.Enqueue((item, deadline));
        return item.AsValueTask();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SpinLoop(object? coreObj)
    {
        int core = (int)coreObj!;

        // P/Invoke はここだけ（ホットパスでは一切呼ばない）
        SetThreadAffinityMask(GetCurrentThread(), new UIntPtr(1u << core));
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
        NtSetTimerResolution(1, true, out _);

        var wheel = _wheel!;

        while (_running)
        {
            long now = NativeClock.GetTimestamp(); // ~1ns、P/Invokeゼロ
            wheel.Advance(now);                    // O(1)

            while (_incoming.TryDequeue(out var req))
                wheel.Enqueue(req.Item, req.Deadline);

            if (_incoming.IsEmpty)
                Thread.SpinWait(50);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running  = false;
        _spinThread?.Join(TimeSpan.FromSeconds(1));
        _wheel?.Dispose();
        _wheel      = null;
        _spinThread = null;
    }
}

#endif
