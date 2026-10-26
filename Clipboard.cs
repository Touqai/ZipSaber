using System;
using System.Runtime.InteropServices;

namespace ZipSaber
{
    /// <summary>Reads text from the Windows clipboard.</summary>
    internal static class Clipboard
    {
        internal static string GetText()
        {
            // Unity's own clipboard accessor (IMGUI module isn't referenced, so via reflection)
            try
            {
                var t = Type.GetType("UnityEngine.GUIUtility, UnityEngine.IMGUIModule");
                var p = t?.GetProperty("systemCopyBuffer");
                if (p?.GetValue(null) is string s && !string.IsNullOrEmpty(s)) return s;
            }
            catch { }

            // Fallback: Win32 clipboard
            try { return ReadWin32(); } catch { return null; }
        }

        private const uint CF_UNICODETEXT = 13;
        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint uFormat);
        [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);

        private static string ReadWin32()
        {
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
            if (!OpenClipboard(IntPtr.Zero)) return null;
            try
            {
                IntPtr h = GetClipboardData(CF_UNICODETEXT);
                if (h == IntPtr.Zero) return null;
                IntPtr p = GlobalLock(h);
                if (p == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(p); }
                finally { GlobalUnlock(h); }
            }
            finally { CloseClipboard(); }
        }
    }
}
