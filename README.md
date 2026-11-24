# AirPods Audio Keep-Alive

A small Windows utility for Bluetooth headphones that lose audio after a pause. It keeps the selected headphones' Windows playback stream open using shared-mode WASAPI and a very low-level 1 Hz signal.

The utility follows the default multimedia output and matches the Bluetooth device name you pass on the command line. It releases the stream when you change outputs, disconnect the headphones, lock Windows, turn off the display, or suspend the computer. Keeping the playback link active can use more headphone battery.

## Build

The utility was built on Windows 11 x64 using the compiler included with the Windows .NET Framework. Run this command in PowerShell from the repository directory.

```powershell
.\build.ps1
```

This produces `bin\AirPodsAudioKeepAlive.exe`.

## Run

In Windows sound settings, select your headphones as the default output. Substitute the exact device name shown in Bluetooth settings for `AirPods Pro` in this example.

```powershell
& .\bin\AirPodsAudioKeepAlive.exe 'AirPods Pro'
```

Use the Bluetooth device name without the Windows `Headphones (...)` label. The program runs in the background and opens a stream when the selected output has a stereo, 32-bit float mix format. To stop it, end `AirPodsAudioKeepAlive.exe` in Task Manager.

For sign-in startup, keep the executable in a permanent location and add a shortcut in the Windows Startup folder with the same device name argument. Remove the shortcut to disable startup.

## 中文说明

这是一个 Windows 蓝牙音频保活工具，用于绕过耳机暂停一段时间后恢复播放时无声的问题。程序只在指定耳机是默认输出时保持音频流，切换输出、锁屏、息屏或电脑睡眠时会释放音频流。持续保持音频连接可能增加耳机耗电。

在 PowerShell 中执行上面的构建命令，将耳机选为默认输出，再用蓝牙设置中的实际设备名运行程序。程序在后台运行，可在任务管理器中结束 `AirPodsAudioKeepAlive.exe`。

## License

MIT. See [LICENSE](LICENSE).
