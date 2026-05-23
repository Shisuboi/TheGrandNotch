using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using TheGrandNotch.Settings;

namespace TheGrandNotch.Services;

/// <summary>
/// Gère le démarrage automatique de l'application au logon Windows selon trois méthodes :
///  - Registre (clé Run HKCU)        : sans droits admin
///  - Dossier Démarrage (raccourci)  : sans droits admin
///  - Tâche planifiée au logon        : droits admin, démarre tôt, peut afficher une fenêtre
///    (un vrai service Windows ne peut pas dessiner d'UI : la tâche planifiée est l'équivalent correct)
/// </summary>
public static class StartupManager
{
    private const string AppName = "TheGrandNotch";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string TaskName = "TheGrandNotch";

    private static string ExePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName!;

    private static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), AppName + ".lnk");

    /// <summary>
    /// Détecte la méthode réellement active sur le système (source de vérité).
    /// </summary>
    public static StartupMethod GetCurrentMethod()
    {
        if (IsScheduledTaskEnabled()) return StartupMethod.ScheduledTask;
        if (IsRegistryEnabled()) return StartupMethod.Registry;
        if (IsStartupFolderEnabled()) return StartupMethod.StartupFolder;
        return StartupMethod.None;
    }

    /// <summary>
    /// Applique la méthode demandée (et désactive les autres).
    /// Retourne false si l'opération a échoué (ex : UAC refusé pour la tâche planifiée).
    /// </summary>
    public static bool SetMethod(StartupMethod method)
    {
        var current = GetCurrentMethod();
        if (current == method)
            return true;

        // Toujours nettoyer les méthodes sans admin
        try { DisableRegistry(); } catch { }
        try { DisableStartupFolder(); } catch { }

        // Retirer la tâche planifiée si on s'en éloigne (nécessite UAC)
        if (IsScheduledTaskEnabled() && method != StartupMethod.ScheduledTask)
        {
            if (!RunScheduledTaskCommand($"/Delete /TN \"{TaskName}\" /F"))
                return false;
        }

        switch (method)
        {
            case StartupMethod.Registry:
                EnableRegistry();
                break;
            case StartupMethod.StartupFolder:
                EnableStartupFolder();
                break;
            case StartupMethod.ScheduledTask:
                if (!EnableScheduledTask())
                    return false;
                break;
            case StartupMethod.None:
            default:
                break;
        }

        return true;
    }

    // ----- Registre (HKCU\...\Run) -----

    private static bool IsRegistryEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        return key?.GetValue(AppName) != null;
    }

    private static void EnableRegistry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(AppName, $"\"{ExePath}\"");
    }

    private static void DisableRegistry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
        if (key?.GetValue(AppName) != null)
            key.DeleteValue(AppName, false);
    }

    // ----- Dossier Démarrage (raccourci .lnk via WScript.Shell) -----

    private static bool IsStartupFolderEnabled() => File.Exists(ShortcutPath);

    private static void EnableStartupFolder()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
            return;

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(ShortcutPath);
            shortcut.TargetPath = ExePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(ExePath);
            shortcut.IconLocation = ExePath + ",0";
            shortcut.Description = "The Grand Notch";
            shortcut.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void DisableStartupFolder()
    {
        if (File.Exists(ShortcutPath))
            File.Delete(ShortcutPath);
    }

    // ----- Tâche planifiée au logon (privilèges élevés, via schtasks.exe) -----

    private static bool IsScheduledTaskEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool EnableScheduledTask()
    {
        // /RL HIGHEST → privilèges élevés ; /SC ONLOGON → au logon de l'utilisateur courant
        var args = $"/Create /TN \"{TaskName}\" /TR \"\\\"{ExePath}\\\"\" /SC ONLOGON /RL HIGHEST /F";
        return RunScheduledTaskCommand(args);
    }

    /// <summary>
    /// Exécute schtasks.exe en mode élevé (UAC). Retourne false si l'utilisateur refuse l'élévation
    /// ou si la commande échoue.
    /// </summary>
    private static bool RunScheduledTaskCommand(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch
        {
            // Win32Exception 1223 = UAC refusé
            return false;
        }
    }
}
