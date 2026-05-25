using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace BookReaderApp.Services
{
  /// <summary>Ответ платформенного выбора книги FB2/EPUB или ZIP.</summary>
  public class FilePickerResult
  {
    /// <summary>Полный путь к выбранному или извлечённому файлу.</summary>
    public string FilePath { get; set; }
    /// <summary>Проверка успешного выбора (есть файл).</summary>
    public bool Success { get; set; }
    /// <summary>Пользователь закрёл диалог без выбора.</summary>
    public bool Cancelled { get; set; }
    /// <summary>Неподдерживаемое расширение файла (не ZIP с книгой внутри).</summary>
    public bool IsInvalidFormat { get; set; }
    /// <summary>Текст ошибки или сообщение об отмене.</summary>
    public string ErrorMessage { get; set; }

    /// <summary>Успех с путём к файлу.</summary>
    public static FilePickerResult Successful(string path) => new FilePickerResult
    {
      FilePath = path,
      Success = true
    };

    /// <summary>Пользователь отменил диалог выбора.</summary>
    public static FilePickerResult CancelledByUser() => new FilePickerResult
    {
      Cancelled = true,
      ErrorMessage = "Выбор файла отменён"
    };

    /// <summary>Расширение не поддерживается.</summary>
    public static FilePickerResult InvalidFormat(string extension) => new FilePickerResult
    {
      IsInvalidFormat = true,
      ErrorMessage = $"Формат '{extension}' не поддерживается."
    };

    /// <summary>Неустранимая ошибка выбора или распаковки.</summary>
    public static FilePickerResult Error(string message) => new FilePickerResult
    {
      ErrorMessage = message
    };
  }

  /// <summary>Системный выбор книги FB2/EPUB или архива ZIP с одной «основной» книгой внутри.</summary>
  public interface IFilePickerService
  {
    /// <summary>Отображает платформенный диалог выбора и при необходимости распаковывает ZIP во временную папку.</summary>
    Task<FilePickerResult> PickBookFileAsync();
  }

  /// <summary>Обёртка над <see cref="FilePicker"/>: фильтры по платформе и распаковка ZIP.</summary>
  public class FilePickerService : IFilePickerService
  {
    /// <inheritdoc />
    public async Task<FilePickerResult> PickBookFileAsync()
    {
      try
      {
        // Показываем все файлы - фильтрация после выбора
        var customFileType = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
          { DevicePlatform.Android, new[] { "*/*" } },
          { DevicePlatform.iOS, new[] { "public.item" } },
          { DevicePlatform.WinUI, new[] { ".fb2", ".epub", ".fb2.zip", ".epub.zip", ".zip", ".*" } },
          { DevicePlatform.MacCatalyst, new[] { "public.item" } }
        });

        var result = await FilePicker.PickAsync(new PickOptions
        {
          FileTypes = customFileType,
          PickerTitle = "Выберите книгу (FB2, EPUB или ZIP)"
        });

        // Пользователь отменил выбор
        if (result == null)
        {
          System.Diagnostics.Debug.WriteLine("[FilePicker] Выбор отменён пользователем");
          return FilePickerResult.CancelledByUser();
        }

        string fileName = result.FileName.ToLower();
        string filePath = result.FullPath;

        System.Diagnostics.Debug.WriteLine($"[FilePicker] Выбран: {result.FileName}");

        // FB2 или EPUB - сразу возвращаем
        if (fileName.EndsWith(".fb2") || fileName.EndsWith(".epub"))
        {
          System.Diagnostics.Debug.WriteLine($"[FilePicker] ✓ Формат: {(fileName.EndsWith(".fb2") ? "FB2" : "EPUB")}");
          return FilePickerResult.Successful(filePath);
        }

        // ZIP архивы
        if (fileName.EndsWith(".fb2.zip") || fileName.EndsWith(".epub.zip") || fileName.EndsWith(".zip"))
        {
          System.Diagnostics.Debug.WriteLine("[FilePicker] Распаковка ZIP архива...");
          string extractedPath = await ExtractZipArchiveAsync(filePath);

          if (string.IsNullOrEmpty(extractedPath))
          {
            return FilePickerResult.Error("В архиве не найдены файлы FB2 или EPUB");
          }

          return FilePickerResult.Successful(extractedPath);
        }

        // Неподдерживаемый формат
        string extension = Path.GetExtension(result.FileName);
        System.Diagnostics.Debug.WriteLine($"[FilePicker] ✗ Неподдерживаемый формат: {extension}");
        return FilePickerResult.InvalidFormat(extension);
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[FilePicker] Ошибка: {ex.Message}");
        return FilePickerResult.Error($"Ошибка выбора файла: {ex.Message}");
      }
    }

    /// <summary>
    /// Извлекает самую большую по размеру FB2/EPUB из архива во временный каталог.
    /// </summary>
    async Task<string> ExtractZipArchiveAsync(string zipPath)
    {
      try
      {
        System.Diagnostics.Debug.WriteLine($"[FilePicker] Распаковка архива: {zipPath}");

        string tempFolder = Path.Combine(FileSystem.CacheDirectory, "ExtractedBooks", Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempFolder);

        using (var archive = ZipFile.OpenRead(zipPath))
        {
          // Находим все книги в архиве
          var bookEntries = archive.Entries
              .Where(e => e.FullName.ToLower().EndsWith(".fb2") ||
                          e.FullName.ToLower().EndsWith(".epub"))
              .OrderByDescending(e => e.Length) // Сортируем по размеру (самый большой первый)
              .ToList();

          if (bookEntries.Count == 0)
          {
            System.Diagnostics.Debug.WriteLine("[FilePicker] В архиве не найдены FB2 или EPUB файлы");
            return null;
          }

          // Логируем найденные книги
          System.Diagnostics.Debug.WriteLine($"[FilePicker] Найдено книг в архиве: {bookEntries.Count}");
          foreach (var book in bookEntries)
          {
            System.Diagnostics.Debug.WriteLine($"  - {book.Name} ({book.Length / 1024} КБ)");
          }

          // Выбираем самую большую книгу
          var selectedEntry = bookEntries.First();

          if (bookEntries.Count > 1)
          {
            System.Diagnostics.Debug.WriteLine($"[FilePicker] Выбрана самая большая: {selectedEntry.Name}");
          }

          string extractedPath = Path.Combine(tempFolder, selectedEntry.Name);
          await Task.Run(() => selectedEntry.ExtractToFile(extractedPath, true));

          System.Diagnostics.Debug.WriteLine($"[FilePicker] ✓ Извлечён: {extractedPath}");
          return extractedPath;
        }
      }
      catch (Exception ex)
      {
        System.Diagnostics.Debug.WriteLine($"[FilePicker] Ошибка распаковки: {ex.Message}");
        return null;
      }
    }
  }
}
