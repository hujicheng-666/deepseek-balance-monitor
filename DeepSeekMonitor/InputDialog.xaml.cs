using System.Windows;

namespace DeepSeekMonitor;

public partial class InputDialog : Window
{
    public string Value => KeyBox.Password;

    public InputDialog(string title, string message, string initial = "")
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        if (!string.IsNullOrEmpty(initial))
            KeyBox.Password = initial;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
