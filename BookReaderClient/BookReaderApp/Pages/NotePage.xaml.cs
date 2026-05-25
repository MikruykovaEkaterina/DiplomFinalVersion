using BookReaderApp.Resources;
using BookReaderApp.Services;
using BookReaderApp.ViewModels;
using Microsoft.Maui.Controls;

namespace BookReaderApp;

/// <summary>
/// Список заметок для произведения по идентификатору work: загрузка, добавление строки заметки, подтверждение удаления через делегат модели представления.
/// </summary>
public partial class NotesPage : ContentPage
{
  /// <summary>Создаёт страницу, связывает подписи, загружает заметки для произведения и задаёт делегат подтверждения удаления.</summary>
  public NotesPage(int workId, NotesPageViewModel viewModel)
  {
    InitializeComponent();
    BindingContext = viewModel;
    Title = Strings.Notes_ScreenTitle;
    ScreenTitleLabel.Text = Strings.Notes_ScreenTitle;
    EmptyNotesLabel.Text = Strings.Notes_NoNotesYet;
    _ = viewModel.LoadAsync(workId);
    viewModel.ConfirmDeleteNoteAsync = () =>
        ThemedOverlayPresenter.ShowConfirmAsync(
            this,
            Strings.Notes_DeleteConfirmTitle,
            Strings.Notes_DeleteConfirmMessage,
            Strings.Notes_DeleteConfirmAction,
            Strings.Common_Cancel);
  }

  /// <summary>Возврат к предыдущему экрану (обычно к читалке).</summary>
  private async void OnBackClicked(object sender, EventArgs e) =>
      await Navigation.PopAsync();

  /// <summary>Запускает команду добавления заметки из модели представления.</summary>
  private void OnAddNoteClicked(object sender, EventArgs e)
  {
    if (BindingContext is NotesPageViewModel vm)
      vm.AddNoteCommand.Execute(null);
  }
}
