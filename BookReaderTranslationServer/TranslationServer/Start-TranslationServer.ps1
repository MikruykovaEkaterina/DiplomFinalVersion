# Запуск сервера перевода (Docker Compose) из папки TranslationServer.
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Error "Docker не найден в PATH. Установите Docker Desktop и перезапустите терминал."
}

$models = @(
    (Join-Path $here "translate-en_ru-1_9.argosmodel"),
    (Join-Path $here "translate-ru_en-1_9.argosmodel")
)
foreach ($m in $models) {
    if (-not (Test-Path $m)) {
        Write-Error "Нет файла модели: $m. Нужны оба направления en↔ru (см. docker-compose.yml). Скачайте: .\Install-EnRu-Model.ps1"
    }
}

docker compose up -d --build
Write-Host ""
Write-Host "Готово. Прокси: http://localhost:8080  (с телефона — http://<ваш_LAN_IP>:8080)"
Write-Host "Проверка: Invoke-RestMethod -Method Post -Uri http://localhost:8080/api/translation/sentence -ContentType 'application/json' -Body '{\"text\":\"Hello\",\"sourceLanguage\":\"en\",\"targetLanguage\":\"ru\"}'"
