using BookReaderApp.Models;
using BookReaderApp.Resources;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using BookReaderApp.Helpers;

namespace BookReaderApp.Services
{
  /// <summary>
  /// Полный пайплайн импорта: копирование файла, обложка, подсчёт символов, запись <see cref="Work"/> и <see cref="Card"/>.
  /// </summary>
  public class BookUploadService : IBookUploadService
  {
    private readonly IDatabaseService _databaseService;
    private readonly IBookParserService _bookParserService;

    /// <summary>Связывает БД и парсер метаданных (ожидается из DI).</summary>
    public BookUploadService(IDatabaseService databaseService, IBookParserService bookParserService)
    {
      _databaseService = databaseService;
      _bookParserService = bookParserService;
    }

    /// <inheritdoc />
    public async Task<UploadResult> UploadAndSaveBookAsync(
      string filePath,
      BookMetadata metadata,
      BookLanguage language,
      IProgress<double>? progress = null,
      CancellationToken cancellationToken = default)
    {
      try
      {
        cancellationToken.ThrowIfCancellationRequested();
        System.Diagnostics.Debug.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        System.Diagnostics.Debug.WriteLine("║                    ЗАГРУЗКА КНИГИ                           ║");
        System.Diagnostics.Debug.WriteLine("╚══════════════════════════════════════════════════════════════╝");

        progress?.Report(0.1); // 10% - начало загрузки

        // 1. Копируем файл
        cancellationToken.ThrowIfCancellationRequested();
        string destinationPath = await CopyBookFileAsync(filePath);
        if (string.IsNullOrEmpty(destinationPath))
        {
          System.Diagnostics.Debug.WriteLine("[Upload] ОШИБКА: Не удалось скопировать файл");
          return new UploadResult { Success = false, ErrorMessage = Strings.Upload_Error_CopyFile };
        }
        System.Diagnostics.Debug.WriteLine($"[Upload] Файл скопирован: {destinationPath}");
        progress?.Report(0.3); // 30% - файл скопирован

        // 2. Подсчитываем символы
        cancellationToken.ThrowIfCancellationRequested();
        long totalChars = await CountCharactersAsync(filePath);
        System.Diagnostics.Debug.WriteLine($"[Upload] Подсчитано символов: {totalChars}");
        progress?.Report(0.5); // 50% - символы подсчитаны

        // 3. Извлекаем и сохраняем обложку
        cancellationToken.ThrowIfCancellationRequested();
        string coverPath = await ExtractAndSaveCoverAsync(filePath);
        System.Diagnostics.Debug.WriteLine($"[Upload] Обложка: {(string.IsNullOrEmpty(coverPath) ? "не найдена" : coverPath)}");
        progress?.Report(0.7); // 70% - обложка извлечена

        // 4. Создаём Work (книга)
        cancellationToken.ThrowIfCancellationRequested();
        var work = new Work
        {
          Reaction = BookReaction.Unrated,
          ReadingStatus = BookStatus.New
        };
        int workId = await _databaseService.SaveWorkAsync(work);
        progress?.Report(0.8); // 80% - Work создан

        // Логируем метаданные Work
        System.Diagnostics.Debug.WriteLine("┌──────────────────────────────────────────────────────────────┐");
        System.Diagnostics.Debug.WriteLine("│                  МЕТАДАННЫЕ КНИГИ (Work)                    │");
        System.Diagnostics.Debug.WriteLine("├──────────────────────────────────────────────────────────────┤");
        System.Diagnostics.Debug.WriteLine($"│ ID: {workId}");
        System.Diagnostics.Debug.WriteLine($"│ Реакция: {work.Reaction}");
        System.Diagnostics.Debug.WriteLine($"│ Статус прочтения: {work.ReadingStatus}");
        System.Diagnostics.Debug.WriteLine("└──────────────────────────────────────────────────────────────┘");

        // 5. Создаём Card (версия книги)
        var card = new Card
        {
          WorkId = workId,
          Title = metadata.Title ?? Strings.Book_NoTitle,
          Author = metadata.Author ?? "",
          Language = BookLanguageStorage.ToStored(language),
          TotalChars = totalChars,
          Description = metadata.Description ?? "",
          FilePath = destinationPath,
          Format = Path.GetExtension(filePath).TrimStart('.').ToUpper(),
          AddedDate = DateTime.Now,
          LastOpened = DateTime.Now,
          CoverPath = coverPath,
          ReadChars = 0
        };
        int cardId = await _databaseService.SaveCardAsync(card);
        var ts = await _databaseService.GetTextSettingsAsync();
        card.EstimatedPageCount = TextReadingLayout.ComputeEstimatedPageCountForCard(card.TotalChars, ts);
        await _databaseService.UpdateCardAsync(card);

        // Логируем метаданные Card
        System.Diagnostics.Debug.WriteLine("┌──────────────────────────────────────────────────────────────┐");
        System.Diagnostics.Debug.WriteLine("│              МЕТАДАННЫЕ ВЕРСИИ КНИГИ (Card)                 │");
        System.Diagnostics.Debug.WriteLine("├──────────────────────────────────────────────────────────────┤");
        System.Diagnostics.Debug.WriteLine($"│ ID карточки: {cardId}");
        System.Diagnostics.Debug.WriteLine($"│ ID произведения (Work): {card.WorkId}");
        System.Diagnostics.Debug.WriteLine($"│ Название: {card.Title}");
        System.Diagnostics.Debug.WriteLine($"│ Автор: {(string.IsNullOrEmpty(card.Author) ? "(пусто)" : card.Author)}");
        System.Diagnostics.Debug.WriteLine($"│ Язык: {card.Language}");
        System.Diagnostics.Debug.WriteLine($"│ Формат: {card.Format}");
        System.Diagnostics.Debug.WriteLine($"│ Общее кол-во символов: {card.TotalChars}");
        System.Diagnostics.Debug.WriteLine($"│ Прочитано символов: {card.ReadChars}");
        System.Diagnostics.Debug.WriteLine($"│ Дата добавления: {card.AddedDate:dd.MM.yyyy HH:mm:ss}");
        System.Diagnostics.Debug.WriteLine($"│ Последнее открытие (версия): {card.LastOpened:dd.MM.yyyy HH:mm:ss}");
        System.Diagnostics.Debug.WriteLine($"│ Путь к файлу: {card.FilePath}");
        System.Diagnostics.Debug.WriteLine($"│ Обложка: {(string.IsNullOrEmpty(card.CoverPath) ? "(нет)" : card.CoverPath)}");
        System.Diagnostics.Debug.WriteLine($"│ Описание: {(string.IsNullOrEmpty(card.Description) ? "(пусто)" : TruncateText(card.Description, 50))}");
        System.Diagnostics.Debug.WriteLine("└──────────────────────────────────────────────────────────────┘");

        // 6. Создаём позицию чтения
        var readingPosition = new ReadingPosition
        {
          CardId = cardId,
          CharacterOffset = 0,
          LastUpdated = DateTime.Now
        };
        await _databaseService.SaveReadingPositionAsync(readingPosition);
        System.Diagnostics.Debug.WriteLine($"[Upload] Позиция чтения создана для Card ID: {cardId}");
        progress?.Report(0.95); // 95% - позиция создана

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(1.0); // 100% - загрузка завершена

        System.Diagnostics.Debug.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        System.Diagnostics.Debug.WriteLine("║                  КНИГА УСПЕШНО ЗАГРУЖЕНА                    ║");
        System.Diagnostics.Debug.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        return new UploadResult { Success = true, CardId = cardId };
      }
      catch (OperationCanceledException)
      {
        throw;
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        System.Diagnostics.Debug.WriteLine("║                    ОШИБКА ЗАГРУЗКИ                          ║");
        System.Diagnostics.Debug.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        System.Diagnostics.Debug.WriteLine($"[Upload] Исключение: {ex.GetType().Name}");
        System.Diagnostics.Debug.WriteLine($"[Upload] Сообщение: {ex.Message}");
        System.Diagnostics.Debug.WriteLine($"[Upload] StackTrace: {ex.StackTrace}");
        return new UploadResult { Success = false, ErrorMessage = ex.Message };
      }
    }

