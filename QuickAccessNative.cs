using System.Runtime.InteropServices;

namespace Vitals;

internal static class QuickAccessNative
{
    public const uint MfPopup = 0x0010;
    public const uint MfSeparator = 0x0800;
    private const uint MiimBitmap = 0x0080;
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MENUITEMINFO
    {
        public uint cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public nint hSubMenu;
        public nint hbmpChecked;
        public nint hbmpUnchecked;
        public nint dwItemData;
        public nint dwTypeData;
        public uint cch;
        public nint hbmpItem;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern nint SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi,
        uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(nint hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetMenuItemInfoW")]
    private static extern bool SetMenuItemInfo(nint hMenu, uint item, bool fByPosition, ref MENUITEMINFO lpmii);

    public static nint TryCreateMenuBitmap(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return 0;

        var info = new SHFILEINFO();
        nint result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), ShgfiIcon | ShgfiSmallIcon);
        if (result == 0 || info.hIcon == 0) return 0;

        try
        {
            if (!GetIconInfo(info.hIcon, out var iconInfo)) return 0;
            if (iconInfo.hbmMask != 0) NativeMethods.DeleteObject(iconInfo.hbmMask);
            return iconInfo.hbmColor;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    public static void SetMenuBitmap(nint menu, uint commandId, nint bitmap)
    {
        if (bitmap == 0) return;
        var info = new MENUITEMINFO
        {
            cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(),
            fMask = MiimBitmap,
            hbmpItem = bitmap,
        };
        SetMenuItemInfo(menu, commandId, false, ref info);
    }
}
