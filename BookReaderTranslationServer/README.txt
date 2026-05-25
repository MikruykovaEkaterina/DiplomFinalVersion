BookReader — сервер перевода (LibreTranslate: только en и ru + TranslationProxy).

Расположение на этом ПК:
  D:\BookReaderTranslationServer
    TranslationServer   — docker-compose, модели Argos (.argosmodel), скрипты PowerShell
    TranslationProxy    — ASP.NET прокси (сборка Docker берёт контекст из соседней папки)

Запуск: дважды Start-TranslationServer.bat (нужны Docker Desktop и оба файла моделей в TranslationServer).
Остановка: Stop-TranslationServer.bat

Или из PowerShell:
  cd D:\BookReaderTranslationServer\TranslationServer
  .\Start-TranslationServer.ps1

В приложении BookReader базовый URL прокси (не LibreTranslate) по умолчанию совпадает с этим именем хоста:
  http://bookreader-translation.test:8080
  На ПК разработки в файле hosts (Windows: ...\drivers\etc\hosts) добавьте строку:
    <IPv4 этого ПК в Wi‑Fi>  bookreader-translation.test   (IPv4 см. ipconfig)
  На реальных телефонах в LAN проще добавить ту же запись на роутере (локальный DNS), т.к. системный hosts на Android/iOS обычно недоступен.
  Альтернатива без DNS: переопределить URL в настройках приложения (Preferences), например http://192.168.x.x:8080.

Отдельная отладка TranslationProxy без compose:
  dotnet run --project D:\BookReaderTranslationServer\TranslationProxy\TranslationProxy.csproj

Задачи перевода КНИГИ (multipart /api/translation/book/*) сохраняются в SQLite translation-jobs.db рядом с TranslationProxy.dll
  и в папке translation-jobs\<JobId>. Переводы ПРЕДЛОЖЕНИЙ (POST /sentence) в БД не пишутся.
  После срока ResultTtlHours в appsettings.json с диска удаляется вся папка задачи (исходник + результат).
