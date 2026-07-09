using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NxMassConfigurator.Models;
using NxMassConfigurator.Services;

namespace NxMassConfigurator.Wpf;

internal sealed class MainViewModel
{
    public ObservableCollection<ServerItem> Servers { get; } = [];
    public ObservableCollection<DeviceItem> VisibleDevices { get; } = [];
    public ObservableCollection<ResultItem> Results { get; } = [];
}

internal abstract class NotifyBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

internal sealed class ServerItem : NotifyBase
{
    private bool _isSelected;

    public ServerItem(ServerInfo info, List<JsonElement> devices)
    {
        Info = info;
        Devices = devices.Select(d => new DeviceItem(this, d)).OrderBy(d => d.Name).ToList();
    }

    public ServerInfo Info { get; }
    public List<DeviceItem> Devices { get; }
    public string Name => string.IsNullOrWhiteSpace(Info.Name) ? Info.Id : Info.Name;
    public string Version => string.IsNullOrWhiteSpace(Info.Version) ? "?" : Info.Version;
    public int CameraCount => Devices.Count;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }
}

internal sealed class DeviceItem : NotifyBase
{
    private bool _isSelected;

    public DeviceItem(ServerItem server, JsonElement device)
    {
        Server = server;
        Device = device;
    }

    public ServerItem Server { get; }
    public JsonElement Device { get; }
    public string ServerName => Server.Name;
    public string Id => NxClient.GetStringProperty(Device, "id");
    public string Name => NxClient.GetStringProperty(Device, "name");

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }
}

internal sealed class ResultItem
{
    public string ServerName { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public double? OldFps { get; init; }
    public double? NewFps { get; init; }
    public string Status { get; init; } = "";
    public string? ErrorMessage { get; init; }
}
