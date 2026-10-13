using System.IO;
using System.Net.Http;
using Lasero.Avalonia.Services;
using Lasero.Avalonia.ViewModels;
using Lasero.Core.LaseroApi;
using Lasero.Core.Materials;
using ReactiveUI.Builder;

namespace Lasero.Avalonia.Tests;

/// <summary>
/// Regression tests for Settings' two distinct persistence behaviors (matches SettingsWindow.xaml.cs):
/// the three safety checkboxes are deferred-commit (only written to disk on Save), while work-area and
/// laser-profile fields commit to the in-memory settings object live and are only flushed to disk when
/// the underlying store's Save() is called — same as Machine Control's and Materials' own tests.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")] // SettingsViewModel now depends on AccountViewModel.
public sealed class SettingsViewModelTests
{
    private static AppSettingsStore NewSettingsStore() =>
        new(Path.Combine(Path.GetTempPath(), $"lasero-test-{Guid.NewGuid():N}.json"));

    private static AccountViewModel NewAccount()
    {
        var http = new HttpClient();
        var sessionStore = new SessionStore(Path.Combine(Path.GetTempPath(), $"lasero-test-session-{Guid.NewGuid():N}.dat"));
        return new AccountViewModel(new LaseroAuthClient(http), new LaseroAccountClient(http), sessionStore);
    }

    [Fact]
    public void SafetyTogglesAreDeferredUntilSave()
    {
        TestApp.EnsureInitialized();
        var store = NewSettingsStore();
        store.Current.Safety.ConfirmSoftReset = true;
        var connection = new ConnectionViewModel(new FakeLaserMachine(), store);
        var materials = new MaterialCatalogViewModel(store);
        var settings = new SettingsViewModel(store, connection, materials, NewAccount());

        settings.ConfirmSoftReset = false; // flip in the UI only

        Assert.True(store.Current.Safety.ConfirmSoftReset); // not written yet

        settings.Save.Execute().Subscribe();

        Assert.False(store.Current.Safety.ConfirmSoftReset); // now persisted
    }

    [Fact]
    public void SaveIsRejectedWhenWorkAreaIsInvalid()
    {
        TestApp.EnsureInitialized();
        var store = NewSettingsStore();
        var connection = new ConnectionViewModel(new FakeLaserMachine(), store);
        var materials = new MaterialCatalogViewModel(store);
        var settings = new SettingsViewModel(store, connection, materials, NewAccount());

        connection.WorkAreaWidthMm = -10; // Connection's own setter ignores non-positive values,
        // so the store keeps its last valid width unless something else corrupts it directly.
        Assert.True(store.Current.Machine.WorkAreaWidthMm > 0);

        store.Current.Machine.WorkAreaWidthMm = double.NaN; // simulate a corrupted/invalid stored value
        var raised = false;
        settings.Saved += () => raised = true;
        settings.Save.Execute().Subscribe();

        Assert.False(raised);
        Assert.NotEmpty(settings.ErrorMessage);
    }

    [Fact]
    public void SavingNavigatesBackViaSavedEvent()
    {
        TestApp.EnsureInitialized();
        var store = NewSettingsStore();
        var connection = new ConnectionViewModel(new FakeLaserMachine(), store);
        var materials = new MaterialCatalogViewModel(store);
        var settings = new SettingsViewModel(store, connection, materials, NewAccount());

        var raised = false;
        settings.Saved += () => raised = true;
        settings.Save.Execute().Subscribe();

        Assert.True(raised);
    }

    [Fact]
    public void LaserProfileSelectionPersistsImmediatelyAndIsSharedWithMaterialsFilter()
    {
        TestApp.EnsureInitialized();
        var store = NewSettingsStore();
        var materials = new MaterialCatalogViewModel(store);

        materials.SelectedTechnology = LaserTechnology.Infrared;
        materials.SelectedPowerWatts = 40;

        Assert.Equal(LaserTechnology.Infrared, store.Current.Machine.LaserTechnology);
        Assert.Equal(40, store.Current.Machine.LaserPowerWatts);
    }
}
