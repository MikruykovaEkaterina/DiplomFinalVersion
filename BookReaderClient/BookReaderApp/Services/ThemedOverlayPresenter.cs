using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.ApplicationModel;

#if ANDROID
using Android.Views.InputMethods;
#endif
#if IOS || MACCATALYST
using UIKit;
#endif

namespace BookReaderApp.Services;

/// <summary>Системные DisplayActionSheet / DisplayAlert не следуют теме приложения — показываем свои оверлеи с ресурсами палитры и размером шрифта интерфейса.</summary>
public static class ThemedOverlayPresenter
{
  static readonly object Gate = new();
  static Grid? _host;
  static View? _layer;
  static TaskCompletionSource<string?>? _sheetTcs;
  static TaskCompletionSource<bool>? _confirmTcs;
  static TaskCompletionSource<string?>? _promptTcs;
  static TaskCompletionSource? _alertTcs;
  static EventHandler<ShellNavigatedEventArgs>? _navDismiss;

  static bool TryMergedResource(string key, out object? value)
  {
    value = null;
    var app = Application.Current;
    if (app == null)
      return false;
    foreach (var dict in app.Resources.MergedDictionaries.Reverse())
    {
      if (dict.TryGetValue(key, out var merged))
      {
        value = merged;
        return true;
      }
    }

    return app.Resources.TryGetValue(key, out value);
  }



  static Color Col(string key, Color fallback)
  {
    return TryMergedResource(key, out var o) && o is Color c ? c : fallback;
  }

  /// <summary>Затемнение как в ReadingPage — полупрозрачное, без лишнего фона под Color.</summary>
  static BoxView CreateScrim() =>
      new()
      {
        Color = Col("ModalScrimColor", Color.FromArgb("#99000000")),
        BackgroundColor = Colors.Transparent,
        HorizontalOptions = LayoutOptions.Fill,
        VerticalOptions = LayoutOptions.Fill
      };

  static ImageButton CreateModalCloseButton(Action onClose)
  {
    var btn = new ImageButton
    {
      Padding = new Thickness(0),
      BorderWidth = 0,
      CornerRadius = 0,
      BackgroundColor = Colors.Transparent,
      Aspect = Aspect.AspectFit,
      WidthRequest = InterfacePreferenceCoordinator.EffectiveDismissIconSize,
      HeightRequest = InterfacePreferenceCoordinator.EffectiveDismissIconSize,
      MinimumWidthRequest = InterfacePreferenceCoordinator.EffectiveDismissTapMinSize,
      MinimumHeightRequest = InterfacePreferenceCoordinator.EffectiveDismissTapMinSize,
      VerticalOptions = LayoutOptions.Center,
      HorizontalOptions = LayoutOptions.End
    };
    btn.SetDynamicResource(ImageButton.SourceProperty, "UiIconClose");
    btn.Clicked += (_, _) => onClose();
    return btn;
  }

  static void AttachNavDismiss()
  {
    if (Shell.Current == null || _navDismiss != null)
      return;
    _navDismiss = (_, _) => AbortDueToNavigation();
    Shell.Current.Navigated += _navDismiss;
  }

  static void DetachNavDismiss()
  {
    if (Shell.Current != null && _navDismiss != null)
      Shell.Current.Navigated -= _navDismiss;
    _navDismiss = null;
  }

  static void AbortDueToNavigation()
  {
    TaskCompletionSource<string?>? sheet;
    TaskCompletionSource<bool>? confirm;
    TaskCompletionSource<string?>? promptTcs;
    TaskCompletionSource? alert;
    Grid? host;
    View? layer;

    lock (Gate)
    {
      sheet = _sheetTcs;
      confirm = _confirmTcs;
      promptTcs = _promptTcs;
      alert = _alertTcs;
      host = _host;
      layer = _layer;
      _sheetTcs = null;
      _confirmTcs = null;
      _promptTcs = null;
      _alertTcs = null;
      _host = null;
      _layer = null;
      DetachNavDismiss();
    }

    RunUi(() =>
    {
      if (host != null && layer != null)
      {
        try
        {
          host.Children.Remove(layer);
        }
        catch { }
      }

      sheet?.TrySetResult(null);
      confirm?.TrySetResult(false);
      promptTcs?.TrySetResult(null);
      alert?.TrySetResult();
    });
  }

