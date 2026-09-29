using System.Windows;
using MaMini.App.ViewModels;

namespace MaMini.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        TokenBox.Password = vm.Token;
        vm.CloseRequested += (_, saved) =>
        {
            Saved = saved;
            Close();
        };
        ContentRendered += (_, _) => vm.OnShown();
    }

    public bool Saved { get; private set; }

    private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e) => _vm.Token = TokenBox.Password;
}
