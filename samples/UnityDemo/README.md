# Unity Demo（使い方）

このサンプルは `usleep_win_cs.unity.dll` を Unity プロジェクトに取り込んで使うためのメモです。

## 1) DLL をビルド

リポジトリルートで実行:

- Generic 版（非 Windows でも読み込める構成）
  ```bat
  build_unity_generic.bat
  ```

- Windows-only 版（P/Invoke 有効）
  ```bat
  build_unity_windows.bat
  ```

生成物:

```text
unity/bin/Release/netstandard2.1/usleep_win_cs.unity.dll
```

## 2) Unity プロジェクトへ配置

DLL を Unity プロジェクトの任意の Plugins 配下（例: `Assets/Plugins/usleep_win_cs/`）へコピーします。

## 3) asmdef 設定

- `asmdef` で DLL を参照します。
- **Windows-only 版**を使う場合は、対象プラットフォームを **Windows Editor / Windows Standalone** に制限してください。

## 4) 実行時の注意

- 高頻度ループはワーカースレッドで実行し、Unity メインスレッドをブロックしないようにします。
- `timeBeginPeriod(1)` 相当の変更（`UsleepWin.InitTimerResolution(1)`）はシステム全体に影響するため、必要時のみ有効化してください。