  static void RunUi(Action action)
  {
    if (MainThread.IsMainThread)
      action();
    else
      MainThread.BeginInvokeOnMainThread(action);
  }

  /// <summary>Скрыть софт-клавиатуру при закрытии поля ввода в модалках.</summary>
  static void DismissSoftInputKeyboard()
  {
#if ANDROID
    try
    {
      var act = Platform.CurrentActivity;
      if (act == null)
        return;
      var view = act.CurrentFocus ?? act.Window?.DecorView;
      if (view?.WindowToken != null)
      {
        var imm = InputMethodManager.FromContext(act);
        imm?.HideSoftInputFromWindow(view.WindowToken, HideSoftInputFlags.None);
      }
    }
    catch
    {
      // ignore — не блокируем закрытие диалога
    }
#elif IOS || MACCATALYST
    try
    {
      UIApplication.SharedApplication?.KeyWindow?.EndEditing(true);
    }
    catch
    {
    }
#endif
  }

  /// <inheritdoc cref="ShowActionSheetAsync(ContentPage, string?, string, IReadOnlyList{string})"/>
  public static Task<string?> ShowActionSheetAsync(ContentPage page, string? title, string cancelText, params string[] options) =>
      ShowActionSheetAsync(page, title, cancelText, (IReadOnlyList<string>)options);

  /// <summary>Тёмный затемняющий оверлей с вертикальным списком кнопок (аналог действий по теме приложения).</summary>
  public static Task<string?> ShowActionSheetAsync(ContentPage page, string? title, string cancelText, IReadOnlyList<string> options)
  {
    var tcs = new TaskCompletionSource<string?>();
    RunUi(() =>
    {
      lock (Gate)
      {
        if (_layer != null)
        {
          tcs.TrySetResult(null);
          return;
        }

        _sheetTcs = tcs;
        var host = InAppNotificationPresenter.ObtainOverlayHost(page);
        _host = host;

        var btnFs = InterfacePreferenceCoordinator.EffectiveButtonFontSize;
        var titleFs = InterfacePreferenceCoordinator.EffectiveAppTitleFontSize;

        var root = new Grid
        {
          InputTransparent = false,
          ZIndex = 10_000,
          HorizontalOptions = LayoutOptions.Fill,
          VerticalOptions = LayoutOptions.Fill
        };

        void DismissNull()
        {
          CompleteSheet(host, root, null);
        }

        var backdropTap = new TapGestureRecognizer();
        backdropTap.Tapped += (_, _) => DismissNull();
        root.GestureRecognizers.Add(backdropTap);

        var bubble = new Border
        {
          Margin = new Thickness(24),
          Padding = new Thickness(16),
          StrokeThickness = 1,
          Stroke = Col("CardBorderColor", Colors.Gray),
          BackgroundColor = Col("PrimaryBackground", Colors.White),
          StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(12) },
          HorizontalOptions = LayoutOptions.Fill,
          VerticalOptions = LayoutOptions.Center,
          MaximumWidthRequest = 480,
          InputTransparent = false
        };
        var absorbBubbleTap = new TapGestureRecognizer();
        absorbBubbleTap.Tapped += (_, _) => { };
        bubble.GestureRecognizers.Add(absorbBubbleTap);

        var stack = new VerticalStackLayout { Spacing = 12 };

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        if (!string.IsNullOrWhiteSpace(title))
        {
          var titleLbl = new Label
          {
            Text = title,
            FontSize = titleFs,
            TextColor = Col("MainTextColor", Colors.Black),
            LineBreakMode = LineBreakMode.WordWrap,
            VerticalOptions = LayoutOptions.Center
          };
          Grid.SetColumn(titleLbl, 0);
          headerGrid.Children.Add(titleLbl);
        }

        var closeSheet = CreateModalCloseButton(DismissNull);
        Grid.SetColumn(closeSheet, 1);
        headerGrid.Children.Add(closeSheet);
        stack.Children.Add(headerGrid);

        foreach (var label in options)
        {
          var captured = label;
          var btn = new Button
          {
            Text = captured,
            FontSize = btnFs,
            LineBreakMode = LineBreakMode.WordWrap,
            HorizontalOptions = LayoutOptions.Fill,
            MinimumHeightRequest = 48,
            Padding = new Thickness(14, 12),
            TextColor = Col("ActiveButtonTextColor", Colors.White),
            BackgroundColor = Col("ActiveButtonBackgroundColor", Colors.Teal)
          };
          btn.Clicked += (_, _) => CompleteSheet(host, root, captured);
          stack.Children.Add(btn);
        }

        var cancelBtn = new Button
        {
          Text = cancelText,
          FontSize = btnFs,
          LineBreakMode = LineBreakMode.WordWrap,
          HorizontalOptions = LayoutOptions.Fill,
          MinimumHeightRequest = 48,
          Padding = new Thickness(14, 12),
          TextColor = Col("MainTextColor", Colors.Black),
          BackgroundColor = Col("InactiveButtonBackgroundColor", Colors.LightGray)
        };
        cancelBtn.Clicked += (_, _) => DismissNull();
        stack.Children.Add(cancelBtn);

        bubble.Content = stack;

        var scrim = CreateScrim();

        root.Children.Add(scrim);
        root.Children.Add(bubble);

        Grid.SetRow(root, 0);
        host.Children.Add(root);
        _layer = root;
        AttachNavDismiss();
      }
    });

