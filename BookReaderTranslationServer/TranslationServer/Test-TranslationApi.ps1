# Test translation proxy from THIS PC (Docker must be running).
# 127.0.0.1 = this computer. Phone uses LAN IP in TranslationServerConfig.
# Optional: $env:TRANSLATION_TEST_URL = "http://192.168.x.x:8080"

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$base = if ($env:TRANSLATION_TEST_URL) { $env:TRANSLATION_TEST_URL.TrimEnd("/") } else { "http://127.0.0.1:8080" }
$uri = "$base/api/translation/sentence"
$json = '{"text":"Hello world","sourceLanguage":"en","targetLanguage":"ru"}'

Write-Host "POST $uri"
Write-Host ""

# Quick check: is anything listening on port 8080?
try {
  $u = [Uri]$base
  $port = if ($u.IsDefaultPort) { 80 } else { $u.Port }
  $t = Test-NetConnection -ComputerName $u.Host -Port $port -WarningAction SilentlyContinue
  if (-not $t.TcpTestSucceeded) {
    Write-Host "Port $port on $($u.Host) is not reachable. Start stack first:"
    Write-Host "  docker compose up -d"
    Write-Host "  (in this TranslationServer folder)"
    Write-Host ""
  }
} catch { }

try {
  Add-Type -AssemblyName System.Net.Http
  $handler = New-Object System.Net.Http.HttpClientHandler
  $c = New-Object System.Net.Http.HttpClient($handler)
  $c.Timeout = [TimeSpan]::FromSeconds(180)
  $content = New-Object System.Net.Http.StringContent($json, [Text.Encoding]::UTF8, "application/json")
  $task = $c.PostAsync($uri, $content)
  $response = $task.GetAwaiter().GetResult()
  $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
  $code = [int]$response.StatusCode
  Write-Host "HTTP $code"
  if ([string]::IsNullOrWhiteSpace($body)) {
    Write-Host "(empty body)"
  } else {
    Write-Host $body
  }
  if ($code -ge 400) {
    Write-Host ""
    Write-Host "If message mentions InternalError: docker logs bookreader-translation-proxy"
    Write-Host "Check LibreTranslate: open http://127.0.0.1:5000/languages in browser"
  }
}
catch {
  Write-Host "REQUEST FAILED:"
  Write-Host $_.Exception.Message
  $x = $_.Exception
  while ($null -ne $x.InnerException) {
    $x = $x.InnerException
    Write-Host "  -> $($x.Message)"
  }
  Write-Host ""
  Write-Host "Is Docker running? Run: docker ps"
}
