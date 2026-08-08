using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DeepSeekMonitor.Views;

public partial class InputDialog : Window
{
    public string Value => KeyBox.Text ?? "";

    public InputDialog(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = prompt;
        if (!string.IsNullOrEmpty(initial))
            KeyBox.Text = initial;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        KeyBox.Focus();
        KeyBox.SelectAll();
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
