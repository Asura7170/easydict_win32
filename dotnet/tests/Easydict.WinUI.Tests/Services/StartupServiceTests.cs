using Easydict.WinUI.Services;
using FluentAssertions;
using Microsoft.Win32;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Covers the unpackaged HKCU\...\Run branch of StartupService. The test host has no
/// package identity (IsPackaged=false), so only the registry branch is reachable —
/// the MSIX StartupTask branch needs a packaged runtime and is verified by the MSIX
/// install checklist instead. Every test runs under a dedicated value name so the
/// machine's real "Easydict" Run entry (created by the app or the Inno installer)
/// is never touched.
/// </summary>
[Trait("Category", "WinUI")]
public sealed class StartupServiceTests : IDisposable
{
    private const string TestValueName = "EasydictStartupTests";
    private readonly string _originalValueName = StartupService.ValueName;

    public StartupServiceTests()
    {
        StartupService.ValueName = TestValueName;
        DisposeValue(); // start from a clean slate even if a previous run left residue
    }

    public void Dispose()
    {
        StartupService.ValueName = _originalValueName;
        DisposeValue();
    }

    [Fact]
    public async Task Enable_WritesQuotedPathOfTestHostExe()
    {
        var expected = $"\"{Environment.ProcessPath}\"";

        var effective = await StartupService.SetEnabledAsync(true);

        effective.Should().BeTrue();
        ReadValue().Should().Be(expected);
    }

    [Fact]
    public async Task Enable_Twice_KeepsValueUnchanged()
    {
        (await StartupService.SetEnabledAsync(true)).Should().BeTrue();
        var valueAfterFirst = ReadValue();

        (await StartupService.SetEnabledAsync(true)).Should().BeTrue();

        ReadValue().Should().Be(valueAfterFirst);
    }

    [Fact]
    public async Task Disable_AfterEnabled_ReportsNotRunning_AndRemovesValue()
    {
        (await StartupService.SetEnabledAsync(true)).Should().BeTrue();
        ReadValue().Should().NotBeNull();

        (await StartupService.SetEnabledAsync(false)).Should().BeFalse();

        ReadValue().Should().BeNull();
    }

    [Fact]
    public async Task Disable_WithNoEntry_DoesNotThrowAndReportsOff()
    {
        ReadValue().Should().BeNull();

        var effective = await StartupService.SetEnabledAsync(false);

        effective.Should().BeFalse();
    }

    [Fact]
    public async Task Enable_WithStalePathValue_RewritesToCurrentExe()
    {
        // Migration case: the Run value points at an old portable install.
        using (var key = Registry.CurrentUser.OpenSubKey(StartupService.RegistryPath, true))
        {
            key.Should().NotBeNull();
            key?.SetValue(TestValueName, "\"C:\\old\\Easydict.exe\"");
        }

        await StartupService.SetEnabledAsync(true);

        ReadValue().Should().Be($"\"{Environment.ProcessPath}\"");
    }

    [Fact]
    public void ReadPendingRunValue_ReportsPresence()
    {
        StartupService.ReadPendingRunValue().Should().BeFalse();

        using (var key = Registry.CurrentUser.OpenSubKey(StartupService.RegistryPath, true))
        {
            key.Should().NotBeNull();
            key?.SetValue(StartupService.ValueName, "\"C:\\anything.exe\"");
        }

        StartupService.ReadPendingRunValue().Should().BeTrue();
    }

    private static string? ReadValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupService.RegistryPath, writable: false);
        return key?.GetValue(TestValueName) as string;
    }

    private static void DisposeValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupService.RegistryPath, true);
        key?.DeleteValue(TestValueName, throwOnMissingValue: false);
    }
}
