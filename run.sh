#!/bin/sh
# Запуск AutoParser на Linux: CLI из собранного образа, образцы и результат — через тома.
#
#   ./run.sh process "/data/Заявка№10878 26-130531 НТЭ"
#   ./run.sh dump "/data/Заявка№10878 26-130531 НТЭ/Михайлова Осаго.pdf"
#
# DATA_DIR — папка с заявками (по умолчанию AutoParser/data), OUT_DIR — куда писать отчёт.
set -e

ROOT=$(cd "$(dirname "$0")" && pwd)
DATA_DIR=${DATA_DIR:-$ROOT/AutoParser/data}
OUT_DIR=${OUT_DIR:-$ROOT/out}
IMAGE=${IMAGE:-autoparser}

mkdir -p "$OUT_DIR"

exec docker run --rm -i \
    --memory "${MEMORY:-4g}" \
    -v "$DATA_DIR:/data" \
    -v "$OUT_DIR:/out" \
    "$IMAGE" "$@" --out "/out"
