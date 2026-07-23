using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PicNest;

public partial class ConfirmDeleteWindow : Window
{
    public ConfirmDeleteWindow() : this(0)
    {
    }

    public ConfirmDeleteWindow(int count)
    {
        InitializeComponent();
        MessageText.Text = $"{count:n0} selected item(s) will be removed from their current folders.";
    }

    public static async Task<bool> ShowAsync(Window owner, int count)
    {
        var dialog = new ConfirmDeleteWindow(count);
        return await dialog.ShowDialog<bool>(owner);
    }

    private void ConfirmDelete(object? sender, RoutedEventArgs e) => Close(true);

    private void CancelDelete(object? sender, RoutedEventArgs e) => Close(false);
}
