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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Complete()
    {
        Debug.Assert(IsInitialized,
            "未初期化の PreciseWaitItem への Complete 呼び出し");
        IsInitialized = false;
        _vtsc.SetResult(true);
        PreciseWaitItemPool.Return(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CompleteAsCancelled()
    {
        Debug.Assert(IsInitialized,
            "未初期化の PreciseWaitItem への Cancel 呼び出し");
        IsInitialized = false;
        _vtsc.SetException(new OperationCanceledException());
        PreciseWaitItemPool.Return(this);
    }

    // IValueTaskSource
    public void GetResult(short token) => _vtsc.GetResult(token);
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
