using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BookReaderApp.Models;
using BookReaderApp.Resources;

namespace BookReaderApp.Services
{
  /// <summary>Чтение метаданных из FB2, EPUB и ZIP-обёрток без сохранения в БД.</summary>
  public interface IBookParserService
  {
    /// <summary>Заголовок, автор, описание и язык по пути к файлу; null при ошибке или неподдерживаемом формате.</summary>
    BookMetadata ParseBookMetadata(string filePath);
  }

  /// <summary>Реализация <see cref="IBookParserService"/> с XML/regex для FB2 и EPUB.</summary>
  public class BookParserService : IBookParserService
  {
    /// <inheritdoc />
    public BookMetadata ParseBookMetadata(string filePath)
    {
      if (string.IsNullOrEmpty(filePath))
        return null;

      try
      {
        var lower = filePath.ToLowerInvariant();
        if (lower.EndsWith(".fb2.zip", StringComparison.Ordinal))
          return ParseFB2Zip(filePath);
        if (lower.EndsWith(".epub.zip", StringComparison.Ordinal))
          return ParseEpubZip(filePath);

        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        if (extension == ".fb2")
          return ParseFB2(filePath);
        if (extension == ".epub")
          return ParseEPUB(filePath);
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"Error parsing book: {ex.Message}");
      }

      return null;
    }

