namespace Lazarus.Features;

/// <summary>
/// Set-Location never moves the pwsh process directory, so the user adds this line to $PROFILE once (Lazarus only copies it):
/// every location change writes $env:LAZARUS_CWD, which <see cref="Peb"/> reads. It replaces another LocationChangedAction, if any.
/// </summary>
public static class PowerShellHook
{
    public const string Line =
        "$ExecutionContext.InvokeCommand.LocationChangedAction = { param($s, $e) if ($e.NewPath.Provider.Name -eq 'FileSystem') { $env:LAZARUS_CWD = $e.NewPath.ProviderPath } }";
}
