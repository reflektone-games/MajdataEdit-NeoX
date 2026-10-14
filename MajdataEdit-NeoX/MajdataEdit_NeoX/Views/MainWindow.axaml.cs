using System;
using Avalonia.Controls;

namespace MajdataEdit_NeoX.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is IAsyncDisposable disposable)
            await disposable.DisposeAsync();
    }
}