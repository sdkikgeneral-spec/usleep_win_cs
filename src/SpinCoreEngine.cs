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

        // スピン経路で受け付ける最大待機をホイールが表現できることを実行時に検証する
        // （表現範囲は Stopwatch.Frequency に依存するため定数比較では担保できない）。
        _wheel   = new TimerWheel(PreciseDelay.SpinPathMaxMilliseconds * 1000L);
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
        long deadline = Stopwatch.GetTimestamp()
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
            wheel.Advance(Stopwatch.GetTimestamp()); // 内部で QPC（実測 ~20 ns/call）

            while (_incoming.TryDequeue(out var req))
            {
                // drain の途中でも _currentSlot を最新に保つ。ここを省くと、
                // Advance からの経過時間ぶんだけ「何スロット先か」の判定が
                // ずれ、バーストや直前のプリエンプトでホイール範囲外と
                // 誤判定されうる。Advance は進んだスロットぶんしか回らないので
                // 通常はほぼ空振りで返る。
                wheel.Advance(Stopwatch.GetTimestamp());
                wheel.Enqueue(req.Item, req.Deadline);
            }

            if (_incoming.IsEmpty)
                Thread.SpinWait(50);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running  = false;

        // Join が成功したときだけ Dispose する。まだスピンスレッドが動いている
        // 状態で _disposed を立てた TimerWheel を触らせると、次の Enqueue が
        // ObjectDisposedException を投げてスピンスレッドごとプロセスが落ちる。
        // 取りこぼした場合はホイールを解放せず GC に委ねる。
        bool stopped = _spinThread?.Join(TimeSpan.FromSeconds(1)) ?? true;
        if (stopped)
            _wheel?.Dispose();

        _wheel      = null;
        _spinThread = null;
    }
}

#endif
