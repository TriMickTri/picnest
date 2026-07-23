using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PicNest;

public enum ProtectedPasswordMode
{
    Set,
    Unlock
}

public sealed record ProtectedPasswordResult(string Password);

public partial class ProtectedPasswordWindow : Window
{
    private readonly ProtectedPasswordMode _mode;

    public ProtectedPasswordWindow() : this(ProtectedPasswordMode.Unlock)
    {
    }

    public ProtectedPasswordWindow(ProtectedPasswordMode mode)
    {
        _mode = mode;
        InitializeComponent();
        var setting = mode == ProtectedPasswordMode.Set;
        Title = setting ? "Set protected-folder password" : "Unlock protected folders";
        Heading.Text = setting ? "Set an app password" : "Unlock protected folders";
        Description.Text = setting
            ? "This password hides protected folders inside PicNest. It does not encrypt the original files."
            : "Enter your PicNest password to show protected folders for this session.";
        ConfirmationPanel.IsVisible = setting;
        SubmitButton.Content = setting ? "Save password" : "Unlock";
        Opened += (_, _) => PasswordInput.Focus();
    }

    private void Submit(object? sender, RoutedEventArgs e)
    {
        var password = PasswordInput.Text ?? "";
        if (string.IsNullOrWhiteSpace(password))
        {
            DialogStatus.Text = "Enter a password.";
            return;
        }
        if (_mode == ProtectedPasswordMode.Set && password.Length < 4)
        {
            DialogStatus.Text = "Use at least four characters.";
            return;
        }
        if (_mode == ProtectedPasswordMode.Set && !string.Equals(password, ConfirmationInput.Text ?? "", StringComparison.Ordinal))
        {
            DialogStatus.Text = "The passwords do not match.";
            return;
        }
        Close(new ProtectedPasswordResult(password));
    }

    private void Cancel(object? sender, RoutedEventArgs e) => Close();
}
