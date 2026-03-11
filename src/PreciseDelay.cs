// SPDX-License-Identifier: MIT
#if !USLP_UNITY

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Usleep.Win;

/// <summary>
/// 高精度非同期タイマーの公開 API。
/// 既存の UsleepWin と並列して使用する。
///
/// 使い分け：
///   UsleepWin    → 精度不要・省電力優先の箇所（既存コードはそのまま）
///   PreciseDelay → ±1〜3μs 精度が必要な箇所（新たに使う）
///
/// ≤5ms → SpinCoreEngine（スピン・精度優先）
/// &gt;5ms → WaitableTimer HR（省電力フォールバック）
/// </summary>
public static class PreciseDelay
{
    private static SpinCoreEngine? _engine;

    /// <summary>初期化済みかどうか。</summary>
    public static bool IsInitialized => _engine is not null;

    /// <summary>
    /// アプリ起動時に1回だけ呼ぶ。
    /// </summary>
    /// <param name="dedicatedCpuCore">
    /// 専有する CPU コア番号（0 は禁止、デフォルト: 3）
    /// </param>
    public static void Initialize(int dedicatedCpuCore = 3)
    {
        if (_engine is not null)
            throw new InvalidOperationException("既に初期化済みです");

        var engine = new SpinCoreEngine();
        engine.Initialize(dedicatedCpuCore); // 例外が出た場合は _engine に代入しない
        _engine = engine;
    }

    /// <summary>アプリ終了時に呼ぶ。</summary>
    public static void Shutdown()
    {
        _engine?.Dispose();
        _engine = null;
    }

    /// <summary>
    /// 高精度非同期待機。目標精度 ±1〜3μs。
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Initialize() を呼ぶ前に使用した場合。
    /// </exception>
    public static ValueTask WaitAsync(
        TimeSpan delay,
        CancellationToken ct = default)
    {
        var engine = _engine
            ?? throw new InvalidOperationException(
                $"{nameof(Initialize)}() を先に呼び出してください");

        if (delay.Ticks <= 0)
            return ValueTask.CompletedTask;

        // 5ms 超は WaitableTimer HR にフォールバック（省電力）
        return delay.TotalMilliseconds > 5
            ? new ValueTask(WaitableTimerAsync(delay, ct))
            : engine.EnqueueWait(delay, ct);
    }

    // ── WaitableTimer HR フォールバック ──────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(
        IntPtr lpTimerAttributes, string? lpTimerName,
        uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(
        SafeWaitHandle hTimer, ref long lpDueTime,
        int lPeriod, IntPtr pfnCompletionRoutine,
        IntPtr lpArgToCompletionRoutine, bool fResume);

    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;

    private static async Task WaitableTimerAsync(TimeSpan delay, CancellationToken ct)
    {
        var handle = CreateWaitableTimerExW(
            IntPtr.Zero, null,
            CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            TIMER_ALL_ACCESS);

        long dueTime = -(delay.Ticks);
        SetWaitableTimer(handle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false);

        var tcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // SafeWaitHandle を EventWaitHandle に差し替えて RegisterWaitForSingleObject に渡す
        using var waitHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
        waitHandle.SafeWaitHandle = handle;

        ThreadPool.RegisterWaitForSingleObject(
            waitHandle,
            static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
            tcs, Timeout.Infinite, executeOnlyOnce: true);

        await using (ct.Register(
            static s => ((TaskCompletionSource)s!).TrySetCanceled(), tcs))
            await tcs.Task.ConfigureAwait(false);
    }
}

#endif
