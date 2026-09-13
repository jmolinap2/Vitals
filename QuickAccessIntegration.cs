using System.Diagnostics;
using System.Runtime.InteropServices;
using Vitals.Shared;
using static Vitals.NativeMethods;

namespace Vitals;

/// <summary>
/// Extiende el menú Win32 que ya crea PillWindow sin acoplar el lanzador al
/// renderizado de la píldora. Un hook del mismo hilo observa WM_INITMENUPOPUP
/// y agrega el submenú justo antes de que Windows lo muestre.
/// </summary>
internal static class QuickAccessIntegration
{
    private const int WhCallWndProc = 4;
    private const uint WmInitMenuPopup = 0x0117;
    private const uint WmUninitMenuPopup = 0x0125;
    private const uint MfByPosition = 0x0400;
    private const uint MfPopup = 0x0010;
    private const uint MfSeparator = 0x0800;
    private const int QuickCommandBase = 2000;
    private const int MaxQuickAccessItems = 200;

    private static nint _hook;
    private static List<QuickAccessItem> _activeItems = [];
    private static readonly List<nint> MenuBitmaps = [];

    [StructLayout(LayoutKind.Sequential)]
    private struct CWPSTRUCT
    {
        public nint lParam;
        public nint wParam;
        public uint message;
        public nint hwnd;
    }

    [DllImport("user32.dll")]
    private static extern unsafe nint SetWindowsHookEx(int idHook, delegate* unmanaged<int, nint, nint, nint> lpfn,
        nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "InsertMenuW")]
    private static extern bool InsertMenu(nint hMenu, uint uPosition, uint uFlags, nint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(nint hMenu);

    [DllImport("user32.dll")]
    private static extern uint GetMenuItemID(nint hMenu, int nPos);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(nint hWnd, string lpText, string lpCaption, uint uType);

    public static unsafe void InstallForCurrentThread()
    {
        if (_hook != 0) return;
        var callback = (delegate* unmanaged<int, nint, nint, nint>)&HookProc;
        _hook = SetWindowsHookEx(WhCallWndProc, callback, 0, GetCurrentThreadId());
    }

    public static void Uninstall()
    {
        CleanupMenuBitmaps();
        if (_hook == 0) return;
        UnhookWindowsHookEx(_hook);
        _hook = 0;
    }

    [UnmanagedCallersOnly]
    private static nint HookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && lParam != 0)
        {
            var message = Marshal.PtrToStructure<CWPSTRUCT>(lParam);

            if (message.message == WmInitMenuPopup)
                TryPopulateRootMenu(message.wParam);
            else if (message.message == WM_COMMAND)
                TryLaunchCommand(message.hwnd, message.wParam);
            else if (message.message == WmUninitMenuPopup)
                CleanupMenuBitmaps();
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static void TryPopulateRootMenu(nint menu)
    {
        // El menú raíz de Vitals nace siempre con Configuración + Salir.
        // Esta comprobación evita inyectar el lanzador dentro de su propio submenú.
        if (menu == 0 || GetMenuItemCount(menu) != 2) return;
        if (GetMenuItemID(menu, 0) != (uint)IdSettings || GetMenuItemID(menu, 1) != (uint)IdExit) return;

        CleanupMenuBitmaps();
        _activeItems = QuickAccessStore.Load()
            .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Target))
            .OrderBy(x => x.Order)
            .Take(MaxQuickAccessItems)
            .ToList();

        if (_activeItems.Count == 0) return;

        nint quickMenu = CreatePopupMenu();
        for (int i = 0; i < _activeItems.Count; i++)
        {
            uint commandId = (uint)(QuickCommandBase + i);
            var item = _activeItems[i];
            AppendMenu(quickMenu, MF_STRING, (nint)commandId, item.Name);

            string? iconSource = item.IconMode == QuickAccessIconMode.Custom && !string.IsNullOrWhiteSpace(item.IconPath)
                ? item.IconPath
                : item.Target;
            nint bitmap = QuickAccessNative.TryCreateMenuBitmap(iconSource);
            if (bitmap != 0)
            {
                QuickAccessNative.SetMenuBitmap(quickMenu, commandId, bitmap);
                MenuBitmaps.Add(bitmap);
            }
        }

        InsertMenu(menu, 0, MfByPosition | MfPopup, quickMenu, "Accesos rápidos");
        InsertMenu(menu, 1, MfByPosition | MfSeparator, 0, null);
    }

    private static void TryLaunchCommand(nint hwnd, nint wParam)
    {
        int commandId = unchecked((int)(long)wParam) & 0xFFFF;
        int index = commandId - QuickCommandBase;
        if ((uint)index >= (uint)_activeItems.Count) return;

        var item = _activeItems[index];
        try
        {
            var startInfo = BuildStartInfo(item);
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            MessageBox(hwnd, $"No se pudo abrir '{item.Name}'.\n\n{ex.Message}", "Vitals", 0x10);
        }
    }

    private static ProcessStartInfo BuildStartInfo(QuickAccessItem item)
    {
        string target = Environment.ExpandEnvironmentVariables(item.Target.Trim());
        string arguments = Environment.ExpandEnvironmentVariables(item.Arguments ?? string.Empty);
        string workingDirectory = Environment.ExpandEnvironmentVariables(item.WorkingDirectory ?? string.Empty);

        if (target.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
        {
            return new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -File \"{target}\" {arguments}".TrimEnd(),
                WorkingDirectory = ResolveWorkingDirectory(target, workingDirectory),
                UseShellExecute = true,
            };
        }

        return new ProcessStartInfo
        {
            FileName = target,
            Arguments = arguments,
            WorkingDirectory = ResolveWorkingDirectory(target, workingDirectory),
            UseShellExecute = true,
        };
    }

    private static string ResolveWorkingDirectory(string target, string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        if (File.Exists(target)) return Path.GetDirectoryName(target) ?? string.Empty;
        return string.Empty;
    }

    private static void CleanupMenuBitmaps()
    {
        foreach (nint bitmap in MenuBitmaps)
            DeleteObject(bitmap);
        MenuBitmaps.Clear();
    }
}
