// SPDX-License-Identifier: MIT

#if USLP_GENERATOR
using System.Runtime.CompilerServices;
[module: SkipLocalsInit]

// TimerWheel / PreciseWaitItem は internal だが、境界条件（過去 deadline の
// 大量投入、ホイール範囲外）は PreciseDelay 越しの待機では再現できない。
// これらを直接叩くユニットテストのために公開する。
[assembly: InternalsVisibleTo("UsleepWin.Tests")]
#endif
