using System;
using System.Globalization;
using Microsoft.Maui.Controls;

namespace BookReaderApp.Converters
{
  /// <summary>Инвертирует <see cref="bool"/> для привязок (например <c>IsVisible</c>, когда нужно показывать блок, пока флаг «занятости» выключен).</summary>
  public class InvertedBoolConverter : IValueConverter
  {
    /// <summary>Возвращает отрицание булева значения; если значение не <see cref="bool"/>, считается «ложь», возвращается <see langword="true"/>.</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
      if (value is bool boolValue)
        return !boolValue;
      return true;
    }

    /// <summary>То же инвертирование для двусторонней привязки; если значение не <see cref="bool"/>, возвращает <see langword="false"/>.</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
      if (value is bool boolValue)
        return !boolValue;
      return false;
    }
  }
}

