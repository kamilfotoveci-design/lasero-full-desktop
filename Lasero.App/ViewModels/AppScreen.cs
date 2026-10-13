namespace Lasero.App.ViewModels;

/// <summary>Which top-level screen MainWindow is currently showing in its main content area.</summary>
public enum AppScreen
{
    Home,
    Designer,
    Device,
    Chat,
}

/// <summary>The pages of the Zařízení screen, left to right in its one tab strip. Connection is the live machine page
/// (connection, position, jog); the other four are the device settings, edited as one draft and saved together.</summary>
public enum DeviceScreenTab
{
    Connection,
    Dimensions,
    Basic,
    GCode,
    More,
}
