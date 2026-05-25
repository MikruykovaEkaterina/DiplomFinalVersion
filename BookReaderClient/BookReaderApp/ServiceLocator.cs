using Microsoft.Extensions.DependencyInjection;

namespace BookReaderApp;

/// <summary>Доступ к DI из страниц без конструкторной инъекции.</summary>
public static class ServiceLocator
{
  static IServiceProvider? _services;

  /// <summary>Инициализация после <see cref="MauiHostBuilderExtensions.Build"/></summary>
  public static void Init(IServiceProvider services) => _services = services;

  /// <summary>Возвращает зарегистрированный сервис или null.</summary>
  public static T? Get<T>() where T : class => _services?.GetService<T>();

  /// <summary>Возвращает зарегистрированный сервис; при отсутствии регистрации — исключение.</summary>
  public static T GetRequired<T>() where T : class =>
      (_services ?? throw new InvalidOperationException("ServiceLocator.Init was not called."))
      .GetRequiredService<T>();
}