    return tcs.Task;
  }

  static void CompleteSheet(Grid host, View root, string? result)
  {
    TaskCompletionSource<string?>? tcs;
    lock (Gate)
    {
      tcs = _sheetTcs;
      _sheetTcs = null;
      try
      {
        host.Children.Remove(root);
      }
      catch { }

      if (ReferenceEquals(_layer, root))
      {
        _layer = null;
        _host = null;
        DetachNavDismiss();
      }
    }

    tcs?.TrySetResult(result);
  }

  /// <summary>Темизированное подтверждение (принять / отменить) с возвратом <c>true</c> при согласии.</summary>
  public static Task<bool> ShowConfirmAsync(ContentPage page, string title, string message, string acceptText, string cancelText)
  {
    var tcs = new TaskCompletionSource<bool>();
    RunUi(() =>
    {
      lock (Gate)
      {
        if (_layer != null)
        {
          tcs.TrySetResult(false);
          return;
        }

        _confirmTcs = tcs;
        var host = InAppNotificationPresenter.ObtainOverlayHost(page);
        _host = host;

        var bodyFs = InterfacePreferenceCoordinator.EffectiveMainFontSize;
        var btnFs = InterfacePreferenceCoordinator.EffectiveButtonFontSize;
        var titleFs = InterfacePreferenceCoordinator.EffectiveAppTitleFontSize;

        var root = new Grid
        {
          InputTransparent = false,
          ZIndex = 10_000,
          HorizontalOptions = LayoutOptions.Fill,
          VerticalOptions = LayoutOptions.Fill
        };

        void Finish(bool v)
        {
          CompleteConfirm(host, root, v);
        }

        var backdropTap = new TapGestureRecognizer();
        backdropTap.Tapped += (_, _) => Finish(false);
        root.GestureRecognizers.Add(backdropTap);

        var bubble = new Border
        {
          Margin = new Thickness(24),
          Padding = new Thickness(16),
          StrokeThickness = 1,
          Stroke = Col("CardBorderColor", Colors.Gray),
          BackgroundColor = Col("PrimaryBackground", Colors.White),
          StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(12) },
          HorizontalOptions = LayoutOptions.Fill,
          VerticalOptions = LayoutOptions.Center,
          MaximumWidthRequest = 480,
          InputTransparent = false
        };
        var absorbBubbleTap = new TapGestureRecognizer();
        absorbBubbleTap.Tapped += (_, _) => { };
        bubble.GestureRecognizers.Add(absorbBubbleTap);

        var stack = new VerticalStackLayout { Spacing = 14 };

        var headerConfirm = new Grid();
        headerConfirm.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        headerConfirm.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var titleLblConfirm = new Label
        {
          Text = title,
          FontSize = titleFs,
          TextColor = Col("MainTextColor", Colors.Black),
          LineBreakMode = LineBreakMode.WordWrap,
          VerticalOptions = LayoutOptions.Center
        };
        Grid.SetColumn(titleLblConfirm, 0);
        headerConfirm.Children.Add(titleLblConfirm);
        var closeConfirm = CreateModalCloseButton(() => Finish(false));
        Grid.SetColumn(closeConfirm, 1);
        headerConfirm.Children.Add(closeConfirm);
        stack.Children.Add(headerConfirm);

        stack.Children.Add(new Label
        {
          Text = message,
          FontSize = bodyFs,
          TextColor = Col("MainTextColor", Colors.Black),
          LineBreakMode = LineBreakMode.WordWrap
        });

        var btnGrid = new Grid
        {
          ColumnSpacing = 10,
          HorizontalOptions = LayoutOptions.Fill
        };
        btnGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        btnGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var cancelBtn = new Button
        {
          Text = cancelText,
          FontSize = btnFs,
          LineBreakMode = LineBreakMode.WordWrap,
          HorizontalOptions = LayoutOptions.Fill,
          MinimumHeightRequest = 48,
          Padding = new Thickness(12, 10),
          TextColor = Col("MainTextColor", Colors.Black),
          BackgroundColor = Col("InactiveButtonBackgroundColor", Colors.LightGray)
        };
        cancelBtn.Clicked += (_, _) => Finish(false);
        var okBtn = new Button
        {
          Text = acceptText,
          FontSize = btnFs,
          LineBreakMode = LineBreakMode.WordWrap,
          HorizontalOptions = LayoutOptions.Fill,
          MinimumHeightRequest = 48,
          Padding = new Thickness(12, 10),
          TextColor = Col("ActiveButtonTextColor", Colors.White),
          BackgroundColor = Col("ActiveButtonBackgroundColor", Colors.Teal)
        };
        okBtn.Clicked += (_, _) => Finish(true);
        btnGrid.Children.Add(cancelBtn);
        btnGrid.Children.Add(okBtn);
        Grid.SetColumn(okBtn, 1);

        stack.Children.Add(btnGrid);

        bubble.Content = stack;

        var scrim = CreateScrim();

        root.Children.Add(scrim);
        root.Children.Add(bubble);

        Grid.SetRow(root, 0);
        host.Children.Add(root);
        _layer = root;
        AttachNavDismiss();
      }
    });

    return tcs.Task;
  }

  static void CompleteConfirm(Grid host, View root, bool result)
  {
    TaskCompletionSource<bool>? tcs;
    lock (Gate)
    {
      tcs = _confirmTcs;
      _confirmTcs = null;
      try
      {
        host.Children.Remove(root);
      }
      catch { }

      if (ReferenceEquals(_layer, root))
      {
        _layer = null;
        _host = null;
        DetachNavDismiss();
      }
    }

    tcs?.TrySetResult(result);
  }

  static void CompletePrompt(Grid host, View root, string? result)
  {
    TaskCompletionSource<string?>? tcs;
    lock (Gate)
    {
      tcs = _promptTcs;
      _promptTcs = null;
      try
      {
        host.Children.Remove(root);
      }
      catch { }

      if (ReferenceEquals(_layer, root))
      {
        _layer = null;
        _host = null;
        DetachNavDismiss();
      }
    }

    tcs?.TrySetResult(result);
  }

  /// <summary>Ввод строки темизированным оверлеем (вместо <see cref="Page.DisplayPromptAsync"/>).</summary>
  public static Task<string?> ShowPromptAsync(
      ContentPage page,
      string title,
      string? message,
      string acceptText,
      string cancelText,
      string? initialValue = null,
      Keyboard? keyboard = null)
  {
    var tcs = new TaskCompletionSource<string?>();
    RunUi(() =>
    {
      lock (Gate)
      {
        if (_layer != null)
        {
          tcs.TrySetResult(null);
          return;
        }

        _promptTcs = tcs;
        var host = InAppNotificationPresenter.ObtainOverlayHost(page);
        _host = host;

        var bodyFs = InterfacePreferenceCoordinator.EffectiveMainFontSize;
        var btnFs = InterfacePreferenceCoordinator.EffectiveButtonFontSize;
        var titleFs = InterfacePreferenceCoordinator.EffectiveAppTitleFontSize;

        var root = new Grid
        {
          InputTransparent = false,
          ZIndex = 10_000,
          HorizontalOptions = LayoutOptions.Fill,
          VerticalOptions = LayoutOptions.Fill
        };

        var promptEntry = new Entry
        {
          FontSize = bodyFs,
          Text = initialValue ?? "",
          HorizontalOptions = LayoutOptions.Fill,
          ClearButtonVisibility = ClearButtonVisibility.WhileEditing,
          Margin = new Thickness(0, 2, 0, 4)
        };
        if (keyboard != null)
          promptEntry.Keyboard = keyboard;
        promptEntry.SetDynamicResource(Entry.TextColorProperty, "MainTextColor");
        promptEntry.SetDynamicResource(Entry.BackgroundColorProperty, "PrimaryBackground");

        void Finish(string? v)
        {
          DismissSoftInputKeyboard();
          promptEntry.Unfocus();
          try
          {
            page.Focus();
          }
          catch
          {
          }
          CompletePrompt(host, root, v);
        }

        var backdropTap = new TapGestureRecognizer();
        backdropTap.Tapped += (_, _) => Finish(null);
        root.GestureRecognizers.Add(backdropTap);

        var bubble = new Border
        {
          Margin = new Thickness(24),
          Padding = new Thickness(16),
          StrokeThickness = 1,
          StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(12) },
          HorizontalOptions = LayoutOptions.Fill,
          VerticalOptions = LayoutOptions.Center,
          MaximumWidthRequest = 480,
          InputTransparent = false
        };
        bubble.SetDynamicResource(Border.BackgroundColorProperty, "PrimaryBackground");
        bubble.SetDynamicResource(Border.StrokeProperty, "CardBorderColor");

        var absorbBubbleTap = new TapGestureRecognizer();
        absorbBubbleTap.Tapped += (_, _) => { };
        bubble.GestureRecognizers.Add(absorbBubbleTap);

        var stack = new VerticalStackLayout { Spacing = 14 };

        var headerPrompt = new Grid();
        headerPrompt.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        headerPrompt.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var titleLblPrompt = new Label
        {
          Text = title,
          FontSize = titleFs,
          LineBreakMode = LineBreakMode.WordWrap,
          VerticalOptions = LayoutOptions.Center
        };
        titleLblPrompt.SetDynamicResource(Label.TextColorProperty, "MainTextColor");
        Grid.SetColumn(titleLblPrompt, 0);
        headerPrompt.Children.Add(titleLblPrompt);
        var closePrompt = CreateModalCloseButton(() => Finish(null));
        Grid.SetColumn(closePrompt, 1);
        headerPrompt.Children.Add(closePrompt);
        stack.Children.Add(headerPrompt);

        if (!string.IsNullOrWhiteSpace(message))
        {
          var msgLbl = new Label
          {
            Text = message,
            FontSize = bodyFs,
            LineBreakMode = LineBreakMode.WordWrap
          };
          msgLbl.SetDynamicResource(Label.TextColorProperty, "MainTextColor");
          stack.Children.Add(msgLbl);
        }

        stack.Children.Add(promptEntry);
        promptEntry.Loaded += (_, _) =>
          MainThread.BeginInvokeOnMainThread(() =>
          {
            try
            {
              promptEntry.Focus();
            }
            catch
            {
              // ignore focus errors on unloaded views
            }
          });
        promptEntry.Completed += (_, _) => Finish(promptEntry.Text);

        var btnGrid = new Grid
        {
          ColumnSpacing = 10,
          HorizontalOptions = LayoutOptions.Fill
        };
        btnGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        btnGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var cancelBtn = new Button
        {
          Text = cancelText,
          FontSize = btnFs,
          LineBreakMode = LineBreakMode.WordWrap,
          HorizontalOptions = LayoutOptions.Fill,
          MinimumHeightRequest = 48,
          Padding = new Thickness(12, 10)
        };
        cancelBtn.SetDynamicResource(Button.TextColorProperty, "MainTextColor");
        cancelBtn.SetDynamicResource(Button.BackgroundColorProperty, "InactiveButtonBackgroundColor");
        cancelBtn.Clicked += (_, _) => Finish(null);
        var okBtn = new Button
        {
          Text = acceptText,
          FontSize = btnFs,
          LineBreakMode = LineBreakMode.WordWrap,
          HorizontalOptions = LayoutOptions.Fill,
          MinimumHeightRequest = 48,
          Padding = new Thickness(12, 10)
        };
        okBtn.SetDynamicResource(Button.TextColorProperty, "ActiveButtonTextColor");
        okBtn.SetDynamicResource(Button.BackgroundColorProperty, "ActiveButtonBackgroundColor");
        okBtn.Clicked += (_, _) => Finish(promptEntry.Text);
        btnGrid.Children.Add(cancelBtn);
        btnGrid.Children.Add(okBtn);
        Grid.SetColumn(okBtn, 1);
        stack.Children.Add(btnGrid);

        bubble.Content = stack;

        var scrim = CreateScrim();

        root.Children.Add(scrim);
        root.Children.Add(bubble);

        Grid.SetRow(root, 0);
        host.Children.Add(root);
        _layer = root;
        AttachNavDismiss();
      }
    });

    return tcs.Task;
  }

  /// <summary>Упрощённый alert с одной кнопкой «ОК» поверх страницы.</summary>
  public static Task ShowAlertAsync(ContentPage page, string title, string message, string okText)
  {
    var tcs = new TaskCompletionSource();
    RunUi(() =>
    {
      lock (Gate)
      {
        if (_layer != null)
        {
          tcs.TrySetResult();
          return;
        }

        _alertTcs = tcs;
        var host = InAppNotificationPresenter.ObtainOverlayHost(page);
        _host = host;

        var bodyFs = InterfacePreferenceCoordinator.EffectiveMainFontSize;
        var btnFs = InterfacePreferenceCoordinator.EffectiveButtonFontSize;
        var titleFs = InterfacePreferenceCoordinator.EffectiveAppTitleFontSize;

        var root = new Grid
        {
          InputTransparent = false,
          ZIndex = 10_000,
          HorizontalOptions = LayoutOptions.Fill,
          VerticalOptions = LayoutOptions.Fill
        };

        void Finish()
        {
          CompleteAlert(host, root);
        }

        var backdropTap = new TapGestureRecognizer();
        backdropTap.Tapped += (_, _) => Finish();
        root.GestureRecognizers.Add(backdropTap);

        var bubble = new Border
        {
          Margin = new Thickness(24),
          Padding = new Thickness(16),
          StrokeThickness = 1,
          Stroke = Col("CardBorderColor", Colors.Gray),
          BackgroundColor = Col("PrimaryBackground", Colors.White),
          StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(12) },
          HorizontalOptions = LayoutOptions.Fill,
          VerticalOptions = LayoutOptions.Center,
          MaximumWidthRequest = 480,
          InputTransparent = false
        };
        var absorbBubbleTap = new TapGestureRecognizer();
        absorbBubbleTap.Tapped += (_, _) => { };
        bubble.GestureRecognizers.Add(absorbBubbleTap);

        var stack = new VerticalStackLayout { Spacing = 14 };

        var headerAlert = new Grid();
        headerAlert.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        headerAlert.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var titleLblAlert = new Label
        {
          Text = title,
          FontSize = titleFs,
          TextColor = Col("MainTextColor", Colors.Black),
          LineBreakMode = LineBreakMode.WordWrap,
          VerticalOptions = LayoutOptions.Center
        };
        Grid.SetColumn(titleLblAlert, 0);
        headerAlert.Children.Add(titleLblAlert);
        var closeAlert = CreateModalCloseButton(Finish);
        Grid.SetColumn(closeAlert, 1);
        headerAlert.Children.Add(closeAlert);
        stack.Children.Add(headerAlert);

        stack.Children.Add(new Label
        {
          Text = message,
          FontSize = bodyFs,
          TextColor = Col("MainTextColor", Colors.Black),
          LineBreakMode = LineBreakMode.WordWrap
        });
        var okBtn = new Button
        {
          Text = okText,
          FontSize = btnFs,
          LineBreakMode = LineBreakMode.WordWrap,
          HorizontalOptions = LayoutOptions.Fill,
          MinimumHeightRequest = 48,
          Padding = new Thickness(14, 12),
          TextColor = Col("ActiveButtonTextColor", Colors.White),
          BackgroundColor = Col("ActiveButtonBackgroundColor", Colors.Teal)
        };
        okBtn.Clicked += (_, _) => Finish();
        stack.Children.Add(okBtn);

        bubble.Content = stack;

        var scrim = CreateScrim();

        root.Children.Add(scrim);
        root.Children.Add(bubble);

        Grid.SetRow(root, 0);
        host.Children.Add(root);
        _layer = root;
        AttachNavDismiss();
      }
    });

    return tcs.Task;
  }

  static void CompleteAlert(Grid host, View root)
  {
    TaskCompletionSource? tcs;
    lock (Gate)
    {
      tcs = _alertTcs;
      _alertTcs = null;
      try
      {
        host.Children.Remove(root);
      }
      catch { }

      if (ReferenceEquals(_layer, root))
      {
        _layer = null;
        _host = null;
        DetachNavDismiss();
      }
    }

    tcs?.TrySetResult();
  }
}
