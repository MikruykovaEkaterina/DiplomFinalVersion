# Live log of LibreTranslate: model download, errors. Stop with Ctrl+C.
Set-Location $PSScriptRoot
Write-Host "Streaming: docker compose logs -f libretranslate"
Write-Host "Wait until: no endless IncompleteRead, gunicorn Listening, /languages works in browser."
Write-Host ""
docker compose logs -f libretranslate
