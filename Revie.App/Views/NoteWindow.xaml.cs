using System.Windows;

namespace Revie.App.Views;

public partial class NoteWindow : Window
{
    public NoteWindow(string captureTitle, string noteText)
    {
        InitializeComponent();
        TitleTextBlock.Text = string.IsNullOrWhiteSpace(captureTitle) ? "Capture Note" : captureTitle;
        NoteTextBox.Text = string.IsNullOrWhiteSpace(noteText) ? "(No note text)" : noteText;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
