using Avalonia.Controls;
using MajdataEdit_NeoX.Models.Setting;
using PropertyGenerator.Avalonia;
using System.Windows.Input;

namespace MajdataEdit_NeoX.Views;

public partial class SettingsView : UserControl
{
    [GeneratedDirectProperty(DefaultBindingMode = Avalonia.Data.BindingMode.TwoWay)]
    public partial MajSetting? Settings { get; set; }

    [GeneratedDirectProperty(DefaultBindingMode = Avalonia.Data.BindingMode.TwoWay)]
    public partial ICommand? SettingsChanged { get; set; }

    public SettingsView()
    {
        InitializeComponent();
    }
    private void PropertyGrid_CommandExecuted(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SettingsChanged?.Execute(null);
    }
}
