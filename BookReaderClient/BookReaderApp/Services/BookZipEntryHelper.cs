using System.IO.Compression;
using System.Linq;

namespace BookReaderApp.Services;

/// <summary>Единая логика выбора вложенного файла книги в .fb2.zip / .epub.zip (несколько записей — берём основную по размеру).</summary>
public static class BookZipEntryHelper
{
  /// <summary>Возвращает самую большую по размеру запись с расширением <c>.fb2</c>.</summary>
  public static ZipArchiveEntry? FindPrimaryFb2Entry(ZipArchive zip) =>
      zip.Entries
          .Where(e => !string.IsNullOrEmpty(e.Name) &&
                      e.Name.EndsWith(".fb2", StringComparison.OrdinalIgnoreCase))
          .OrderByDescending(e => e.Length)
          .FirstOrDefault();

  /// <summary>Возвращает самую большую по размеру запись с расширением <c>.epub</c>.</summary>
  public static ZipArchiveEntry? FindPrimaryEpubEntry(ZipArchive zip) =>
      zip.Entries
          .Where(e => !string.IsNullOrEmpty(e.Name) &&
                      e.Name.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
          .OrderByDescending(e => e.Length)
          .FirstOrDefault();
}
