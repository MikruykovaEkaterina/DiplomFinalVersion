#!/bin/bash
# Custom entrypoint: skip LT_POWERCYCLE (avoids IncompleteRead on model download).
# Mount both Argos bundles to /opt (see docker-compose).
# LibreTranslate calls check_and_install_models when fewer than 2 packages are installed —
# then it hits the network (~150+ MB per direction). One local en→ru file is NOT enough;
# you need ru→en as well for LT_LOAD_ONLY=en,ru to stay fully offline.
__dirname=$(cd "$(dirname "$0")"; pwd -P)
cd "${__dirname}/.."

echo ""
echo "LibreTranslate (custom entrypoint: skip network model sync at boot)"
echo "v$(cat VERSION)"
echo ""

echo Booting...
touch /tmp/booting.flag

echo "Skipping powercycle model download (use pre-installed Argos packages)."

rm -f /tmp/booting.flag

eval "$(./venv/bin/python ./scripts/print_args_env.py "$@")"

for MODEL_BUNDLE in \
 /opt/translate-en_ru-1_9.argosmodel \
 /opt/translate-ru_en-1_9.argosmodel; do
 if [[ -f "$MODEL_BUNDLE" ]]; then
  echo "Installing Argos model from $MODEL_BUNDLE ..."
  ./venv/bin/python -c "import argostranslate.package as ap; ap.install_from_path(r'''${MODEL_BUNDLE}''')"
 fi
done

PKG_COUNT="$(./venv/bin/python -c "import argostranslate.package as p; print(len(p.get_installed_packages()))" 2>/dev/null | tr -d '\r\n ' || true)"
if [[ -z "$PKG_COUNT" || ! "$PKG_COUNT" =~ ^[0-9]+$ ]]; then PKG_COUNT=0; fi
if [[ "$PKG_COUNT" -lt 2 ]]; then
 echo ""
 echo "ERROR: Найдено Argos-пакетов: ${PKG_COUNT}. Нужно минимум 2 (en→ru и ru→en)." >&2
 echo "Иначе LibreTranslate при каждом воркере Gunicorn снова качает модели из сети (IncompleteRead)." >&2
 echo "Положите в папку TranslationServer оба файла и пересоздайте контейнер:" >&2
 echo "  translate-en_ru-1_9.argosmodel  translate-ru_en-1_9.argosmodel" >&2
 echo "Скачать: .\\Install-EnRu-Model.ps1" >&2
 echo ""
 exit 1
fi

if [[ $LT_HOST == "127.0.0.1" ]]; then
 BIND_ADDR="0.0.0.0"
 if [[ -f /proc/sys/net/ipv6/conf/all/disable_ipv6 ]]; then
 IPV6_STATUS=$(cat /proc/sys/net/ipv6/conf/all/disable_ipv6)
 if [[ $IPV6_STATUS -eq 0 ]]; then
 BIND_ADDR="[::]"
 fi
 fi
else
 BIND_ADDR="$LT_HOST"
fi

unset LT_UPDATE_MODELS
unset FORCE_UPDATE_MODELS

export PROMETHEUS_MULTIPROC_DIR=$(realpath "${__dirname}/../db/prometheus")
if [[ -e "$PROMETHEUS_MULTIPROC_DIR" ]]; then
 find "$PROMETHEUS_MULTIPROC_DIR" -name '*.db' -delete
else
 mkdir -p "$PROMETHEUS_MULTIPROC_DIR"
fi

if [[ -z "$ARGOS_CHUNK_TYPE" ]]; then
 export ARGOS_CHUNK_TYPE=MINISBD
fi

exec ./venv/bin/gunicorn -c scripts/gunicorn_conf.py --workers "$LT_THREADS" --max-requests 250 --timeout 2400 --bind "$BIND_ADDR:$LT_PORT" 'wsgi:app()'
