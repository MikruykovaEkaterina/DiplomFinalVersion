using System.Threading;
using BookReaderApp.Models;

namespace BookReaderApp.Services
{
  /// <summary>Импорт файла книги в хранилище приложения и запись в БД; импорт готового перевода в состав работы.</summary>
  public interface IBookUploadService
  {
    /// <summary>Копирует файл, извлекает метаданные и создаёт <see cref="Work"/> с <see cref="Card"/>.</summary>
    Task<UploadResult> UploadAndSaveBookAsync(
        string filePath,
        BookMetadata metadata,
        BookLanguage language,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Привязывает файл перевода к существующей работе как новую языковую версию.</summary>
    Task<UploadResult> ImportTranslatedBookVersionAsync(
        int workId,
        string translatedFilePath,
        BookLanguage targetLanguage,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
  }
}
