using System.Windows;

namespace Revie.App.Views;

public partial class SessionNameWindow : Window
{
    public SessionNameWindow(string defaultName, string sessionsRoot)
    {
        InitializeComponent();
        SessionNameTextBox.Text = defaultName;
        DestinationTextBlock.Text = $"Save under: {sessionsRoot}";
        DestinationTextBlock.ToolTip = sessionsRoot;
        SessionNameTextBox.SelectAll();
        Loaded += (_, _) => SessionNameTextBox.Focus();
    }

    public string SessionName => SessionNameTextBox.Text.Trim();

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SessionNameTextBox.Text))
        {
            System.Windows.MessageBox.Show(
                "Please enter a name for the session.",
                "Session Name Required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void SessionNameTextBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            SaveButton_OnClick(sender, e);
            e.Handled = true;
        }
    }
}
