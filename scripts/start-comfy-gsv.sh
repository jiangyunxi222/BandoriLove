#!/usr/bin/env bash
set -euo pipefail

root=$(realpath "${1:?Usage: bash start-comfy-gsv.sh /path/to/jjj-gsv-v5}")
baseline="$root/bandori-shared-v1"
checkpoint="$root/bandori-shared-v2"
port=19883
image=jjj-gsv-v5:runtime-e91506f
name=bandorilove-gsv
state="$root/bandorilove-inference"
mkdir -p "$state/home"

python3 - "$checkpoint" "$state/tts.yaml" <<'PY'
import json
import sys
from pathlib import Path, PurePosixPath

root = Path(sys.argv[1])
weights = json.loads((root / "shared-weights.json").read_text(encoding="utf8"))
if weights["status"] != "complete":
    raise ValueError("Round 2 weights have not completed verification")
config = {
    "version": "v5dev",
    "device": "cpu",
    "is_half": False,
    "bert_base_path": "GPT_SoVITS/pretrained_models/chinese-roberta-wwm-ext-large",
    "cnhuhbert_base_path": "GPT_SoVITS/pretrained_models/chinese-hubert-base",
}
for source, destination in (("gpt", "t2s_weights_path"), ("sovits", "vits_weights_path")):
    relative = PurePosixPath(weights[source]).relative_to("/data")
    if ".." in relative.parts or not (root / relative).is_file():
        raise FileNotFoundError(relative)
    config[destination] = str(PurePosixPath("/round-2") / relative)
content = "custom:\n" + "".join(f"  {key}: {json.dumps(value)}\n" for key, value in config.items())
Path(sys.argv[2]).write_text(content, encoding="utf8")
PY

test -d "$baseline/roles"
docker run -d --name "$name" -p "127.0.0.1:$port:9880" \
  --user "$(id -u):$(id -g)" --cpus 8 --memory 24g --cap-drop ALL \
  --security-opt no-new-privileges:true \
  -e CUDA_VISIBLE_DEVICES=-1 -e OMP_NUM_THREADS=8 -e HOME=/data/home \
  -e TORCH_FORCE_NO_WEIGHTS_ONLY_LOAD=1 \
  -v "$state:/data" -v "$baseline:/round-1:ro" -v "$checkpoint:/round-2:ro" \
  -v "$root/pretrained_models:/opt/gsv/GPT_SoVITS/pretrained_models:ro" \
  -v "$root/G2PWModel:/opt/gsv/GPT_SoVITS/text/G2PWModel:ro" \
  -v "$root/data/nltk_data:/data/nltk_data:ro" \
  -v "$root/data/nltk_data:/opt/gsv/runtime/nltk_data:ro" \
  -v "$root/data/open_jtalk_dic_utf_8-1.11:/data/open_jtalk_dic_utf_8-1.11:ro" \
  --entrypoint python "$image" -B -u api_v2.py -a 0.0.0.0 -p 9880 -c /data/tts.yaml

for ((attempt=0; attempt<90; attempt++)); do
  if curl --silent --fail --max-time 3 "http://127.0.0.1:$port/openapi.json" >/dev/null; then
    printf 'GPT-SoVITS API ready on server loopback port %s\n' "$port"
    exit 0
  fi
  if [[ $(docker inspect --format '{{.State.Running}}' "$name") != true ]]; then break; fi
  sleep 2
done
docker logs --tail 60 "$name" >&2
exit 1
