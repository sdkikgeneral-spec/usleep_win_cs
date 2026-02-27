# usleep_win_cs

高精度・低ジッタな Windows 向け `usleep` 相当機能を **pure C#** で提供するライブラリです。

`usleep_win_cs` は、Windows 上で **実用的な精度と低負荷** の短時間待機を実現するために、
以下を組み合わせたハイブリッド方式を採用しています。

- High-Resolution Waitable Timer（環境により高分解能）
- QueryPerformanceCounter (QPC)
- CPU ヒント命令（x86 `PAUSE` / ARM64 `YIELD`）
- `Sleep(0)` / `SwitchToThread()` / `Sleep(1)` を併用した公平性重視の待機

> 注: Windows はハードリアルタイム OS ではありません。微小待機の結果は、
> 電源管理・仮想化・バックグラウンド負荷などの影響を受けます。

---

## ✨ 特長

### 🔧 高精度・低ジッタ
- `WaitableTimer + QPC` を優先利用
- 最終区間のみ短くスピンすることで遅着を抑制しやすい設計

### 🧵 CPU に配慮した待機
- スピン一辺倒ではなく、状況に応じて `Sleep(0)` / `SwitchToThread()` / `Sleep(1)` を利用
- 負荷とジッタのバランスを取りやすい

### 🖥 3つのプロファイル
| プロファイル | 説明 |
|---|---|
| `BALANCED`（既定） | 低ジッタと公平性のバランス |
| `STRICT` | より低ジッタ重視（CPU 消費は増えやすい） |
| `LOW_POWER` | 省電力重視（ジッタは増えやすい） |

### 📊 統計カウンタ API
- `UsleepWin.GetStats()` で以下のスレッドローカル統計を取得可能
	- `SpinRelax`
	- `YieldSwitch`
	- `YieldSleep0`
	- `YieldSleep1`
	- `WaitableTimerUses`

---

## 📦 出力物

同一ソースから以下をビルドします。

- **NuGet 向け**（`net10.0-windows`）
	- `LibraryImport` ベースの P/Invoke（source generator）
- **Unity 向け DLL**（`netstandard2.1`）
	- Generic 版（非 Windows でも読み込める構成）
	- Windows-only 版（P/Invoke 有効）

---

## 🛠 ビルド

- 全ターゲット: `build_all.bat`
- NuGet 向け: `build_net10.bat`
- Unity Generic DLL: `build_unity_generic.bat`
- Unity Windows-only DLL: `build_unity_windows.bat`
- クリーン: `clean.bat`

生成物の例:

- `pack/bin/Release/usleep_win_cs.0.1.0.nupkg`
- `unity/bin/Release/netstandard2.1/usleep_win_cs.unity.dll`

---

## 📝 使用例（C#）

### 1) 基本のマイクロ秒スリープ

```csharp
using Usleep.Win;

UsleepWin.SleepMicroseconds(300); // 300 µs
```

### 2) 1ms 周期ループ（締切方式）

```csharp
using Usleep.Win;

const ulong tickUs = 1000;
ulong next = UsleepWin.NowSteadyMicroseconds();

for (;;)
{
		next += tickUs;
		UsleepWin.SleepUntilSteadyMicroseconds(next); // ドリフト抑制

		// do work...
}
```

### 3) プロファイル / ポリシー調整

```csharp
using Usleep.Win;

UsleepWin.SetProfile(UsleepProfile.BALANCED);
UsleepWin.SetTailSpinMicroseconds(250);
UsleepWin.SetYieldPolicy(UsleepYieldPolicy.SLEEP0);

// 低ジッタ寄り
UsleepWin.SetProfile(UsleepProfile.STRICT);

// 省電力寄り
UsleepWin.SetProfile(UsleepProfile.LOW_POWER);
```

### 4) 統計の取得

```csharp
using Usleep.Win;

UsleepStats st = UsleepWin.GetStats(reset: false);
Console.WriteLine($"spin={st.SpinRelax}, sleep0={st.YieldSleep0}, timer={st.WaitableTimerUses}");
```

### 5) （必要時のみ）タイマー分解能の変更

```csharp
using Usleep.Win;

if (UsleepWin.InitTimerResolution(1))
{
		// ... high-resolution timer dependent work
		UsleepWin.ShutdownTimerResolution();
}
```

> `timeBeginPeriod(1)` はシステム全体に影響します。必要時のみ使用してください。

---

## 🔧 チューニングの目安

- 既定は `BALANCED`。
- 低ジッタを優先する場合:
	- `STRICT`
	- `SetTailSpinMicroseconds(300〜500)` 付近を検討
- CPU/電力を優先する場合:
	- `LOW_POWER`
	- `SLEEP1` ベース

ワークロードにより最適値は変わるため、目標周期・許容遅着・CPU 使用率を合わせて計測調整するのが推奨です。

---

## 🎮 Unity 利用メモ

1. `build_unity_generic.bat` か `build_unity_windows.bat` で DLL を作成
2. Unity プロジェクトの `Assets/Plugins/...` に DLL を配置
3. `asmdef` のプラットフォーム制限を適切に設定（Windows-only 版は Windows 向けに制限）
4. メインスレッドをブロックしない設計で利用

詳細は `samples/UnityDemo/README.md` を参照してください。

---

## 📁 ディレクトリ概要

```
src/                   // 共有ソース
pack/                  // NuGet 向け csproj
unity/                 // Unity 向け csproj
samples/ConsoleDemo/   // C# コンソール使用例
samples/UnityDemo/     // Unity 利用メモ
```

---

## 📜 ライセンス

MIT License。詳細は [LICENSE](./LICENSE) を参照してください。

---

## 🤝 貢献

Issue / PR 歓迎です。計測結果やチューニング知見の共有も歓迎します。
