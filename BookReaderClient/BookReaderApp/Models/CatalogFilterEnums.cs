namespace BookReaderApp.Models;

/// <summary>Статус прочитанности книги одного произведения (хранится в <see cref="Work"/>).</summary>
public enum BookStatus
{
  None,
  New,
  InProgress,
  Read
}

/// <summary>Реакция на произведение (хранится в <see cref="Work"/>).</summary>
public enum BookReaction
{
  None,
  Favorite,
  Unrated
}

/// <summary>Вариант сортировки списка книг на главной.</summary>
public enum BookSort
{
  TitleAsc,
  TitleDesc,
  DateNew,
  DateOld,
  SizeAsc,
  SizeDesc
}
