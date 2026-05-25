using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BookReaderApp.Services;

/// <summary>Извлечение плоского текста книги по пути (та же логика, что при открытии в ReadingPage).</summary>
public static class BookPlainTextLoader
{
  /// <summary>Возвращает извлечённый текст или null при ошибке/неподдерживаемом формате.</summary>
  public static string? TryLoadPlainText(string filePath, string? format)
  {
    if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
      return null;
    try
    {
      var fmt = format?.ToUpperInvariant() ?? string.Empty;
      var lowerPath = filePath.ToLowerInvariant();

      if (lowerPath.EndsWith(".fb2.zip", StringComparison.Ordinal))
      {
        using var zip = new ZipArchive(File.OpenRead(filePath));
        var fb2Entry = BookZipEntryHelper.FindPrimaryFb2Entry(zip);
        if (fb2Entry == null)
          return null;
        using var stream = fb2Entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var xml = reader.ReadToEnd();
        var (text, _) = Fb2BookTextExtractor.ExtractFromXml(xml);
        return string.IsNullOrWhiteSpace(text) ? null : text;
      }

      if (fmt == "FB2" || lowerPath.EndsWith(".fb2"))
      {
        var xml = File.ReadAllText(filePath);
        var (text, _) = Fb2BookTextExtractor.ExtractFromXml(xml);
        return string.IsNullOrWhiteSpace(text) ? null : text;
      }

      if (fmt == "EPUB" || lowerPath.EndsWith(".epub") || lowerPath.EndsWith(".epub.zip"))
      {
        if (EpubBookExtractor.TryExtract(filePath, out var epubPlain, out _, out _))
          return string.IsNullOrWhiteSpace(epubPlain) ? null : epubPlain;
        return ExtractEpubFallback(filePath);
      }

      return null;
    }
    catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"[BookPlainTextLoader] {ex.Message}");
      return null;
    }
  }

  /// <summary>Упрощённое извлечение текста из HTML/XHTML внутри EPUB при сбое <see cref="EpubBookExtractor"/>.</summary>
  static string? ExtractEpubFallback(string filePath)
  {
    try
    {
      var text = new StringBuilder();
      using var zip = new ZipArchive(File.OpenRead(filePath));
      foreach (var entry in zip.Entries.OrderBy(e => e.FullName))
      {
        if (entry.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
            entry.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase))
        {
          using var stream = entry.Open();
          using var reader = new StreamReader(stream);
          var content = reader.ReadToEnd();
          var textOnly = Regex.Replace(content, "<[^>]*>", "");
          text.Append(textOnly).Append(' ');
        }
      }
      var s = text.ToString().Trim();
      return string.IsNullOrEmpty(s) ? null : s;
    }
    catch
    {
      return null;
    }
  }
}
