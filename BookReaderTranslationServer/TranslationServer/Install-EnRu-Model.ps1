# Скачивание обоих Argos-пакетов en↔ru для офлайн LibreTranslate (docker-compose монтирует их в /opt).
# Запуск из папки TranslationServer. Без ОБОИХ файлов контейнер не стартует: см. libretranslate-entrypoint.sh.
#
# Ручная загрузка (если скрипт не прошёл): сохраните в ЭТУ папку:
#   translate-en_ru-1_9.argosmodel
#   translate-ru_en-1_9.argosmodel
#
param(
  [switch] $SkipDownload,
  [string] $UrlEnRu = "",
  [string] $UrlRuEn = ""
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$Container = "bookreader-libretranslate"

$ModelSpecs = @(
  @{
    File = "translate-en_ru-1_9.argosmodel"
    DefaultUrls = @(
      "https://argos-net.com/v1/translate-en_ru-1_9.argosmodel",
      "https://huggingface.co/TiberiuCristianLeon/Argostranslate/resolve/50b9550bd4ea6890825218ccf42fd8741b8dc0e1/translate-en_ru-1_9.argosmodel"
    )
  },
  @{
    File = "translate-ru_en-1_9.argosmodel"
    DefaultUrls = @(
      "https://argos-net.com/v1/translate-ru_en-1_9.argosmodel",
      "https://huggingface.co/TiberiuCristianLeon/Argostranslate/resolve/50b9550bd4ea6890825218ccf42fd8741b8dc0e1/translate-ru_en-1_9.argosmodel"
    )
  }
)

function Save-ModelFile {
  param(
    [string]$RelativeName,
    [string[]]$Urls,
    [string]$OverrideUrl
  )
  $localPath = Join-Path $PSScriptRoot $RelativeName
  $urls = if ($OverrideUrl -ne "") { @($OverrideUrl) } else { $Urls }
  $ok = $false
  foreach ($u in $urls) {
    Write-Host "Trying (~150–200 MB): $RelativeName ← $u"
    try {
      if (Test-Path $localPath) { Remove-Item $localPath -Force }
      Invoke-WebRequest -Uri $u -OutFile $localPath -UseBasicParsing -TimeoutSec 7200
      if ((Test-Path $localPath) -and (Get-Item $localPath).Length -gt 10MB) {
        $ok = $true
        Write-Host "Saved: $localPath"
        break
      }
    }
    catch {
      Write-Host "Failed: $($_.Exception.Message)"
    }
  }
  if (-not $ok) {
    throw "Не удалось скачать $RelativeName"
  }
}

if (-not $SkipDownload) {
  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
  $ProgressPreference = 'SilentlyContinue'
  Save-ModelFile -RelativeName $ModelSpecs[0].File -Urls $ModelSpecs[0].DefaultUrls -OverrideUrl $UrlEnRu
  Save-ModelFile -RelativeName $ModelSpecs[1].File -Urls $ModelSpecs[1].DefaultUrls -OverrideUrl $UrlRuEn
}
else {
  foreach ($s in $ModelSpecs) {
    $p = Join-Path $PSScriptRoot $s.File
    if (-not (Test-Path $p)) {
      Write-Error "Файл не найден: $p"
      exit 1
    }
  }
}

Write-Host ""
Write-Host "Оба .argosmodel в папке TranslationServer. Пересоздайте LibreTranslate:"
Write-Host "  docker compose up -d --force-recreate libretranslate"
Write-Host "(или .\Start-TranslationServer.ps1)"
Write-Host ""

# Опционально: если контейнер уже существует — установка вручную через /tmp (compose с монтированием /opt обычно не нужна).
if ((docker inspect -f '{{.State.Status}}' $Container 2>$null) -eq 'running') {
  Write-Host "Копирование в контейнер (запасной путь, если нужен ручной install) ..."
  foreach ($s in $ModelSpecs) {
    $localPath = Join-Path $PSScriptRoot $s.File
    $PathInContainer = "/tmp/$($s.File)"
    docker cp $localPath "${Container}:${PathInContainer}"
    $py = "import argostranslate.package as ap; ap.install_from_path(r'$PathInContainer'); print('OK')"
    & docker exec $Container /app/venv/bin/python -c $py
    if ($LASTEXITCODE -ne 0) {
      Write-Warning "docker exec install failed for $($s.File); после пересоздания контейнера entrypoint установит с /opt"
    }
  }
  Write-Host "Перезапуск libretranslate..."
  docker compose restart libretranslate
}

Write-Host 'Готово. Проверка: http://127.0.0.1:5000/languages  затем .\Test-TranslationApi.ps1'
