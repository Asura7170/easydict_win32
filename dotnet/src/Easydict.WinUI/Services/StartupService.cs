using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Easydict.WinUI.Services;

/// <summary>
/// Why Windows did not leave startup in the state the settings save requested.
/// <see cref="None"/> means the effective state matches the request.
/// </summary>
public enum StartupRefusal
{
    None,
    DisabledByUser,
    DisabledByPolicy,
    EnabledByPolicy,
    RunKeyUnavailable,
    Failed,
}

/// <summary>
/// Effective logon-start state after a save, plus why it differs from the request.
/// </summary>
public readonly record struct StartupApplyResult(bool Effective, StartupRefusal Refusal);

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
    /// Whether Windows will launch the app at logon. Does not enable, disable, or write settings.
    /// Null when the packaged task cannot be queried; callers keep the stored value.
    /// </summary>
    public static async Task<bool?> ReadEffectiveEnabledAsync()
    {
        try
        {
            if (!EasydictConditions.IsPackaged)
            {
                return ReadPendingRunValue();
            }

            var task = await StartupTask.GetAsync(TaskId);
            return IsEffectiveRunning(task.State);
        }
        catch (Exception ex)
        {
            CrashDiagnostics.Log($"[StartupService] ReadEffectiveEnabledAsync failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Applies the requested startup state and reports the effective one, i.e. whether
    /// Windows will actually launch the app at logon. Use the result to reconcile the
    /// settings and the UI: Windows deliberately refuses DisabledByUser re-enables
    /// requested from code (Task Manager owns that state).
    /// Effective is false after a successful disable.
    /// </summary>
    public static async Task<StartupApplyResult> SetEnabledAsync(bool enabled)
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
            // Report the actual effective state: a failed disable must not look successful.
            // Unpackaged builds have no package identity, so they ask the Run key instead.
            try
            {
                if (!EasydictConditions.IsPackaged)
                {
                    return ResultFor(enabled, ReadPendingRunValue(), StartupRefusal.RunKeyUnavailable);
                }

                var task = await StartupTask.GetAsync(TaskId);
                return FromTaskState(enabled, task.State);
            }
            catch
            {
                // Unknown either way. Do not report a disable as successful.
                return enabled
                    ? new StartupApplyResult(false, StartupRefusal.Failed)
                    : new StartupApplyResult(true, StartupRefusal.Failed);
            }
        }
    }

    private static async Task<StartupApplyResult> ApplyStartupTaskAsync(bool enabled)
    {
        var task = await StartupTask.GetAsync(TaskId);

        if (enabled)
        {
            if (IsEffectiveRunning(task.State))
            {
                return FromTaskState(enabled, task.State);
            }

            var result = await task.RequestEnableAsync();
            CrashDiagnostics.Log($"[StartupService] StartupTask '{TaskId}' enable -> {result}");
            return FromTaskState(enabled, result);
        }

        if (!IsEffectiveRunning(task.State))
        {
            // Disabled/DisabledByUser/DisabledByPolicy: already off; Windows documents no
            // behavior for Disable() on the states it owns, so never call it there.
            return FromTaskState(enabled, task.State);
        }

        task.Disable();
        // Docs make no freshness guarantee for State on the same instance after the
        // synchronous Disable(); official samples always re-query via GetAsync first.
        task = await StartupTask.GetAsync(TaskId);
        CrashDiagnostics.Log($"[StartupService] StartupTask '{TaskId}' disable -> {task.State}");
        return FromTaskState(enabled, task.State);
    }

    private static StartupApplyResult FromTaskState(bool requested, StartupTaskState state)
    {
        var refusal = state switch
        {
            StartupTaskState.DisabledByUser => StartupRefusal.DisabledByUser,
            StartupTaskState.DisabledByPolicy => StartupRefusal.DisabledByPolicy,
            StartupTaskState.EnabledByPolicy => StartupRefusal.EnabledByPolicy,
            _ => StartupRefusal.Failed,
        };
        return ResultFor(requested, IsEffectiveRunning(state), refusal);
    }

    private static StartupApplyResult ResultFor(bool requested, bool effective, StartupRefusal refusalWhenMismatch)
    {
        return effective == requested
            ? new StartupApplyResult(effective, StartupRefusal.None)
            : new StartupApplyResult(effective, refusalWhenMismatch);
    }

    private static bool IsEffectiveRunning(StartupTaskState state) => state
        is StartupTaskState.Enabled
        or StartupTaskState.EnabledByPolicy;

    private static StartupApplyResult ApplyRunKey(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true);
        if (key == null)
        {
            CrashDiagnostics.Log("[StartupService] HKCU Run key unavailable");
            // Nothing is registered, so a disable request is already satisfied.
            return new StartupApplyResult(false, enabled ? StartupRefusal.RunKeyUnavailable : StartupRefusal.None);
        }

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return new StartupApplyResult(false, StartupRefusal.None);
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            CrashDiagnostics.Log("[StartupService] Environment.ProcessPath empty; startup entry skipped");
            return new StartupApplyResult(false, StartupRefusal.RunKeyUnavailable);
        }

        // SettingsPage applies this on every save; rewrite only when the path changed.
        var value = $"\"{exePath}\"";
        if (!string.Equals(key.GetValue(ValueName) as string, value, StringComparison.Ordinal))
        {
            key.SetValue(ValueName, value);
        }
        return new StartupApplyResult(true, StartupRefusal.None);
    }

    // The Run-key path's answer lives in the registry itself; querying it needs no
    // package identity, so it is also the safe fallback inside the catch path.
    internal static bool ReadPendingRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
        return !string.IsNullOrEmpty(key?.GetValue(ValueName) as string);
    }

    // MSIX virtualizes HKCU writes into this package's private hive, so this delete can
    // never reach the real Run entry a portable install owns; kept as a safety net in
    // case registry write virtualization is disabled for this package (it is also the
    // only path that clears values the package's own virtualized hive contains).
    private static void CleanupLegacyRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
