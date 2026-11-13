"""Keep one local GSV model loaded and accept synthesis requests over stdio."""
import contextlib
import json
from pathlib import Path
import sys
import traceback


def send(value):
    print(json.dumps(value, ensure_ascii=False), flush=True)


def main():
    config = Path(sys.argv[1]).resolve()
    sys.path.insert(0, str(Path.cwd()))
    sys.argv = ["api_v2.py", "-c", str(config)]
    with contextlib.redirect_stdout(sys.stderr):
        try:
            import jieba_fast
        except ModuleNotFoundError:
            import jieba
            import jieba.posseg
            sys.modules["jieba_fast"] = jieba
            sys.modules["jieba_fast.posseg"] = jieba.posseg
        from tools import portable_runtime
        portable_runtime.RUNTIME_DIR = config.parent / "runtime"
        portable_runtime.SITE_PACKAGES = portable_runtime.RUNTIME_DIR / "Lib" / "site-packages"
        portable_runtime.FFMPEG_EXE = portable_runtime.RUNTIME_DIR / "ffmpeg.exe"
        portable_runtime.FFPROBE_EXE = portable_runtime.RUNTIME_DIR / "ffprobe.exe"
        from api_v2 import tts_pipeline
        import soundfile
    send({"ready": True})
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
            send({"ok": True})
        except Exception as error:
            traceback.print_exc(file=sys.stderr)
            send({"error": str(error) or type(error).__name__})


if __name__ == "__main__":
    main()
