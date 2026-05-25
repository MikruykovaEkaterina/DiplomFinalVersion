namespace BookReaderApp.Models;

/// <summary>
/// Язык содержимого книги: загрузка, поиск, перевод книги, язык перевода предложений в настройках текста.
/// Каталог только RU/EN (остальные языки не являются «языками книги» в приложении).
/// </summary>
public enum BookLanguage
{
  None,
  Russian,
  English
}
