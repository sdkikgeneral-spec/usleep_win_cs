// SPDX-License-Identifier: MIT

using Xunit;

namespace UsleepWin.Tests;

/// <summary>
/// PreciseDelay は静的状態を持つため、関連テストを同一コレクションに入れて直列実行する。
/// </summary>
[CollectionDefinition("PreciseDelay", DisableParallelization = true)]
public class PreciseDelayCollection { }
