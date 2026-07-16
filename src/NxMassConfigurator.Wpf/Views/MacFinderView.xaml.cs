using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NxMassConfigurator.Wpf.ViewModels;

namespace NxMassConfigurator.Wpf.Views;

/// <summary>
/// Code-behind for the "Remover por MAC" tab.
/// </summary>
public partial class MacFinderView : UserControl
{
    private readonly MacFinderViewModel _vm = new();

    public MacFinderView()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.LoadSettings();

        // Bind password from PasswordBox (not a DependencyProperty)
        NasPasswordBox.Password = _vm.Password;
        NasPasswordBox.PasswordChanged += (_, _) =>
        {
            _vm.Password = NasPasswordBox.Password;
        };
        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MacFinderViewModel.Password) && NasPasswordBox.Password != _vm.Password)
            {
                NasPasswordBox.Password = _vm.Password;
            }
        };

        Loaded += (_, _) => FocusMacTextBox();
    }

    private void FocusMacTextBox()
    {
        MacTextBox.Focus();
        MacTextBox.SelectAll();
    }

    public void AppendLog(string line)
    {
        _vm.AppendLog(line);
    }

    // ── Search ──────────────────────────────────────────────────────

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        SearchButton.Visibility = Visibility.Collapsed;
        CancelSearchButton.Visibility = Visibility.Visible;
        SearchProgressBar.Visibility = Visibility.Visible;
        ProgressText.Text = "Buscando...";
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            await _vm.SearchAsync();
        }
        finally
        {
            SearchButton.Visibility = Visibility.Visible;
            CancelSearchButton.Visibility = Visibility.Collapsed;
            SearchProgressBar.Visibility = Visibility.Collapsed;
            ProgressText.Text = "";
            Mouse.OverrideCursor = null;
        }
    }

    private void CancelSearch_Click(object sender, RoutedEventArgs e)
    {
        _vm.CancelSearch();
    }

    // ── Delete ──────────────────────────────────────────────────────

    private async void DeleteSingle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not NxMassConfigurator.Models.Qnap.MacFoundPath path)
            return;

        var result = MessageBox.Show(
            $"Confirmar exclusão de:\n\n{path.UncPath}\n\nEsta ação é permanente.",
            "Confirmar Exclusão",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        Mouse.OverrideCursor = Cursors.Wait;
        _vm.AppendLog($"[WARN] Excluindo: {path.UncPath}...");

        try
        {
            var rel = NxMassConfigurator.Services.Security.PathSecurityValidator
                .UncToRelativeTarget(path.UncPath, path.ServerIp, path.Share);
            var method = await NxMassConfigurator.Services.SmartQnapDelete.DeleteAsync(
                path.ServerIp, path.Share, _vm.Username, _vm.Password, rel);

            _vm.AppendLog($"[ OK ] Excluída via {method}.");
            _vm.FoundPaths.Remove(path);
        }
        catch (Exception ex)
        {
            _vm.AppendLog($"[ERRO] Falha: {ex.Message}");
            MessageBox.Show($"Falha ao excluir: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private async void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.FoundPaths.Count == 0) return;

        var result = MessageBox.Show(
            $"Confirmar exclusão de TODAS as {_vm.FoundPaths.Count} pastas?\n\nEsta ação é permanente e irreversível.",
            "Confirmar Exclusão em Massa",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        DeleteAllButton.Visibility = Visibility.Collapsed;
        CancelDeleteButton.Visibility = Visibility.Visible;
        SearchProgressBar.Visibility = Visibility.Visible;
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            await _vm.DeleteAllAsync(
                progress: new Progress<(double, string)>(p =>
                {
                    SearchProgressBar.Value = p.Item1;
                    ProgressText.Text = p.Item2;
                }),
                fileStatus: new Progress<string>(msg =>
                {
                    ProgressText.Text = msg;
                }));
        }
        finally
        {
            DeleteAllButton.Visibility = Visibility.Visible;
            CancelDeleteButton.Visibility = Visibility.Collapsed;
            SearchProgressBar.Visibility = Visibility.Collapsed;
            ProgressText.Text = "";
            Mouse.OverrideCursor = null;
        }
    }

    private void CancelDelete_Click(object sender, RoutedEventArgs e)
    {
        _vm.CancelDelete();
    }

    // ── Servers management ──────────────────────────────────────────

    private void AddServer_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ServerInputDialog();
        dialog.Owner = Window.GetWindow(this);
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ServerIp))
        {
            _vm.AddServer(dialog.ServerIp);
        }
    }

    private void RemoveServer_Click(object sender, RoutedEventArgs e)
    {
        // TODO: implement selected item removal from the server list
        // For now, no-op — servers are managed via settings
    }

    // ── Settings ────────────────────────────────────────────────────

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _vm.SaveSettings();
        _vm.AppendLog("[INFO] Configurações salvas com sucesso.");
    }
}
