using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Easydict.WinUI.Services;

/// <summary>
/// Applies the "Launch at Windows startup" setting.
/// Packaged (MSIX) builds use the manifest-declared windows.startupTask; portable
/// builds fall back to the classic HKCU...\CurrentVersion\Run registry entry.
/// </summary>
public static class StartupService
{
    // Must match TaskId="EasydictStartup" on the uap5:StartupTask in Package.appxmanifest.
    private const string TaskId = "EasydictStartup";

    // Internal (not const) so Easydict.WinUI.Tests can write under its own value name.
    internal static string ValueName = "Easydict";
    internal const string RegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Applies the requested startup state and reports the effective one, i.e. whether
    /// Windows will actually launch the app at logon. Use the result to reconcile the
    /// settings and the UI: Windows deliberately refuses DisabledByUser re-enables
    /// requested from code (Settings &gt; Startup apps / Task Manager owns that state).
    /// Returns FALSE after a successful disable.
    /// </summary>
    public static async Task<bool> SetEnabledAsync(bool enabled)
    {
        try
        {
            if (!EasydictConditions.IsPackaged)
            {
                return ApplyRunKey(enabled);
            }

            // Cleanup first: a cosmetic registry failure must not taint the effective result.
            try
            {
                CleanupLegacyRunKey();
            }
            catch (Exception ex)
            {
                CrashDiagnostics.Log($"[StartupService] Legacy Run-key cleanup failed: {ex.Message}");
            }
            return await ApplyStartupTaskAsync(enabled);
        }
        catch (Exception ex)
        {
            CrashDiagnostics.Log($"[StartupService] SetEnabledAsync({enabled}) failed: {ex.Message}");
            // Report the ACTUAL effective state instead of a silent false: a failed
            // disable must not be mistaken for a successful one.
            try
            {
                var task = await StartupTask.GetAsync(TaskId);
                return IsEffectiveRunning(task.State);
            }
            catch
            {
                return false;
            }
        }
    }

    private static async Task<bool> ApplyStartupTaskAsync(bool enabled)
    {
        var task = await StartupTask.GetAsync(TaskId);

        if (enabled)
        {
            if (IsEffectiveRunning(task.State))
            {
                return true;
            }

            var result = await task.RequestEnableAsync();
            CrashDiagnostics.Log($"[StartupService] StartupTask '{TaskId}' enable -> {result}");
            return IsEffectiveRunning(result);
        }

        if (!IsEffectiveRunning(task.State))
        {
            // Disabled/DisabledByUser/DisabledByPolicy: already off; Windows documents no
            // behavior for Disable() on the states it owns, so never call it there.
            return false;
        }

        task.Disable();
        CrashDiagnostics.Log($"[StartupService] StartupTask '{TaskId}' disable -> {task.State}");
        return IsEffectiveRunning(task.State);
    }

    private static bool IsEffectiveRunning(StartupTaskState state) => state
        is StartupTaskState.Enabled
        or StartupTaskState.EnabledByPolicy;

    private static bool ApplyRunKey(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true);
        if (key == null)
        {
            CrashDiagnostics.Log("[StartupService] HKCU Run key unavailable");
            return false;
        }

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return false;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            CrashDiagnostics.Log("[StartupService] Environment.ProcessPath empty; startup entry skipped");
            return false;
        }

        // SettingsPage applies this on every save; rewrite only when the path changed.
        var value = $"\"{exePath}\"";
        if (!string.Equals(key.GetValue(ValueName) as string, value, StringComparison.Ordinal))
        {
            key.SetValue(ValueName, value);
        }
        return true;
    }

    // Portable legacy installs that were upgraded to the MSIX leave an HKCU Run entry
    // behind; Windows would execute it at logon next to the StartupTask.
    private static void CleanupLegacyRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
