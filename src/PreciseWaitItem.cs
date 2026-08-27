// SPDX-License-Identifier: MIT
#if !USLP_UNITY

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Microsoft.Extensions.ObjectPool;

namespace Usleep.Win;

/// <summary>
/// ヒープアロケーションゼロの待機アイテム。
/// Complete() / CompleteAsCancelled() は SpinThread のみが呼ぶ設計。
/// Interlocked 不使用。IsInitialized で use-after-free を防止する。
/// </summary>
internal sealed class PreciseWaitItem
    : IValueTaskSource, IPooledObjectPolicy<PreciseWaitItem>
{
    private ManualResetValueTaskSourceCore<bool> _vtsc;

    public CancellationToken CancellationToken { get; private set; }
    public bool IsInitialized { get; private set; }
    public bool CancellationRequested => CancellationToken.IsCancellationRequested;

    public void Reset(CancellationToken ct)
    {
        _vtsc.Reset();
        CancellationToken = ct;
        IsInitialized     = true;
    }

    public ValueTask AsValueTask() => new(this, _vtsc.Version);

    // 完了時にプールへ返してはならない。返した瞬間に別スレッドが Rent して
    // Reset() を呼ぶと _vtsc.Version が変わり、まだ await していない
    // 呼び出し元の ValueTask がトークン不一致で壊れる。
    // 返却は GetResult（＝呼び出し元が結果を受け取り終えた時点）で行う。

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Complete()
    {
        Debug.Assert(IsInitialized,
            "未初期化の PreciseWaitItem への Complete 呼び出し");
        IsInitialized = false;
        _vtsc.SetResult(true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CompleteAsCancelled()
    {
        Debug.Assert(IsInitialized,
            "未初期化の PreciseWaitItem への Cancel 呼び出し");
        IsInitialized = false;
        _vtsc.SetException(new OperationCanceledException());
    }

    // IValueTaskSource
    public void GetResult(short token)
    {
        try
        {
            _vtsc.GetResult(token);
        }
        finally
        {
            // await されずに捨てられた ValueTask はここを通らずプールに戻らないが、
            // GC に回収されるだけで正しさは損なわれない。
            PreciseWaitItemPool.Return(this);
        }
    }

    public ValueTaskSourceStatus GetStatus(short token) => _vtsc.GetStatus(token);
    public void OnCompleted(Action<object?> continuation, object? state,
        short token, ValueTaskSourceOnCompletedFlags flags)
        => _vtsc.OnCompleted(continuation, state, token, flags);

    // IPooledObjectPolicy
    public PreciseWaitItem Create() => new();
    public bool Return(PreciseWaitItem obj)
    {
        obj.IsInitialized     = false;
        obj.CancellationToken = default;
        return true;
    }
}

internal static class PreciseWaitItemPool
{
    private static readonly ObjectPool<PreciseWaitItem> _pool =
        new DefaultObjectPoolProvider().Create<PreciseWaitItem>();

    public static PreciseWaitItem Rent(CancellationToken ct)
    {
        var item = _pool.Get();
        item.Reset(ct);
        return item;
    }

    public static void Return(PreciseWaitItem item) => _pool.Return(item);
}

#endif