    private string TruncateText(string text, int maxLength)
    {
      if (string.IsNullOrEmpty(text)) return "";
      text = text.Replace("\r", " ").Replace("\n", " ");
      return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "...";
    }

    private async Task<string> CopyBookFileAsync(string sourceFilePath)
    {
      try
      {
        string appDataPath = FileSystem.AppDataDirectory;
        string booksFolder = Path.Combine(appDataPath, "Books");

        if (!Directory.Exists(booksFolder))
          Directory.CreateDirectory(booksFolder);

        string extension = Path.GetExtension(sourceFilePath);
        string fileName = $"{Guid.NewGuid()}{extension}";
        string destinationPath = Path.Combine(booksFolder, fileName);

        await Task.Run(() => File.Copy(sourceFilePath, destinationPath, true));

        return destinationPath;
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[Upload] Ошибка копирования: {ex.Message}");
        return null;
      }
    }

    private async Task<long> CountCharactersAsync(string filePath)
    {
      try
      {
        var extension = Path.GetExtension(filePath).ToLower();

        if (extension == ".fb2")
          return await CountFB2CharactersAsync(filePath);
        else if (extension == ".epub")
          return await CountEPUBCharactersAsync(filePath);

        return 0;
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[Upload] Ошибка подсчёта символов: {ex.Message}");
        return 0;
      }
    }

