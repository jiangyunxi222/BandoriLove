# BandoriLove

Windows 语音合成客户端。输入文本、选择角色卡，即可调用 GPT-SoVITS 推理服务，试听并保存 WAV。

## Windows 本机推理

将训练好的第 2 轮 GPT 与 SoVITS 权重放在 `weights/`，并准备 Comfy 的 `jjj-gsv-v5` 项目资产。生成的本机版将引擎、Python 运行时、权重和角色参考音频放在 EXE 旁边的 `gsv/` 目录，首次生成时启动引擎并加载模型。桌面程序与引擎通过标准输入输出通信。

```powershell
python scripts/prepare-offline.py PATH/TO/jjj-gsv-v5 weights dist
python scripts/import-comfy-cards.py PATH/TO/jjj-gsv-v5/bandori-assets dist/characters.local.json --local
```

将匹配的 Windows Python 3.12 和 GPT-SoVITS 依赖安装到 `dist/gsv/runtime/`。打包脚本会对照 Comfy 训练报告核验两份权重的 SHA-256，并校验角色参考音频。移动程序时应连同 `dist` 中的模型包和角色卡一起移动。

「硬件」面板每 2 秒刷新 CPU、内存、NVIDIA 显卡利用率和显存。推理默认优先选择 CUDA 显卡，可读取显存使用量时优先选空闲显存最多的显卡，也可以指定一张卡或 CPU。自动模式在显卡无法初始化时改用 CPU。监测接口不可用时，仍可通过 CUDA 驱动检测和选择显卡。Windows CUDA 版需将匹配的 PyTorch 与 TorchAudio 安装到 `dist/gsv/cuda/`，CPU 运行库仍位于 `dist/gsv/runtime/`。

```powershell
python -m pip install --target dist/gsv/cuda torch==2.8.0 torchaudio==2.8.0 --index-url https://download.pytorch.org/whl/cu128
```

发布自包含 .NET EXE 后，可把 `gsv/` 与角色卡封装到同一个程序文件。

```powershell
python scripts/package-single-exe.py dist/BandoriLove.exe dist dist/BandoriLove-Offline.exe --extractor "C:\Program Files\7-Zip\7z.exe"
```

首次运行会解包到 `%LOCALAPPDATA%\BandoriLove\packages`。单文件版复用同一套 PyTorch 库执行 CPU 和 CUDA 推理。支持 CUDA 的显卡由 Windows NVIDIA 驱动提供访问，程序包包含 CUDA 推理库和 7-Zip 解压器。7-Zip 的版权、许可证及源代码获取说明见 [7-Zip 官网](https://www.7-zip.org/)。

## 使用

1. 打开 `BandoriLove.exe`，点击左栏「添加」。
2. 填入角色名、GPT-SoVITS `api_v2.py` 的基础地址，以及推理端可访问的参考音频路径、参考台词与参考语言。
3. 如果 API 已固定加载该角色的权重，角色卡中的权重可以留空。需要按卡切换时，填写推理端的 GPT `.ckpt` 和 SoVITS `.pth` 两个路径。
4. 回到主窗口，选角色卡，输入要朗读的文本，选择输入语言并生成。生成后可播放或保存。

音频路径和权重路径以运行 GPT-SoVITS 的机器为准。角色卡保存在 `%LOCALAPPDATA%\BandoriLove\characters.local.json`。如模型在 GPU 服务器上，优先连接内网 API 或通过 SSH 隧道连接回环地址。应用也可以接入同机运行的 GPT-SoVITS。

固定角色 API 可用独立配置启动。GPT-SoVITS 的动态权重切换会修改该推理进程的全局模型状态。多个客户端共享时，给不同模型配置各自的推理实例，避免生成到错误的角色。

## 接入 Comfy 项目的共享模型

训练资产的对比报告推荐第 2 轮。此配置在独立推理容器中固定加载第 2 轮权重，使用首轮保留的参考音频切换角色。

1. 将 `scripts/start-comfy-gsv.sh` 复制到训练服务器，执行 `bash start-comfy-gsv.sh /path/to/jjj-gsv-v5`。复用服务器已有的 GSV 镜像和训练目录。服务只监听服务器的 `127.0.0.1:19883`。
2. 在 Windows 保持 SSH 转发运行，命令为 `ssh -N -L 127.0.0.1:19883:127.0.0.1:19883 user@server`。
3. 在 Windows 执行 `python scripts/import-comfy-cards.py PATH/TO/bandori-assets dist/characters.local.json`。角色卡取自评测使用的角色和参考台词。首次启动时会读取 EXE 同目录的角色卡文件，也可在软件内点击「导入」。

## 构建

需要 Windows 和 .NET 10 SDK。

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

发布产物位于 `dist/BandoriLove.exe`。桌面端使用 WPF，通过 GPT-SoVITS API 执行推理。