    BookMetadata ParseFB2Zip(string zipPath)
    {
      try
      {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = BookZipEntryHelper.FindPrimaryFb2Entry(zip);
        if (entry == null)
        {
          System.Diagnostics.Debug.WriteLine("[FB2 Parser] В архиве нет .fb2");
          return null;
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string xml = reader.ReadToEnd();
        return ParseFB2FromXml(xml, entry.FullName);
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[FB2 Parser] Ошибка .fb2.zip: {ex.Message}");
        return null;
      }
    }

    BookMetadata ParseEpubZip(string zipPath)
    {
      string? tmp = null;
      try
      {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = BookZipEntryHelper.FindPrimaryEpubEntry(zip);
        if (entry == null)
        {
          System.Diagnostics.Debug.WriteLine("[EPUB Parser] В архиве нет .epub");
          return null;
        }

        tmp = Path.Combine(Path.GetTempPath(), $"br-epub-meta-{Guid.NewGuid():N}.epub");
        using (var fs = File.Create(tmp))
        using (var inp = entry.Open())
          inp.CopyTo(fs);
        return ParseEPUB(tmp);
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[EPUB Parser] Ошибка .epub.zip: {ex.Message}");
        return null;
      }
      finally
      {
        if (tmp != null)
        {
          try
          {
            File.Delete(tmp);
          }
          catch { }
        }
      }
    }

    private BookMetadata ParseFB2(string filePath)
    {
      try
      {
        System.Diagnostics.Debug.WriteLine($"[FB2 Parser] Загрузка файла: {filePath}");
        string xmlContent = File.ReadAllText(filePath);
        return ParseFB2FromXml(xmlContent, filePath);
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[FB2 Parser] ОШИБКА: {ex.Message}");
        System.Diagnostics.Debug.WriteLine($"[FB2 Parser] StackTrace: {ex.StackTrace}");
        return new BookMetadata();
      }
    }

    BookMetadata ParseFB2FromXml(string xmlContent, string? debugLabel = null)
    {
      var metadata = new BookMetadata();

      try
      {
        if (!string.IsNullOrEmpty(debugLabel))
          System.Diagnostics.Debug.WriteLine($"[FB2 Parser] Источник XML: {debugLabel}");

        xmlContent = NormalizeXlinkNamespace(xmlContent);

        string descriptionXml = ExtractDescriptionSection(xmlContent);
        if (string.IsNullOrEmpty(descriptionXml))
        {
          System.Diagnostics.Debug.WriteLine("[FB2 Parser] Не удалось извлечь секцию description — regex");
          return ParseFB2WithRegex(xmlContent);
        }

        XDocument doc;
        try
        {
          using (var stringReader = new StringReader(descriptionXml))
            doc = XDocument.Load(stringReader);
        }
        catch (Exception parseEx)
        {
          System.Diagnostics.Debug.WriteLine($"[FB2 Parser] Ошибка парсинга description: {parseEx.Message}");
          return ParseFB2WithRegex(xmlContent);
        }

        var ns = XNamespace.Get("http://www.gribuser.ru/xml/fictionbook/2.0");

        var titleInfo = doc.Root?.Element(ns + "description")?.Element(ns + "title-info");
        if (titleInfo == null)
        {
          System.Diagnostics.Debug.WriteLine("[FB2 Parser] ОШИБКА: title-info не найден — regex");
          return ParseFB2WithRegex(xmlContent);
        }

        metadata.Title = titleInfo.Element(ns + "book-title")?.Value?.Trim() ?? Strings.Book_NoTitle;
        System.Diagnostics.Debug.WriteLine($"[FB2 Parser] Название: {metadata.Title}");

        var authorElements = titleInfo.Elements(ns + "author").ToList();
        if (authorElements.Count > 0)
        {
          var authorParts = new System.Collections.Generic.List<string>();
          foreach (var authorElement in authorElements)
          {
            var firstName = authorElement.Element(ns + "first-name")?.Value?.Trim();
            var middleName = authorElement.Element(ns + "middle-name")?.Value?.Trim();
            var lastName = authorElement.Element(ns + "last-name")?.Value?.Trim();
            var nickname = authorElement.Element(ns + "nickname")?.Value?.Trim();
            var username = authorElement.Element(ns + "username")?.Value?.Trim();

            string one;
            if (!string.IsNullOrEmpty(nickname))
              one = nickname;
            else if (!string.IsNullOrEmpty(username))
              one = username;
            else
            {
              var nameParts = new[] { firstName, middleName, lastName }
                  .Where(s => !string.IsNullOrEmpty(s))
                  .ToList();
              one = nameParts.Count > 0 ? string.Join(" ", nameParts) : "";
            }
            if (!string.IsNullOrEmpty(one))
              authorParts.Add(one);
          }
          metadata.Author = string.Join(", ", authorParts);
        }
        System.Diagnostics.Debug.WriteLine($"[FB2 Parser] Автор: {(string.IsNullOrEmpty(metadata.Author) ? "(пусто)" : metadata.Author)}");

        var annotation = titleInfo.Element(ns + "annotation");
        if (annotation != null)
          metadata.Description = ExtractTextFromAnnotation(annotation, ns);
        System.Diagnostics.Debug.WriteLine($"[FB2 Parser] Описание: {(string.IsNullOrEmpty(metadata.Description) ? "(пусто)" : metadata.Description.Substring(0, Math.Min(100, metadata.Description.Length)) + "...")}");

        metadata.Language = titleInfo.Element(ns + "lang")?.Value?.Trim() ?? "ru";
        System.Diagnostics.Debug.WriteLine($"[FB2 Parser] Язык: {metadata.Language}");

        System.Diagnostics.Debug.WriteLine("[FB2 Parser] Парсинг завершён успешно");
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[FB2 Parser] ОШИБКА: {ex.Message}");
        System.Diagnostics.Debug.WriteLine($"[FB2 Parser] StackTrace: {ex.StackTrace}");
      }

      return metadata;
    }

    /// <summary>
    /// Нормализует xlink namespace в FB2 файле.
    /// Разные файлы используют разные префиксы: xmlns:xlink, xmlns:l и т.д.
    /// Приводим все к единому формату xmlns:xlink
    /// </summary>
    private string NormalizeXlinkNamespace(string xmlContent)
    {
      // Находим объявление xlink namespace с любым префиксом
      // Пример: xmlns:l="http://www.w3.org/1999/xlink"
      var xlinkMatch = System.Text.RegularExpressions.Regex.Match(
          xmlContent, 
          @"xmlns:(\w+)=[""']http://www\.w3\.org/1999/xlink[""']",
          System.Text.RegularExpressions.RegexOptions.IgnoreCase);

      if (xlinkMatch.Success)
      {
        string oldPrefix = xlinkMatch.Groups[1].Value;
        
        if (oldPrefix != "xlink")
        {
          System.Diagnostics.Debug.WriteLine($"[FB2 Parser] Нормализация xlink: '{oldPrefix}:' -> 'xlink:'");
          
          // Заменяем объявление namespace
          xmlContent = xmlContent.Replace(
              $"xmlns:{oldPrefix}=\"http://www.w3.org/1999/xlink\"",
              "xmlns:xlink=\"http://www.w3.org/1999/xlink\"");
          xmlContent = xmlContent.Replace(
              $"xmlns:{oldPrefix}='http://www.w3.org/1999/xlink'",
              "xmlns:xlink='http://www.w3.org/1999/xlink'");
          
          // Заменяем использование префикса в атрибутах (например l:href -> xlink:href)
          xmlContent = System.Text.RegularExpressions.Regex.Replace(
              xmlContent,
              $@"\b{oldPrefix}:(\w+)=",
              "xlink:$1=");
        }
      }
      else if (xmlContent.Contains("xlink:") && !xmlContent.Contains("xmlns:xlink"))
      {
        // xlink используется, но не объявлен - добавляем объявление
        xmlContent = xmlContent.Replace(
            "xmlns=\"http://www.gribuser.ru/xml/fictionbook/2.0\"",
            "xmlns=\"http://www.gribuser.ru/xml/fictionbook/2.0\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"");
        
        System.Diagnostics.Debug.WriteLine("[FB2 Parser] Добавлен отсутствующий xlink namespace");
      }

      return xmlContent;
    }

    /// <summary>
    /// Извлекает секцию description из FB2 файла (без парсинга всего документа)
    /// </summary>
    private string ExtractDescriptionSection(string xmlContent)
    {
      try
      {
        int descStart = xmlContent.IndexOf("<description", StringComparison.OrdinalIgnoreCase);
        if (descStart < 0)
          return null;

        int bodyStart = xmlContent.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        int endSearchAt = xmlContent.Length - 1;
        if (bodyStart > descStart)
        {
          int beforeBody = bodyStart - 1;
          if (beforeBody >= descStart)
            endSearchAt = beforeBody;
        }

        int descEnd = xmlContent.LastIndexOf("</description>", endSearchAt, StringComparison.OrdinalIgnoreCase);
        if (descEnd < 0 || descEnd < descStart)
        {
          descEnd = xmlContent.IndexOf("</description>", descStart, StringComparison.OrdinalIgnoreCase);
          if (descEnd < 0)
            return null;
        }

        descEnd += "</description>".Length;
        string description = xmlContent.Substring(descStart, descEnd - descStart);

        string wrappedXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<FictionBook xmlns=""http://www.gribuser.ru/xml/fictionbook/2.0"" xmlns:xlink=""http://www.w3.org/1999/xlink"">
{description}
</FictionBook>";

        return wrappedXml;
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[FB2 Parser] Ошибка извлечения description: {ex.Message}");
        return null;
      }
    }

    /// <summary>
    /// Парсит FB2 метаданные с помощью регулярных выражений (fallback для битых файлов)
    /// </summary>
    private BookMetadata ParseFB2WithRegex(string xmlContent)
    {
      var metadata = new BookMetadata();

      try
      {
        System.Diagnostics.Debug.WriteLine("[FB2 Parser] Используем regex-парсинг для битого файла");

        // Название
        var titleMatch = System.Text.RegularExpressions.Regex.Match(
            xmlContent, @"<book-title>([^<]+)</book-title>", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (titleMatch.Success)
          metadata.Title = titleMatch.Groups[1].Value.Trim();

        // Автор - пробуем разные варианты
        var authorMatch = System.Text.RegularExpressions.Regex.Match(
            xmlContent, @"<first-name>([^<]+)</first-name>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (authorMatch.Success)
          metadata.Author = authorMatch.Groups[1].Value.Trim();

        var lastNameMatch = System.Text.RegularExpressions.Regex.Match(
            xmlContent, @"<last-name>([^<]+)</last-name>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (lastNameMatch.Success && !string.IsNullOrEmpty(metadata.Author))
          metadata.Author += " " + lastNameMatch.Groups[1].Value.Trim();

        // Если нет first-name, пробуем username или nickname
        if (string.IsNullOrEmpty(metadata.Author))
        {
          var usernameMatch = System.Text.RegularExpressions.Regex.Match(
              xmlContent, @"<username>([^<]+)</username>",
              System.Text.RegularExpressions.RegexOptions.IgnoreCase);
          if (usernameMatch.Success)
            metadata.Author = usernameMatch.Groups[1].Value.Trim();
        }

        if (string.IsNullOrEmpty(metadata.Author))
        {
          var nicknameMatch = System.Text.RegularExpressions.Regex.Match(
              xmlContent, @"<nickname>([^<]+)</nickname>",
              System.Text.RegularExpressions.RegexOptions.IgnoreCase);
          if (nicknameMatch.Success)
            metadata.Author = nicknameMatch.Groups[1].Value.Trim();
        }

        // Язык
        var langMatch = System.Text.RegularExpressions.Regex.Match(
            xmlContent, @"<lang>([^<]+)</lang>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (langMatch.Success)
          metadata.Language = langMatch.Groups[1].Value.Trim();

        var annMatch = System.Text.RegularExpressions.Regex.Match(
            xmlContent, @"<annotation[^>]*>([\s\S]*?)</annotation>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (annMatch.Success)
        {
          var raw = annMatch.Groups[1].Value;
          var stripped = System.Text.RegularExpressions.Regex.Replace(raw, @"<[^>]+>", " ");
          metadata.Description = System.Text.RegularExpressions.Regex.Replace(stripped, @"\s+", " ").Trim();
        }

        System.Diagnostics.Debug.WriteLine($"[FB2 Parser] Regex результат - Название: {metadata.Title}, Автор: {metadata.Author}");
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[FB2 Parser] Ошибка regex-парсинга: {ex.Message}");
      }

      return metadata;
    }

    /// <summary>
    /// Извлекает чистый текст из annotation, удаляя HTML-подобные теги
    /// </summary>
    private string ExtractTextFromAnnotation(XElement annotation, XNamespace ns)
    {
      var sb = new StringBuilder();

      // Ищем теги <p> с описанием (пропускаем служебную информацию)
      var paragraphs = annotation.Elements(ns + "p");
      bool foundDescription = false;

      foreach (var p in paragraphs)
      {
        var text = p.Value?.Trim() ?? "";

        // Пропускаем служебные строки (содержат метаданные)
        if (text.StartsWith("Направленность:") ||
            text.StartsWith("Автор:") ||
            text.StartsWith("Переводчик:") ||
            text.StartsWith("Фэндом:") ||
            text.StartsWith("Пэйринг") ||
            text.StartsWith("Рейтинг:") ||
            text.StartsWith("Размер:") ||
            text.StartsWith("Кол-во частей:") ||
            text.StartsWith("Статус:") ||
            text.StartsWith("Метки:") ||
            text.StartsWith("Публикация") ||
            text.StartsWith("Посвящение:") ||
            text.StartsWith("Примечания:") ||
            text.StartsWith("Оригинальный текст:"))
          continue;

        // Начинаем собирать после метки описания (RU/EN)
        if (text.StartsWith("Описание:") ||
            text.StartsWith("Description:", StringComparison.OrdinalIgnoreCase))
        {
          foundDescription = true;
          continue;
        }

        // Если нашли раздел описания или это похоже на обычный текст описания
        if (foundDescription || (!text.Contains(':') && text.Length > 20))
        {
          if (sb.Length > 0)
            sb.Append(" ");
          sb.Append(text);
        }
      }

      if (sb.Length == 0 && !paragraphs.Any())
      {
        var fromNodes = string.Join(" ", annotation.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
        if (!string.IsNullOrWhiteSpace(fromNodes))
          sb.Append(fromNodes);
        else if (!string.IsNullOrWhiteSpace(annotation.Value))
          sb.Append(annotation.Value.Trim());
      }

      // Если не нашли явное описание, берём весь текст
      if (sb.Length == 0)
      {
        sb.Append(string.Join(" ", paragraphs.Select(p => p.Value?.Trim() ?? "")));
      }

      return sb.ToString().Trim();
    }

    /// <summary>Текст для карточки: убирает теги/HTML из dc:description и нормализует пробелы.</summary>
    static string EpubDcDescriptionToPlain(XElement descriptionEl)
    {
      var fromNodes = string.Join(" ",
          descriptionEl.DescendantNodes().OfType<XText>()
              .Select(t => t.Value.Trim())
              .Where(s => s.Length > 0));
      var raw = string.IsNullOrWhiteSpace(fromNodes) ? (descriptionEl.Value ?? "") : fromNodes;
      return StripHtmlishToPlainText(raw);
    }

    static string StripHtmlishToPlainText(string? raw)
    {
      if (string.IsNullOrWhiteSpace(raw))
        return "";
      string s = WebUtility.HtmlDecode(raw.Trim());
      s = Regex.Replace(s, @"<[^>]+>", " ");
      s = Regex.Replace(s, @"\s+", " ").Trim();
      return s;
    }

    private BookMetadata ParseEPUB(string filePath)
    {
      var metadata = new BookMetadata();

      try
      {
        System.Diagnostics.Debug.WriteLine($"[EPUB Parser] Загрузка файла: {filePath}");

        using (var zip = new System.IO.Compression.ZipArchive(File.OpenRead(filePath)))
        {
          // Ищем container.xml
          var containerEntry = zip.GetEntry("META-INF/container.xml");
          if (containerEntry == null)
          {
            System.Diagnostics.Debug.WriteLine("[EPUB Parser] container.xml не найден");
            return metadata;
          }

          string opfPath = null;

          using (var stream = containerEntry.Open())
          {
            var containerDoc = XDocument.Load(stream);
            var ns = XNamespace.Get("urn:oasis:names:tc:opendocument:xmlns:container");
            
            opfPath = containerDoc.Root
                ?.Element(ns + "rootfiles")
                ?.Element(ns + "rootfile")
                ?.Attribute("full-path")
                ?.Value;

            // Пробуем без namespace если не нашли
            if (string.IsNullOrEmpty(opfPath))
            {
              opfPath = containerDoc.Descendants()
                  .FirstOrDefault(e => e.Name.LocalName == "rootfile")
                  ?.Attribute("full-path")
                  ?.Value;
            }
          }

          System.Diagnostics.Debug.WriteLine($"[EPUB Parser] OPF путь: {opfPath}");

          if (string.IsNullOrEmpty(opfPath))
          {
            System.Diagnostics.Debug.WriteLine("[EPUB Parser] OPF путь не найден");
            return metadata;
          }

          // Пробуем найти OPF файл (может быть с разными путями)
          var opfEntry = zip.GetEntry(opfPath);
          if (opfEntry == null)
          {
            // Пробуем нормализовать путь
            opfPath = opfPath.Replace('\\', '/');
            opfEntry = zip.Entries.FirstOrDefault(e => 
                e.FullName.Equals(opfPath, StringComparison.OrdinalIgnoreCase));
          }

          if (opfEntry == null)
          {
            System.Diagnostics.Debug.WriteLine($"[EPUB Parser] OPF файл не найден: {opfPath}");
            return metadata;
          }

          using (var opfStream = opfEntry.Open())
          {
            var opfDoc = XDocument.Load(opfStream);
            
            // Ищем metadata элемент с разными вариантами namespace
            var metadataElement = opfDoc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "metadata");

            if (metadataElement == null)
            {
              System.Diagnostics.Debug.WriteLine("[EPUB Parser] Элемент metadata не найден");
              return metadata;
            }

            // Dublin Core namespace
            var dcNs = XNamespace.Get("http://purl.org/dc/elements/1.1/");

            // Название
            var titleEl = metadataElement.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "title");
            var titleRaw = titleEl?.Value?.Trim() ?? "";
            metadata.Title = StripHtmlishToPlainText(titleRaw);
            if (string.IsNullOrEmpty(metadata.Title))
              metadata.Title = Strings.Book_NoTitle;

            // Автор (в OPF может быть несколько dc:creator)
            var creators = metadataElement.Elements()
                .Where(e => e.Name.LocalName == "creator")
                .Select(e => StripHtmlishToPlainText(e.Value))
                .Where(s => !string.IsNullOrEmpty(s));
            metadata.Author = string.Join(", ", creators);

            // Описание: в EPUB часто XHTML или строка с &lt;p&gt; — на карточку выводим без тегов
            var descs = metadataElement.Elements()
                .Where(e => e.Name.LocalName == "description")
                .Select(EpubDcDescriptionToPlain)
                .Where(s => !string.IsNullOrEmpty(s));
            metadata.Description = string.Join(" ", descs);

            // Язык
            metadata.Language = metadataElement.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "language")?.Value?.Trim()
                ?? "en";

            System.Diagnostics.Debug.WriteLine($"[EPUB Parser] Название: {metadata.Title}");
            System.Diagnostics.Debug.WriteLine($"[EPUB Parser] Автор: {metadata.Author}");
            System.Diagnostics.Debug.WriteLine($"[EPUB Parser] Язык: {metadata.Language}");
          }
        }

        System.Diagnostics.Debug.WriteLine("[EPUB Parser] Парсинг завершён успешно");
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[EPUB Parser] ОШИБКА: {ex.Message}");
        System.Diagnostics.Debug.WriteLine($"[EPUB Parser] StackTrace: {ex.StackTrace}");
      }

      return metadata;
    }
  }
}
