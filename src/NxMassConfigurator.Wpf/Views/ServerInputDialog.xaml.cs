using System.Windows;

namespace NxMassConfigurator.Wpf.Views;

public partial class ServerInputDialog : Window
{
    public string? ServerIp { get; private set; }

    public ServerInputDialog()
    {
        InitializeComponent();
        IpTextBox.Focus();
        IpTextBox.SelectAll();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(IpTextBox.Text))
        {
            ServerIp = IpTextBox.Text.Trim();
            DialogResult = true;
            Close();
        }
    }
}
