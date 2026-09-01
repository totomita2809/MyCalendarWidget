using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

public static class WindowHelper
{
    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    public static void PinToDesktop(Window window)
    {
        IntPtr hWnd = new WindowInteropHelper(window).Handle;
        // Tìm lớp "Progman" - nơi quản lý các icon desktop
        IntPtr progman = FindWindow("Progman", null);
        SetParent(hWnd, progman);
    }
}