    private async Task<long> CountFB2CharactersAsync(string filePath)
    {
      try
      {
        // Используем таймаут для предотвращения зависания
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        
        return await Task.Run(async () =>
        {
          try
          {
            // Сначала пробуем через XDocument
            var doc = LoadFB2Document(filePath);
            if (doc != null)
            {
              var ns = XNamespace.Get("http://www.gribuser.ru/xml/fictionbook/2.0");
              var body = doc.Root?.Element(ns + "body");
              if (body != null)
              {
                var text = new StringBuilder();
                foreach (var element in body.Descendants())
                {
                  cts.Token.ThrowIfCancellationRequested();
                  if (!element.HasElements)
                    text.Append(element.Value);
                }

                if (text.Length > 0)
                  return text.Length;
              }
            }

            // Fallback: подсчёт через regex для битых файлов
            System.Diagnostics.Debug.WriteLine("[Upload] Используем regex для подсчёта символов");
            return CountFB2CharactersWithRegex(filePath);
          }
          catch (OperationCanceledException)
          {
            System.Diagnostics.Debug.WriteLine("[Upload] Подсчёт символов отменён по таймауту, используем regex");
            return CountFB2CharactersWithRegex(filePath);
          }
          catch (Exception ex)
          {
            System.Diagnostics.Debug.WriteLine($"[Upload] Ошибка подсчёта символов FB2: {ex.Message}");
            return CountFB2CharactersWithRegex(filePath);
          }
        }, cts.Token);
      }
      catch (OperationCanceledException)
      {
        System.Diagnostics.Debug.WriteLine("[Upload] Подсчёт символов отменён, используем упрощённый метод");
        return CountFB2CharactersWithRegex(filePath);
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[Upload] Ошибка подсчёта символов: {ex.Message}");
        return CountFB2CharactersWithRegex(filePath);
      }
    }

    /// <summary>
    /// Подсчитывает символы в FB2 через regex (для битых файлов)
    /// </summary>
    private long CountFB2CharactersWithRegex(string filePath)
    {
      try
      {
        // Используем асинхронное чтение с таймаутом для больших файлов
        string content;
        using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true))
        using (var reader = new StreamReader(fileStream, Encoding.UTF8))
        {
          content = reader.ReadToEnd();
        }

        // Находим секцию body
        int bodyStart = content.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        int bodyEnd = content.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);

        if (bodyStart < 0 || bodyEnd < 0)
          return 0;

        string bodyContent = content.Substring(bodyStart, bodyEnd - bodyStart + "</body>".Length);

        // Удаляем все XML теги
        string textOnly = System.Text.RegularExpressions.Regex.Replace(bodyContent, "<[^>]+>", "");
        
        // Удаляем лишние пробелы
        textOnly = System.Text.RegularExpressions.Regex.Replace(textOnly, @"\s+", " ").Trim();

