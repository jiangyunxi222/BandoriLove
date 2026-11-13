"""Keep one local GSV model loaded and accept synthesis requests over stdio."""
import contextlib
import json
import os
from pathlib import Path
import sys
import traceback


def send(channel, value):
    print("BANDORI " + json.dumps(value, ensure_ascii=False), file=channel, flush=True)


def main():
    channel = os.fdopen(os.dup(sys.stdout.fileno()), "w", encoding="utf8", buffering=1)
    os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
    config = Path(sys.argv[1]).resolve()
    device = sys.argv[2] if len(sys.argv) > 2 else "cpu"
    sys.path.insert(0, str(Path.cwd()))
    sys.argv = ["api_v2.py", "-c", str(config)]
    with contextlib.redirect_stdout(sys.stderr):
        import torch
        import yaml
        if device == "cuda:0" and not torch.cuda.is_available():
            raise RuntimeError("CUDA 不可用，请检查 NVIDIA 驱动和 CUDA 运行库。")
        if device == "cuda:0":
            torch.empty(1, device=device)
        data = yaml.safe_load(config.read_text(encoding="utf8"))
        data["custom"]["device"] = device
        data["custom"]["is_half"] = device.startswith("cuda:")
        config.write_text(yaml.safe_dump(data), encoding="utf8")
        try:
            import jieba_fast
        except ModuleNotFoundError:
            import jieba
            import jieba.posseg
            sys.modules["jieba_fast"] = jieba
            sys.modules["jieba_fast.posseg"] = jieba.posseg
        from tools import portable_runtime
        portable_runtime.RUNTIME_DIR = Path(__file__).resolve().parent / "runtime"
        portable_runtime.SITE_PACKAGES = Path(torch.__file__).resolve().parent.parent
        portable_runtime.FFMPEG_EXE = portable_runtime.RUNTIME_DIR / "ffmpeg.exe"
        portable_runtime.FFPROBE_EXE = portable_runtime.RUNTIME_DIR / "ffprobe.exe"
        from api_v2 import tts_pipeline
        import soundfile
    send(channel, {"ready": True})
    for line in sys.stdin:
        try:
            request = json.loads(line)
            output = Path(request.pop("output"))
            request.update(streaming_mode=False, parallel_infer=False, batch_size=1,
                           text_split_method="cut5", use_cuda_graph=False)
            with contextlib.redirect_stdout(sys.stderr):
                with contextlib.closing(tts_pipeline.run(request)) as audio:
                    rate, samples = next(audio)
                soundfile.write(output, samples, rate, format="WAV", subtype="PCM_16")
            send(channel, {"ok": True})
        except Exception as error:
            traceback.print_exc(file=sys.stderr)
            send(channel, {"error": str(error) or type(error).__name__})


if __name__ == "__main__":
    main()
