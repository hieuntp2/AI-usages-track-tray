using System.IO;
using AiUsageTray.Infrastructure;
using AiUsageTray.Models;
using AiUsageTray.Services;
using AiUsageTray.Tests.TestSupport;
using Xunit;

namespace AiUsageTray.Tests.Shared;

[Collection("IsolatedAppData")]
public class SettingsServiceTests
{
    [Fact]
    public void Load_NoExistingFile_ReturnsDefaults()
    {
        using var isolated = new IsolatedAppData();

        var service = new SettingsService();

        Assert.Equal(AppSettings.CurrentSchemaVersion, service.Current.SchemaVersion);
        Assert.True(service.Current.StartMinimized);
    }

    [Fact]
    public void Load_OldSchemaVersion_MigratesToCurrent()
    {
        using var isolated = new IsolatedAppData();
        Directory.CreateDirectory(AppPaths.ConfigDir);
        File.WriteAllText(AppPaths.SettingsFile, """{ "schemaVersion": 0, "startWithWindows": true }""");

        var service = new SettingsService();

        Assert.Equal(AppSettings.CurrentSchemaVersion, service.Current.SchemaVersion);
        Assert.True(service.Current.StartWithWindows); // pre-migration data preserved
    }

    [Fact]
    public void Load_CorruptedFile_RecoversWithDefaultsAndBacksUpOriginal()
    {
        using var isolated = new IsolatedAppData();
        Directory.CreateDirectory(AppPaths.ConfigDir);
        File.WriteAllText(AppPaths.SettingsFile, "{ this is not valid json !!");

        var service = new SettingsService();

        Assert.Equal(AppSettings.CurrentSchemaVersion, service.Current.SchemaVersion);
        Assert.True(Directory.Exists(AppPaths.BackupsDir));
        Assert.NotEmpty(Directory.GetFiles(AppPaths.BackupsDir));
    }

    [Fact]
    public void Save_ThenReload_RoundTrips()
    {
        using var isolated = new IsolatedAppData();
        var service = new SettingsService();

        service.Update(s =>
        {
            s.StartWithWindows = true;
            s.RefreshIntervalSeconds = 120;
        });

        var reloaded = new SettingsService();

        Assert.True(reloaded.Current.StartWithWindows);
        Assert.Equal(120, reloaded.Current.RefreshIntervalSeconds);
    }

    [Fact]
    public void GetOrAddProvider_UnknownProvider_CreatesDisabledEntry()
    {
        using var isolated = new IsolatedAppData();
        var service = new SettingsService();

        var provider = service.Current.GetOrAddProvider("some-new-provider");

        Assert.False(provider.Enabled);
        Assert.Equal("some-new-provider", provider.ProviderId);
    }
}
