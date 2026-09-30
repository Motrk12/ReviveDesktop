using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Revive.App.Services;

public static class Elevation
{
    public static bool IsAdministrator { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Starts a new elevated copy of the app. Returns false if the user said no at the UAC prompt.</summary>
    public static bool TryRestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
