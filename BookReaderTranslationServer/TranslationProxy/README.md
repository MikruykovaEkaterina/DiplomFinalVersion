# TranslationProxy — HTTP API для перевода предложений

Проксирует запросы приложения `POST /api/translation/sentence` в [LibreTranslate](https://libretranslate.com/) (`POST /translate`).

---

## С нуля: что скачать и куда поставить (отдельный ПК-сервер)

### 1. Что такое LibreTranslate и где его «взять»

- Официальный сайт: [https://libretranslate.com](https://libretranslate.com) (это демо; свой сервер вы поднимаете сами).
- **Скачивать исходники не обязательно.** Практичный путь — **Docker-образ** с [Docker Hub: `libretranslate/libretranslate`](https://hub.docker.com/r/libretranslate/libretranslate): образ сам подтянется при первом `docker run` (нужен установленный Docker и интернет).
- Альтернатива без Docker: Python + `pip install libretranslate` и запуск из venv — дольше и капризнее; для «отдельного сервера» Docker обычно проще.

### 2. Что поставить на компьютер, который будет сервером

1. **Docker**
   - **Windows:** [Docker Desktop](https://docs.docker.com/desktop/) (при необходимости включите WSL2, как просит установщик).
   - **Linux:** пакет **Docker Engine** по официальной инструкции дистрибутива.
2. **.NET 9 SDK** (чтобы собрать и запустить этот прокси) — [скачать](https://dotnet.microsoft.com/download/dotnet/9.0).  
   Либо один раз собрать на другой машине и скопировать папку после `dotnet publish` (см. ниже).

### 3. Запустить LibreTranslate на этом ПК

В терминале на сервере (после установки Docker):

```bash
docker run -d --name libretranslate --restart unless-stopped -p 5000:5000 libretranslate/libretranslate
```

- Первый запуск может долго качать **модели** (несколько ГБ), это нормально.
- Порт **5000** — стандарт LibreTranslate внутри контейнера; снаружи тоже 5000 (`-p 5000:5000`).

Проверка с этого же ПК:

```bash
curl http://127.0.0.1:5000/languages
```

Должен вернуться JSON со списком языков.

### 4. «Написать сервер» — уже написан: проект TranslationProxy

В репозитории это папка `TranslationProxy` — минимальный ASP.NET Core API, один маршрут `POST /api/translation/sentence`, внутри запрос к LibreTranslate `POST /translate`.

На сервере:

1. Скопируйте папку `TranslationProxy` (или весь репозиторий).
2. Убедитесь, что в `appsettings.json` указано:

   ```json
   "LibreTranslate": { "BaseUrl": "http://127.0.0.1:5000" }
   ```

   Если LibreTranslate в Docker на **этом же** ПК — так и оставляете `127.0.0.1:5000`. Если LibreTranslate на **другой** машине — подставьте её IP, например `http://192.168.1.5:5000`.

3. Запуск прокси (слушать все интерфейсы, чтобы дошли телефоны в Wi‑Fi):

   ```bash
   cd TranslationProxy
   dotnet run --urls "http://0.0.0.0:8080"
   ```

   Тогда адрес для телефона: `http://<IP_этого_ПК>:8080` (не порт 5000 — это только LibreTranslate).

Публикация без SDK на сервере (собрали на другой машине с .NET 9):

```bash
dotnet publish -c Release -o ./out
```

Скопируйте папку `out` на сервер и запускайте `TranslationProxy.exe` (Windows) или `dotnet TranslationProxy.dll` с `--urls "http://0.0.0.0:8080"`.

### 5. Сеть и брандмауэр

- Узнайте LAN-IP сервера: Windows — `ipconfig`, Linux — `ip a` или `hostname -I`.
- Разрешите **входящие** TCP на порты, которые слушаете снаружи:
  - **8080** (или ваш порт прокси) — обязательно для телефона;
  - **5000** — только если хотите ходить к LibreTranslate напрямую с других устройств (для BookReader это не нужно, достаточно прокси на 8080).
- Телефон и ПК должны быть в **одной Wi‑Fi** сети (или VPN), если не настраиваете проброс из интернета.

### 6. Что указать в приложении BookReader

По умолчанию приложение берёт базовый URL **прокси** (не LibreTranslate) как `http://bookreader-translation.test:8080` — константа `TranslationServerConfig.DefaultApiBaseUrl` (`Services/TranslationServerConfig.cs`). Имя `bookreader-translation.test` (зона RFC 6761 для тестов) сопоставьте с LAN-IP **этого** ПК: в `hosts` на компьютере разработки или в локальной DNS на роутере (удобнее для телефонов без root). Без своего имени можно задать `http://<IP>:8080` через настройки приложения или Preferences.

Разрешён `http://` в LAN (Android cleartext, на iOS в Info.plist добавлено ATS-исключение для этого хоста). Для интернета лучше HTTPS (nginx/Caddy перед Kestrel).

### 7. Быстрая проверка прокси с телефона или ПК

Подставьте IP сервера:

```bash
curl -X POST http://192.168.1.10:8080/api/translation/sentence -H "Content-Type: application/json" -d "{\"text\":\"Hello\",\"sourceLanguage\":\"en\",\"targetLanguage\":\"ru\"}"
```

Ожидается JSON с `translatedText`.

---

## Запуск локально

1. Поднимите LibreTranslate (Docker на отдельном ПК или локально):

   ```bash
   docker run -d --name libretranslate -p 5000:5000 libretranslate/libretranslate
   ```

2. Убедитесь, что в `appsettings.json` указан URL LibreTranslate (по умолчанию `http://127.0.0.1:5000`).

3. Запуск прокси:

   ```bash
   dotnet run --project TranslationProxy.csproj
   ```

   По умолчанию Kestrel слушает `http://localhost:5001` и `https://localhost:5002` (см. `Properties/launchSettings.json`). Для доступа с телефона в LAN задайте `applicationUrl` в `launchSettings.json` или используйте:

   ```bash
   dotnet run --urls "http://0.0.0.0:8080"
   ```

4. Приложение BookReader по умолчанию использует `http://bookreader-translation.test:8080` (задайте разрешение имени через hosts/`Urls`/`0.0.0.0:8080` на сервере); либо укажите `http://<LAN-IP>:8080` в настройках приложения.

## Один запуск в Docker (LibreTranslate только en/ru + прокси)

Стек размещается в **`D:\BookReaderTranslationServer`**: рядом лежат **`TranslationServer`** (`docker-compose.yml`, модели, скрипты) и эта папка **`TranslationProxy`**. Compose собирает образ прокси из `../TranslationProxy` (порт **8080** наружу).

```bash
cd D:/BookReaderTranslationServer/TranslationServer
docker compose up -d --build
```

Или **`Start-TranslationServer.bat`** в `D:\BookReaderTranslationServer`.

Прокси внутри сети compose ходит в LibreTranslate по `http://libretranslate:5000`. На телефоне в той же Wi‑Fi укажите `http://<LAN-IP-ПК>:8080`.

Альтернатива без Docker для прокси: только LibreTranslate в Docker, прокси — `dotnet run` (см. выше).

## Контракт

- Запрос: `POST /api/translation/sentence`, JSON `{ "text", "sourceLanguage", "targetLanguage" }` (коды ISO 639-1).
- Успех: `{ "translatedText": "..." }`.
- Ошибка: `{ "error", "message" }`.

## Сеть и безопасность

- На ПК с Docker откройте порт в брандмауэре.
- Для интернета используйте HTTPS (nginx/Caddy перед Kestrel).
- При необходимости включите API-ключи LibreTranslate (`--api-keys`) и добавьте проверку в этом прокси.
