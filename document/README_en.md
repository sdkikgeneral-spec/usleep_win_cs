# usleep_win_cs

[日本語README](../README.md)

A pure C# library that provides a high-accuracy, low-jitter `usleep`-equivalent for Windows.

`usleep_win_cs` uses a hybrid approach to achieve practical precision with low overhead for short waits on Windows:

- High-Resolution Waitable Timer (high resolution when available)
- QueryPerformanceCounter (QPC)
- CPU hint instructions (x86 `PAUSE` / ARM64 `YIELD`)
- Fairness-oriented waiting with `Sleep(0)` / `SwitchToThread()` / `Sleep(1)`

> Note: Windows is not a hard real-time OS. Micro-wait behavior can be affected by power settings, virtualization, and background load.

---

## ✨ Features

### 🔧 High accuracy, low jitter
- Prioritizes `WaitableTimer + QPC`
- Reduces late wake-ups by using short spin only in the final phase

### 🧵 CPU-aware waiting
- Avoids spin-only waiting
- Uses `Sleep(0)` / `SwitchToThread()` / `Sleep(1)` depending on remaining time
- Easier to balance CPU usage and jitter

### 🖥 Three profiles
| Profile | Description |
|---|---|
| `BALANCED` (default) | Balanced low jitter and fairness |
| `STRICT` | Lower-jitter oriented (typically higher CPU usage) |
| `LOW_POWER` | Power-saving oriented (typically higher jitter) |

### 📊 Statistics API
- `UsleepWin.GetStats()` returns thread-local counters:
  - `SpinRelax`
  - `YieldSwitch`
  - `YieldSleep0`
  - `YieldSleep1`
  - `WaitableTimerUses`

---

## 📦 Outputs

This repository builds the following from shared sources:

- **NuGet target** (`net10.0-windows`)
  - `LibraryImport`-based P/Invoke (source generator)
- **Unity DLL target** (`netstandard2.1`)
  - Generic build (loadable on non-Windows environments)
  - Windows-only build (P/Invoke enabled)

---

## 🛠 Build

- All targets: `build_all.bat`
- NuGet target: `build_net10.bat`
- Unity Generic DLL: `build_unity_generic.bat`
- Unity Windows-only DLL: `build_unity_windows.bat`
- Clean: `clean.bat`

Example outputs:

- `pack/bin/Release/usleep_win_cs.0.1.0.nupkg`
- `unity/bin/Release/netstandard2.1/usleep_win_cs.unity.dll`

---

## 📝 Usage (C#)

### 1) Basic microsecond sleep

```csharp
using Usleep.Win;

UsleepWin.SleepMicroseconds(300); // 300 µs
```

### 2) 1ms periodic loop (deadline-based)

```csharp
using Usleep.Win;

const ulong tickUs = 1000;
ulong next = UsleepWin.NowSteadyMicroseconds();

for (;;)
{
    next += tickUs;
    UsleepWin.SleepUntilSteadyMicroseconds(next); // drift-resistant scheduling

    // do work...
}
```

### 3) Profile / policy tuning

```csharp
using Usleep.Win;

UsleepWin.SetProfile(UsleepProfile.BALANCED);
UsleepWin.SetTailSpinMicroseconds(250);
UsleepWin.SetYieldPolicy(UsleepYieldPolicy.SLEEP0);

// Lower-jitter oriented
UsleepWin.SetProfile(UsleepProfile.STRICT);

// Power-saving oriented
UsleepWin.SetProfile(UsleepProfile.LOW_POWER);
```

### 4) Read statistics

```csharp
using Usleep.Win;

UsleepStats st = UsleepWin.GetStats(reset: false);
Console.WriteLine($"spin={st.SpinRelax}, sleep0={st.YieldSleep0}, timer={st.WaitableTimerUses}");
```

### 5) Change timer resolution (only when needed)

```csharp
using Usleep.Win;

if (UsleepWin.InitTimerResolution(1))
{
    // ... high-resolution timer dependent work
    UsleepWin.ShutdownTimerResolution();
}
```

> `timeBeginPeriod(1)` affects the whole system. Use only when necessary.

---

## 🔧 Tuning guide

- Default profile is `BALANCED`.
- If you prioritize lower jitter:
  - Use `STRICT`
  - Consider `SetTailSpinMicroseconds(300~500)`
- If you prioritize CPU/power:
  - Use `LOW_POWER`
  - Prefer `SLEEP1`

Optimal settings depend on workload. Measure with your target period, acceptable lateness, and CPU budget.

---

## 🎮 Unity notes

1. Build DLL with `build_unity_generic.bat` or `build_unity_windows.bat`
2. Copy DLL to `Assets/Plugins/...` in your Unity project
3. Configure `asmdef` platform restrictions appropriately (Windows-only build should be restricted to Windows)
4. Avoid blocking Unity main thread

See `samples/UnityDemo/README.md` for details.

---

## 📁 Directory overview

```
src/                    // library source code (including Interop/Intrinsics)
pack/                   // NuGet package csproj
unity/                  // Unity DLL csproj
samples/ConsoleDemo/    // C# console usage sample
samples/UnityDemo/      // Unity usage notes
document/               // documentation (including English README)

build_all.bat           // build all targets
build_net10.bat         // build NuGet target (net10.0-windows)
build_unity_generic.bat // build Unity Generic DLL
build_unity_windows.bat // build Unity Windows-only DLL
```

---

## 📜 License

MIT License. See [LICENSE](../LICENSE) for details.

---

## 🤝 Contributing

Issues and PRs are welcome. Benchmark results and tuning insights are also appreciated.