        System.Diagnostics.Debug.WriteLine($"[Upload] Regex подсчёт: {textOnly.Length} символов");
        return textOnly.Length;
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[Upload] Ошибка regex подсчёта: {ex.Message}");
        return 0;
      }
    }

    /// <summary>
    /// Загружает FB2 документ, исправляя отсутствующий xlink namespace и обрабатывая битые файлы
    /// </summary>
    private XDocument LoadFB2Document(string filePath)
    {
      try
      {
        // Используем асинхронное чтение для больших файлов
        string xmlContent;
        using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true))
        using (var reader = new StreamReader(fileStream, Encoding.UTF8))
        {
          xmlContent = reader.ReadToEnd();
        }

        // Нормализуем xlink namespace
        xmlContent = NormalizeXlinkNamespace(xmlContent);

        // Сначала пробуем загрузить весь документ
        try
        {
          using (var stringReader = new StringReader(xmlContent))
          {
            return XDocument.Load(stringReader);
          }
        }
        catch
        {
          // Если не получилось - пробуем извлечь только description + binary секции
          System.Diagnostics.Debug.WriteLine("[Upload] Файл с битым XML, извлекаем только нужные секции");
          return LoadFB2DescriptionOnly(xmlContent);
        }
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[Upload] Ошибка загрузки FB2: {ex.Message}");
        return null;
      }
    }

    /// <summary>
    /// Нормализует xlink namespace - разные FB2 файлы используют разные префиксы (xlink:, l: и т.д.)
    /// </summary>
    private string NormalizeXlinkNamespace(string xmlContent)
    {
      // Находим объявление xlink namespace с любым префиксом
      var xlinkMatch = System.Text.RegularExpressions.Regex.Match(
          xmlContent, 
          @"xmlns:(\w+)=[""']http://www\.w3\.org/1999/xlink[""']",
          System.Text.RegularExpressions.RegexOptions.IgnoreCase);

      if (xlinkMatch.Success)
      {
        string oldPrefix = xlinkMatch.Groups[1].Value;
        
        if (oldPrefix != "xlink")
        {
          System.Diagnostics.Debug.WriteLine($"[Upload] Нормализация xlink: '{oldPrefix}:' -> 'xlink:'");
          
          // Заменяем объявление namespace
          xmlContent = xmlContent.Replace(
              $"xmlns:{oldPrefix}=\"http://www.w3.org/1999/xlink\"",
              "xmlns:xlink=\"http://www.w3.org/1999/xlink\"");
          xmlContent = xmlContent.Replace(
              $"xmlns:{oldPrefix}='http://www.w3.org/1999/xlink'",
              "xmlns:xlink='http://www.w3.org/1999/xlink'");
          
          // Заменяем использование префикса
          xmlContent = System.Text.RegularExpressions.Regex.Replace(
              xmlContent,
              $@"\b{oldPrefix}:(\w+)=",
              "xlink:$1=");
        }
      }
      else if (xmlContent.Contains("xlink:") && !xmlContent.Contains("xmlns:xlink"))
      {
        xmlContent = xmlContent.Replace(
            "xmlns=\"http://www.gribuser.ru/xml/fictionbook/2.0\"",
            "xmlns=\"http://www.gribuser.ru/xml/fictionbook/2.0\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"");
      }

      return xmlContent;
    }

    /// <summary>
    /// Загружает только секцию description из FB2 (для битых файлов)
    /// </summary>
    private XDocument LoadFB2DescriptionOnly(string xmlContent)
    {
      try
      {
        // Извлекаем description
        int descStart = xmlContent.IndexOf("<description", StringComparison.OrdinalIgnoreCase);
        int descEnd = xmlContent.IndexOf("</description>", StringComparison.OrdinalIgnoreCase);

        if (descStart < 0 || descEnd < 0)
          return null;

        descEnd += "</description>".Length;
        string description = xmlContent.Substring(descStart, descEnd - descStart);

        // Извлекаем все binary секции (для обложек)
        var binaryBuilder = new StringBuilder();
        int binaryStart = 0;
        while ((binaryStart = xmlContent.IndexOf("<binary", binaryStart, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
          int binaryEnd = xmlContent.IndexOf("</binary>", binaryStart, StringComparison.OrdinalIgnoreCase);
          if (binaryEnd < 0) break;

          binaryEnd += "</binary>".Length;
          binaryBuilder.AppendLine(xmlContent.Substring(binaryStart, binaryEnd - binaryStart));
          binaryStart = binaryEnd;
        }

        // Собираем минимальный валидный FB2
        string wrappedXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<FictionBook xmlns=""http://www.gribuser.ru/xml/fictionbook/2.0"" xmlns:xlink=""http://www.w3.org/1999/xlink"">
{description}
<body><section><p></p></section></body>
{binaryBuilder}
</FictionBook>";

        using (var stringReader = new StringReader(wrappedXml))
        {
          return XDocument.Load(stringReader);
        }
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[Upload] Ошибка извлечения description: {ex.Message}");
        return null;
      }
    }

    private async Task<long> CountEPUBCharactersAsync(string filePath)
    {
      return await Task.Run(() =>
      {
        try
        {
          long totalChars = 0;

          using (var zip = new System.IO.Compression.ZipArchive(File.OpenRead(filePath)))
          {
            foreach (var entry in zip.Entries)
            {
              if (entry.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                  entry.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase))
              {
                using (var stream = entry.Open())
                using (var reader = new StreamReader(stream))
                {
                  var content = reader.ReadToEnd();
                  // Удаляем HTML теги
                  var text = System.Text.RegularExpressions.Regex.Replace(content, "<[^>]*>", "");
                  totalChars += text.Length;
                }
              }
            }
          }

          return totalChars;
        }
        catch
        {
          return 0;
        }
      });
    }

    private async Task<string> ExtractAndSaveCoverAsync(string filePath)
    {
      try
      {
        var extension = Path.GetExtension(filePath).ToLower();
        byte[] coverBytes = null;

        if (extension == ".fb2")
          coverBytes = await ExtractFB2CoverAsync(filePath);
        else if (extension == ".epub")
          coverBytes = await ExtractEPUBCoverAsync(filePath);

        if (coverBytes == null || coverBytes.Length == 0)
          return null;

        // Сохраняем обложку как файл
        string appDataPath = FileSystem.AppDataDirectory;
        string coversFolder = Path.Combine(appDataPath, "Covers");

        if (!Directory.Exists(coversFolder))
          Directory.CreateDirectory(coversFolder);

        string coverFileName = $"{Guid.NewGuid()}.jpg";
        string coverPath = Path.Combine(coversFolder, coverFileName);

        await File.WriteAllBytesAsync(coverPath, coverBytes);

        return coverPath;
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[Upload] Ошибка извлечения обложки: {ex.Message}");
        return null;
      }
    }

    private async Task<byte[]> ExtractFB2CoverAsync(string filePath)
    {
      return await Task.Run(() =>
      {
        try
        {
          var doc = LoadFB2Document(filePath);
          if (doc == null) return null;

          var ns = XNamespace.Get("http://www.gribuser.ru/xml/fictionbook/2.0");
          var xlinkNs = XNamespace.Get("http://www.w3.org/1999/xlink");

          // Ищем coverpage
          var coverpage = doc.Root?.Element(ns + "description")
              ?.Element(ns + "title-info")
              ?.Element(ns + "coverpage");

          if (coverpage != null)
          {
            var imageElement = coverpage.Element(ns + "image");
            if (imageElement != null)
            {
              var hrefAttr = imageElement.Attribute(xlinkNs + "href");
              if (hrefAttr != null)
              {
                var imageId = hrefAttr.Value.TrimStart('#');
                var binary = doc.Root?.Elements(ns + "binary")
                    .FirstOrDefault(b => b.Attribute("id")?.Value == imageId);

                if (binary != null)
                {
                  System.Diagnostics.Debug.WriteLine($"[Upload] Найдена обложка: {imageId}");
                  return Convert.FromBase64String(binary.Value);
                }
              }
            }
          }

          return null;
        }
        catch (Exception ex)
        {
          System.Diagnostics.Debug.WriteLine($"[Upload] Ошибка извлечения обложки FB2: {ex.Message}");
          return null;
        }
      });
    }

    private async Task<byte[]> ExtractEPUBCoverAsync(string filePath)
    {
      return await Task.Run(() =>
      {
        try
        {
          using (var zip = new System.IO.Compression.ZipArchive(File.OpenRead(filePath)))
          {
            // Ищем обложку по типичным именам
            var coverNames = new[] { "cover.jpg", "cover.jpeg", "cover.png", "cover.gif" };

            foreach (var entry in zip.Entries)
            {
              var entryName = Path.GetFileName(entry.FullName).ToLower();
              if (coverNames.Contains(entryName) ||
                  entryName.Contains("cover") && IsImageFile(entryName))
              {
                using (var stream = entry.Open())
                using (var ms = new MemoryStream())
                {
                  stream.CopyTo(ms);
                  return ms.ToArray();
                }
              }
            }
          }

          return null;
        }
        catch
        {
          return null;
        }
      });
    }

    /// <inheritdoc />
    public async Task<UploadResult> ImportTranslatedBookVersionAsync(
        int workId,
        string translatedFilePath,
        BookLanguage targetLanguage,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
      try
      {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(translatedFilePath) || !File.Exists(translatedFilePath))
          return new UploadResult { Success = false, ErrorMessage = Strings.Upload_Error_TranslationFileNotFound };

        if (await _databaseService.GetWorkByIdAsync(workId).ConfigureAwait(false) == null)
          return new UploadResult { Success = false, ErrorMessage = Strings.Upload_Error_TargetBookNotFound };

        progress?.Report(0.15);
        cancellationToken.ThrowIfCancellationRequested();
        var destPath = await CopyBookFilePreservingExtensionAsync(translatedFilePath).ConfigureAwait(false);
        if (string.IsNullOrEmpty(destPath))
          return new UploadResult { Success = false, ErrorMessage = Strings.Upload_Error_SaveFile };

        var meta = _bookParserService.ParseBookMetadata(destPath);
        var siblings = await _databaseService.GetCardsByWorkIdAsync(workId).ConfigureAwait(false);
        var template = siblings.FirstOrDefault();

        progress?.Report(0.45);
        long totalChars = await CountCharactersWithFallbackAsync(destPath).ConfigureAwait(false);
        string coverPath = await ExtractAndSaveCoverAsync(destPath).ConfigureAwait(false);

        string title = meta?.Title?.Trim();
        if (string.IsNullOrEmpty(title))
          title = template?.Title ?? Strings.Book_NoTitle;
        string author = meta?.Author?.Trim() ?? "";
        string description = meta?.Description?.Trim() ?? "";
        string langIso = BookLanguageStorage.ToStored(targetLanguage);
        string format = DetectFormatLabelFromPath(destPath);

        var card = new Card
        {
          WorkId = workId,
          Title = title,
          Author = author,
          Language = langIso,
          TotalChars = totalChars,
          Description = description,
          FilePath = destPath,
          Format = format,
          AddedDate = DateTime.Now,
          LastOpened = DateTime.Now,
          CoverPath = coverPath ?? "",
          ReadChars = 0
        };
        int cardId = await _databaseService.SaveCardAsync(card).ConfigureAwait(false);
        var ts = await _databaseService.GetTextSettingsAsync().ConfigureAwait(false);
        card.EstimatedPageCount = TextReadingLayout.ComputeEstimatedPageCountForCard(card.TotalChars, ts);
        await _databaseService.UpdateCardAsync(card).ConfigureAwait(false);

        var readingPosition = new ReadingPosition
        {
          CardId = cardId,
          CharacterOffset = 0,
          LastUpdated = DateTime.Now
        };
        await _databaseService.SaveReadingPositionAsync(readingPosition).ConfigureAwait(false);

        progress?.Report(1);
        return new UploadResult { Success = true, CardId = cardId };
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[Upload] ImportTranslatedBookVersionAsync: {ex}");
        return new UploadResult { Success = false, ErrorMessage = ex.Message };
      }
    }

    static string DetectFormatLabelFromPath(string path)
    {
      var p = path.ToLowerInvariant();
      if (p.Contains(".epub"))
        return "EPUB";
      return "FB2";
    }

    async Task<long> CountCharactersWithFallbackAsync(string destPath)
    {
      long n = await CountCharactersAsync(destPath).ConfigureAwait(false);
      if (n > 0)
        return n;
      var plain = BookPlainTextLoader.TryLoadPlainText(destPath, null);
      return plain?.Length ?? 0;
    }

    async Task<string?> CopyBookFilePreservingExtensionAsync(string sourceFilePath)
    {
      try
      {
        string appDataPath = FileSystem.AppDataDirectory;
        string booksFolder = Path.Combine(appDataPath, "Books");
        if (!Directory.Exists(booksFolder))
          Directory.CreateDirectory(booksFolder);

        string extension = InferCompoundBookExtension(sourceFilePath);
        string fileName = $"{Guid.NewGuid()}{extension}";
        string destinationPath = Path.Combine(booksFolder, fileName);
        await Task.Run(() => File.Copy(sourceFilePath, destinationPath, true)).ConfigureAwait(false);
        return destinationPath;
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[Upload] CopyBookFilePreservingExtensionAsync: {ex.Message}");
        return null;
      }
    }

    static string InferCompoundBookExtension(string path)
    {
      var p = path.ToLowerInvariant();
      if (p.EndsWith(".fb2.zip", StringComparison.Ordinal))
        return ".fb2.zip";
      if (p.EndsWith(".epub.zip", StringComparison.Ordinal))
        return ".epub.zip";
      return Path.GetExtension(path);
    }

    private bool IsImageFile(string fileName)
    {
      var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
      return imageExtensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }
  }
}
