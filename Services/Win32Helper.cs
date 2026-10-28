using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Forms;

namespace DynamicIsland.Services
{
    public static class Win32Helper
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        /// <summary>
        /// Ẩn cửa sổ khỏi danh sách Alt+Tab bằng WS_EX_TOOLWINDOW
        /// </summary>
        public static void SetToolWindow(Window window)
        {
            var helper = new WindowInteropHelper(window);
            IntPtr hWnd = helper.Handle;

            int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            exStyle |= WS_EX_TOOLWINDOW;
            SetWindowLong(hWnd, GWL_EXSTYLE, exStyle);
        }

        /// <summary>
        /// Căn vị trí cửa sổ lên chính giữa mép trên của màn hình chỉ định kèm khoảng cách tùy chỉnh
        /// </summary>
        public static void PositionOnScreen(Window window, Screen screen, double topOffset = 0, double horizontalOffset = 0, bool alwaysOnTop = true)
        {
            var helper = new WindowInteropHelper(window);
            IntPtr hWnd = helper.Handle;

            var source = PresentationSource.FromVisual(window);
            double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            // Dùng Width/Height gốc của WPF nhân với tỷ lệ DPI của màn hình
            int physicalWidth = (int)(window.Width * dpiX);
            int physicalHeight = (int)(window.Height * dpiY);

            // Căn giữa theo bề ngang màn hình và vị trí Y
            int posX = screen.Bounds.X + (screen.Bounds.Width - physicalWidth) / 2 + (int)(horizontalOffset * dpiX);
            int posY = screen.Bounds.Y + (int)(topOffset * dpiY);

            IntPtr insertAfter = alwaysOnTop ? HWND_TOPMOST : HWND_NOTOPMOST;
            SetWindowPos(hWnd, insertAfter, posX, posY, physicalWidth, physicalHeight, SWP_SHOWWINDOW);
        }

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        public static bool IsKeyDown(int vKey)
        {
            return (GetAsyncKeyState(vKey) & 0x8000) != 0;
        }
    }
}
