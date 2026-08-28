// SPDX-License-Identifier: MIT
#if !USLP_UNITY

using System;
using System.Diagnostics;
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

        // 閾値超は WaitableTimer HR にフォールバック（省電力）
        return delay.TotalMilliseconds > SpinPathMaxMilliseconds
            ? new ValueTask(WaitableTimerAsync(delay, ct))
            : engine.EnqueueWait(delay, ct);
    }

    /// <summary>
    /// スピンスレッド（タイマーホイール）で処理する待機の上限（ms）。
    /// これを超える待機は WaitableTimer HR へ回す。
    /// </summary>
    /// <remarks>
    /// この値はタイマーホイールが表現できる範囲を超えてはならない。超えると
    /// スロットが一周して deadline が過去のスロットに落ち、要求より早く完了する。
    /// 実際に表現できる範囲は <see cref="Stopwatch.Frequency"/> に依存するため、
    /// 検証は <see cref="SpinCoreEngine.Initialize"/> 経由で
    /// <see cref="TimerWheel"/> のコンストラクタが実行時に行う
    /// （<c>Debug.Assert</c> は Release ビルドで消えるので使わない）。
    /// </remarks>
    internal const int SpinPathMaxMilliseconds = 5;

    // ── WaitableTimer HR フォールバック ──────────────────────────

    /// <remarks>
    /// <para>
    /// ネイティブ呼び出しが失敗しても例外は投げず、次の順で段階的に劣化する。
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     HR タイマー … <c>CreateWaitableTimerExW</c> に
    ///     <c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c> を指定
    ///   </description></item>
    ///   <item><description>
    ///     非 HR タイマー … 同じ <c>CreateWaitableTimerExW</c> を <c>dwFlags: 0</c> で再試行
    ///     （HR は Win10 1803 以降でのみ有効）
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="Task.Delay(TimeSpan, CancellationToken)"/> による経過時間ベースのループ
    ///   </description></item>
    /// </list>
    /// <para>
    /// 「例外を投げずに劣化させる」という方針は同期側の <c>InternalTiming.SleepByTimer</c> と
    /// 共有するが、<b>劣化の段は同一ではない</b>。同期側が持つ非 Ex 版
    /// <c>CreateWaitableTimer</c> への降格段と、失敗をプロセス単位で memoize する仕組みは
    /// 本メソッドには無い。<c>CreateWaitableTimerExW</c> は Windows 8 以降に存在し、
    /// このファイルがコンパイルされる唯一のバリアントは <c>net10.0-windows</c>
    /// （最低要件 Windows 10 1607）なので
    /// <see cref="EntryPointNotFoundException"/> 経路が実質到達不能であり、
    /// そのために静的状態を増やす価値が無いため。
    /// ただし到達しない限りコストがゼロなので、<b>捕捉だけはしてある</b>。
    /// </para>
    /// <para>
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> は内部タイマーの tick 粒度に従い
    /// 要求より僅かに早く復帰しうるが、フォールバック時も
    /// <b>要求時間より早く返らない</b>（<see cref="DelayFallbackAsync"/> が
    /// <see cref="Stopwatch"/> で実経過を検証し、残りがあれば待ち直す）。
    /// ただし精度は OS のタイマー分解能依存まで落ちる。
    /// </para>
    /// </remarks>
    private static async Task WaitableTimerAsync(TimeSpan delay, CancellationToken ct)
    {
        // キャンセル済みなら、カーネルオブジェクトを確保する前に抜ける。
        ct.ThrowIfCancellationRequested();

        SafeWaitHandle? handle = null;
        try
        {
            handle = NativeMethods.CreateWaitableTimerExSafe(
                IntPtr.Zero, null,
                NativeMethods.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
                NativeMethods.TIMER_ALL_ACCESS);

            // HR タイマー未サポート（Win10 1803 未満）なら非 HR タイマーで再試行。
            if (handle.IsInvalid)
            {
                handle.Dispose();
                handle = NativeMethods.CreateWaitableTimerExSafe(
                    IntPtr.Zero, null, 0, NativeMethods.TIMER_ALL_ACCESS);
            }
        }
        catch (EntryPointNotFoundException)
        {
            // CreateWaitableTimerExW 自体が存在しない環境。
            handle?.Dispose();
            handle = null;
        }

        if (handle is null || handle.IsInvalid)
        {
            handle?.Dispose();
            await DelayFallbackAsync(delay, ct).ConfigureAwait(false);
            return;
        }

        // SafeWaitHandle を EventWaitHandle に差し替えて RegisterWaitForSingleObject に渡す。
        // waitHandle.Dispose() がタイマーハンドルの解放も兼ねる。
        var waitHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
        // setter は旧ハンドルを閉じないので、退避して自分で解放する。
        var eventHandle = waitHandle.SafeWaitHandle;
        waitHandle.SafeWaitHandle = handle;
        eventHandle.Dispose();

        long dueTime = -(delay.Ticks);
        if (!NativeMethods.SetWaitableTimerSafe(handle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
        {
            // 武装できなかったタイマーを待つと永久にハングするのでフォールバックへ落とす。
            waitHandle.Dispose(); // タイマーハンドルもここで閉じる
            await DelayFallbackAsync(delay, ct).ConfigureAwait(false);
            return;
        }

        RegisteredWaitHandle? registered = null;
        try
        {
            var tcs = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            registered = ThreadPool.RegisterWaitForSingleObject(
                waitHandle,
                static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
                tcs, Timeout.Infinite, executeOnlyOnce: true);

            // 呼び出し側の `when (e.CancellationToken == ct)` が機能するよう
            // トークン付きでキャンセルする。state はタプルで渡しクロージャを避ける。
            // なおこれは >5ms の WaitableTimer 経路だけの性質で、API 全体の保証ではない。
            // ≤5ms のスピン経路は PreciseWaitItem 側がトークン無しでキャンセルする。
            await using (ct.Register(
                static s =>
                {
                    var (t, token) = ((TaskCompletionSource, CancellationToken))s!;
                    t.TrySetCanceled(token);
                },
                (tcs, ct)))
                await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            // キャンセルで抜けた場合、登録はまだ生きている。解除しないと
            // ThreadPool の待機スロットとコールバックがタイマー発火まで延命する。
            // 順序は Unregister が先。登録中のハンドルは
            // RegisterWaitForSingleObject が SafeWaitHandle に取る参照カウントで
            // 保護されており Dispose() だけでは CloseHandle されないが、これは
            // BCL の実装詳細なので依存せず、先に解除しておく。
            registered?.Unregister(null);
            waitHandle.Dispose();
        }
    }

    // ── Task.Delay フォールバック ────────────────────────────────

    /// <summary>1 回の <see cref="Task.Delay(TimeSpan, CancellationToken)"/> に渡せる上限。</summary>
    /// <remarks>
    /// <c>Task.Delay</c> は内部 <c>Timer</c> の上限（<c>Timer.MaxSupportedTimeout</c>
    /// = 4294967294ms ≒ 49.7 日）を超えると
    /// <see cref="ArgumentOutOfRangeException"/> を投げる。
    /// <c>SetWaitableTimer</c> にはこの上限が無いため、そのまま渡すと
    /// 「タイマー成功時は動くのにフォールバック時だけ例外」という筋の通らない劣化になる。
    /// そこで 1 回あたりの待機をこの値でクランプし、ループで積み上げる。
    /// 実値はその上限そのもの（<c>uint.MaxValue - 1</c> = 4294967294ms）。
    /// <c>Task.Delay(TimeSpan, CancellationToken)</c> は内部で ms を <c>uint</c> 換算するため
    /// この値までは受け付け、<c>uint.MaxValue</c> で初めて拒否する（実測確認済）。
    /// </remarks>
    private static readonly TimeSpan MaxSingleDelay =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>1 回あたりの最小待機。残りが極小のとき 0ms 待機で回り続けるのを防ぐ。</summary>
    private static readonly TimeSpan MinSingleDelay = TimeSpan.FromMilliseconds(1);

    /// <summary>フォールバックループの反復予算に加える収束用の余裕（保険）。</summary>
    /// <remarks>
    /// 反復予算は「チャンク分割に必要な回数
    /// （<c>delay.Ticks / MaxSingleDelay.Ticks</c>）」にこの値を足して求める。
    /// 収束（<see cref="Task.Delay(TimeSpan, CancellationToken)"/> の早期復帰を待ち直す反復）は
    /// 通常 1～3 反復で足りる。早期復帰は内部 <c>TimerQueue</c> の tick 境界丸めに由来し、
    /// 1 回あたり最大 1 tick（既定のタイマー分解能で約 15.6ms）しかずれないため。
    /// 反復上限を固定値にすると、大きな <c>delay</c> でチャンクを消化しきる前に
    /// ループを抜け、診断も例外も無く「要求時間より早く返らない」不変条件が破れる。
    /// そのため予算は必ず <c>delay</c> から導出する（ただし構造的には有界のまま）。
    /// </remarks>
    private const int FallbackConvergenceMargin = 8;

    /// <summary>
    /// ネイティブタイマーが使えないときの最終フォールバック。
    /// </summary>
    /// <remarks>
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> は内部タイマーの tick 粒度に従うため
    /// 要求より僅かに早く復帰しうる。同期側 <c>InternalTiming.SleepByTimer</c> が
    /// どの劣化段でも最後に目標時刻まで詰めて「早く返らない」不変条件を保つのに合わせ、
    /// ここでも <see cref="Stopwatch"/> で実経過を検証し、残りがあれば待ち直す。
    /// 詰めにスピン（busy-wait）は使わない。このパスは省電力側のフォールバックであり、
    /// 非同期メソッドがスレッドを焼いてはならないため。
    /// </remarks>
    private static async Task DelayFallbackAsync(TimeSpan delay, CancellationToken ct)
    {
        long start = Stopwatch.GetTimestamp();

        // 目標経過 tick。TimeSpan.MaxValue 近辺でも long を溢れさせない。
        double targetTicksD = delay.TotalSeconds * Stopwatch.Frequency;
        long targetTicks = targetTicksD >= long.MaxValue
            ? long.MaxValue
            : (long)targetTicksD;

        // 反復予算を delay から導出する。delay.Ticks の最大値を MaxSingleDelay.Ticks
        // （定数・非ゼロ）で割っても 30 万未満なので桁溢れしない。
        // delay.Ticks が負・0 でも予算は FallbackConvergenceMargin を下回らない。
        long budget = FallbackConvergenceMargin
            + Math.Max(0L, delay.Ticks) / MaxSingleDelay.Ticks;

        var remaining = delay;
        for (long i = 0; i < budget; i++)
        {
            if (remaining > MaxSingleDelay) remaining = MaxSingleDelay;
            else if (remaining < MinSingleDelay) remaining = MinSingleDelay;

            await Task.Delay(remaining, ct).ConfigureAwait(false);

            long remainTicks = targetTicks - (Stopwatch.GetTimestamp() - start);
            if (remainTicks <= 0)
                return;

            remaining = TimeSpan.FromSeconds((double)remainTicks / Stopwatch.Frequency);
        }
    }
}

#endif
