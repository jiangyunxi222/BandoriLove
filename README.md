# BandoriLove

Windows 语音合成客户端。输入文本、选择角色卡，即可调用 GPT-SoVITS 推理服务，试听并保存 WAV。

## 使用

1. 打开 `BandoriLove.exe`，点击左栏「添加」。
2. 填入角色名、GPT-SoVITS `api_v2.py` 的基础地址，以及推理端可访问的参考音频路径、参考台词与参考语言。
3. 如果 API 已固定加载该角色的权重，角色卡中的权重可以留空。需要按卡切换时，填写推理端的 GPT `.ckpt` 和 SoVITS `.pth` 两个路径。
4. 回到主窗口，选角色卡，输入要朗读的文本，选择输入语言并生成。生成后可播放或保存。

音频路径和权重路径以运行 GPT-SoVITS 的机器为准。角色卡保存在 `%LOCALAPPDATA%\BandoriLove\characters.local.json`。如模型在 GPU 服务器上，优先连接内网 API 或通过 SSH 隧道连接回环地址。应用也可以接入同机运行的 GPT-SoVITS。

固定角色 API 可用独立配置启动。GPT-SoVITS 的动态权重切换会修改该推理进程的全局模型状态。多个客户端共享时，给不同模型配置各自的推理实例，避免生成到错误的角色。

## 构建

需要 Windows 和 .NET 10 SDK。

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

发布产物位于 `dist/BandoriLove.exe`。桌面端使用 WPF，通过 GPT-SoVITS API 执行推理。
