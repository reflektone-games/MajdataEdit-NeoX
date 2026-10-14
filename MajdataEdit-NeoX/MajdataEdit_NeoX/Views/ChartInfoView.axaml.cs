using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using MajdataEdit_NeoX.Models.Fumen;
using PropertyGenerator.Avalonia;

namespace MajdataEdit_NeoX.Views
{
    public partial class ChartInfoView : UserControl
    {
        public ChartInfoView()
        {
            InitializeComponent();
        }




        [GeneratedDirectProperty(DefaultBindingMode = BindingMode.TwoWay)]
        public partial Bitmap Cover { get; set; } = null!;

        [GeneratedDirectProperty(DefaultBindingMode = BindingMode.TwoWay)]
        public partial MaidataFile Maidata { get; set; } = null!;

        [RelayCommand]
        public void AddCommand()
        {
            Maidata?.Commands.Add(new MutSimaiCommand("key", "value"));
        }

        [RelayCommand]
        public void DeleteCommand(MutSimaiCommand command)
        {
            Maidata?.Commands.Remove(command);
        }

        [RelayCommand]
        public void OpenBgCover()
        {
        }
        [RelayCommand]
        public void OpenBgVideo()
        {
        }
    }
}