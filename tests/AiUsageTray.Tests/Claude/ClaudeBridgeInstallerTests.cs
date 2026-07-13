using System.IO;
using System.Text.Json.Nodes;
using AiUsageTray.Providers.Claude;
using AiUsageTray.Tests.TestSupport;
using Xunit;

namespace AiUsageTray.Tests.Claude;

[Collection("IsolatedAppData")]
public class ClaudeBridgeInstallerTests
{
    private static (ClaudeBridgeInstaller Installer, ClaudeSettingsService Settings, string SettingsPath) CreateInstaller(string tempRoot)
    {
        var settingsPath = Path.Combine(tempRoot, ".claude", "settings.json");
        var settingsService = new ClaudeSettingsService(settingsPath);
        var installer = new ClaudeBridgeInstaller(settingsService);
        return (installer, settingsService, settingsPath);
    }

    [Fact]
    public void Install_NoExistingStatusLine_AddsBridgeCommand()
    {
        using var isolated = new IsolatedAppData();
        var (installer, settings, settingsPath) = CreateInstaller(isolated.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, """{ "someOtherSetting": true }""");

        var result = installer.Install();

        Assert.True(result.Success);
        var written = settings.Read();
        Assert.True(written["someOtherSetting"]!.GetValue<bool>()); // untouched property preserved
        var statusLine = ClaudeSettingsService.GetStatusLine(written);
        Assert.Equal("command", statusLine!["type"]!.GetValue<string>());
        Assert.Contains("claude-statusline-bridge.ps1", statusLine["command"]!.GetValue<string>());

        var metadata = ClaudeBridgeInstaller.ReadMetadata();
        Assert.True(metadata!.Installed);
        Assert.False(metadata.HadOriginalStatusLine);
    }

    [Fact]
    public void Install_WithExistingStatusLine_BacksUpOriginalCommand()
    {
        using var isolated = new IsolatedAppData();
        var (installer, _, settingsPath) = CreateInstaller(isolated.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, """
        { "statusLine": { "type": "command", "command": "node ./my-statusline.js", "padding": 2 } }
        """);

        var result = installer.Install();

        Assert.True(result.Success);
        var metadata = ClaudeBridgeInstaller.ReadMetadata();
        Assert.True(metadata!.HadOriginalStatusLine);
        Assert.Equal("node ./my-statusline.js", metadata.OriginalCommand);
        var originalNode = JsonNode.Parse(metadata.OriginalStatusLineJson!)!;
        Assert.Equal(2, originalNode["padding"]!.GetValue<int>());
    }

    [Fact]
    public void Install_CalledTwice_IsIdempotentAndDoesNotWrapBridge()
    {
        using var isolated = new IsolatedAppData();
        var (installer, settings, settingsPath) = CreateInstaller(isolated.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, "{}");

        installer.Install();
        var secondResult = installer.Install();

        Assert.True(secondResult.Success);
        var statusLine = ClaudeSettingsService.GetStatusLine(settings.Read());
        var command = statusLine!["command"]!.GetValue<string>();

        // Must reference the bridge script exactly once, never nested/wrapped.
        var occurrences = command.Split("claude-statusline-bridge.ps1").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void Remove_RestoresExactOriginalStatusLine()
    {
        using var isolated = new IsolatedAppData();
        var (installer, settings, settingsPath) = CreateInstaller(isolated.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        const string originalStatusLineJson = """{"type":"command","command":"node ./my-statusline.js","padding":2}""";
        File.WriteAllText(settingsPath, $$"""{ "statusLine": {{originalStatusLineJson}} }""");

        installer.Install();
        var removeResult = installer.Remove();

        Assert.True(removeResult.Success);
        var statusLine = ClaudeSettingsService.GetStatusLine(settings.Read());
        Assert.Equal("node ./my-statusline.js", statusLine!["command"]!.GetValue<string>());
        Assert.Equal(2, statusLine["padding"]!.GetValue<int>());
    }

    [Fact]
    public void Remove_NoOriginalStatusLine_RemovesPropertyEntirely()
    {
        using var isolated = new IsolatedAppData();
        var (installer, settings, settingsPath) = CreateInstaller(isolated.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, "{}");

        installer.Install();
        installer.Remove();

        var written = settings.Read();
        Assert.False(written.ContainsKey("statusLine"));
    }

    [Fact]
    public void Install_InvalidExistingSettingsJson_FailsWithoutModifyingFile()
    {
        using var isolated = new IsolatedAppData();
        var (installer, _, settingsPath) = CreateInstaller(isolated.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        const string invalidJson = "{ this is not valid json";
        File.WriteAllText(settingsPath, invalidJson);

        var result = installer.Install();

        Assert.False(result.Success);
        Assert.Equal(invalidJson, File.ReadAllText(settingsPath));
    }

    [Fact]
    public void BuildBridgeCommand_PathWithSpaces_IsQuoted()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"AI Usage Tray Tests {Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            AiUsageTray.Infrastructure.AppPaths.SetRootForTests(tempRoot);
            var installer = new ClaudeBridgeInstaller(new ClaudeSettingsService(Path.Combine(tempRoot, "settings.json")));

            var command = installer.BuildBridgeCommand();

            Assert.StartsWith("powershell -NoProfile -ExecutionPolicy Bypass -File \"", command);
            Assert.EndsWith("\"", command);
            Assert.Contains(" ", command.Substring(command.IndexOf('"')));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void GetStatus_AfterInstall_ReportsInstalled()
    {
        using var isolated = new IsolatedAppData();
        var (installer, _, settingsPath) = CreateInstaller(isolated.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, "{}");

        installer.Install();

        Assert.Equal(ClaudeBridgeStatus.Installed, installer.GetStatus());
    }

    [Fact]
    public void GetStatus_StatusLineOverwrittenByOtherTool_ReportsDamaged()
    {
        using var isolated = new IsolatedAppData();
        var (installer, settings, settingsPath) = CreateInstaller(isolated.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, "{}");

        installer.Install();

        var current = settings.Read();
        current["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = "something-else.exe" };
        settings.Write(current);

        Assert.Equal(ClaudeBridgeStatus.Damaged, installer.GetStatus());
    }
}
