using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace DynamicIsland.Services
{
    public enum LockKeyType
    {
        CapsLock,
        NumLock,
        ScrollLock,
        InputMethod
    }

    public class LockKeyChangedEventArgs : EventArgs
    {
        public LockKeyType KeyType { get; set; }
        public bool IsOn { get; set; }
        public string Title { get; set; } = "";
        public string StatusText { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public string IconUnicode { get; set; } = "";
    }

    public class LockKeyService : IDisposable
    {
        private static readonly Lazy<LockKeyService> _lazy = new(() => new LockKeyService());
        public static LockKeyService Instance => _lazy.Value;

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private const int VK_CAPITAL = 0x14; // Caps Lock
        private const int VK_NUMLOCK = 0x90; // Num Lock
        private const int VK_SCROLL = 0x91;  // Scroll Lock
        private const int VK_CONTROL = 0x11;
        private const int VK_SHIFT = 0x10;
        private const int VK_MENU = 0x12;   // Alt
        private const int VK_SPACE = 0x20;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_Z = 0x5A;

        private IntPtr _hookId = IntPtr.Zero;
        private LowLevelKeyboardProc? _proc;
        private bool _isDisposed;

        // Bộ gõ Tiếng Việt / Tiếng Anh (Mặc định ở VN là Tiếng Việt)
        private bool _isVietnamese = true;
        private DateTime _lastInputSwitchTime = DateTime.MinValue;

        public event EventHandler<LockKeyChangedEventArgs>? LockKeyChanged;

        private LockKeyService() { }

        public void Start()
        {
            if (_hookId != IntPtr.Zero) return;

            _proc = HookCallback;
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            if (curModule?.ModuleName != null)
            {
                _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        public void Stop()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                int vkCode = Marshal.ReadInt32(lParam);

                if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    HandleKeyUp(vkCode);
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private void HandleKeyUp(int vkCode)
        {
            try
            {
                // 1. Caps Lock
                if (vkCode == VK_CAPITAL)
                {
                    bool isOn = (GetKeyState(VK_CAPITAL) & 1) != 0;
                    LockKeyChanged?.Invoke(this, new LockKeyChangedEventArgs
                    {
                        KeyType = LockKeyType.CapsLock,
                        IsOn = isOn,
                        Title = "Caps Lock",
                        StatusText = isOn ? "BẬT" : "TẮT",
                        Subtitle = isOn ? "Viết hoa chữ cái" : "Viết thường chữ cái",
                        IconUnicode = "\uE879" // Key / Caps
                    });
                    return;
                }

                // 2. Num Lock
                if (vkCode == VK_NUMLOCK)
                {
                    bool isOn = (GetKeyState(VK_NUMLOCK) & 1) != 0;
                    LockKeyChanged?.Invoke(this, new LockKeyChangedEventArgs
                    {
                        KeyType = LockKeyType.NumLock,
                        IsOn = isOn,
                        Title = "Num Lock",
                        StatusText = isOn ? "BẬT" : "TẮT",
                        Subtitle = isOn ? "Bật bàn phím số" : "Tắt bàn phím số",
                        IconUnicode = "\uE752" // Calculator / Numbers
                    });
                    return;
                }

                // 3. Scroll Lock
                if (vkCode == VK_SCROLL)
                {
                    bool isOn = (GetKeyState(VK_SCROLL) & 1) != 0;
                    LockKeyChanged?.Invoke(this, new LockKeyChangedEventArgs
                    {
                        KeyType = LockKeyType.ScrollLock,
                        IsOn = isOn,
                        Title = "Scroll Lock",
                        StatusText = isOn ? "BẬT" : "TẮT",
                        Subtitle = isOn ? "Khóa cuộn trang" : "Mở cuộn trang",
                        IconUnicode = "\uE72E" // Lock
                    });
                    return;
                }

                // 4. Chuyển đổi bộ gõ Tiếng Việt / Tiếng Anh (Ctrl + Shift hoặc Alt + Z)
                bool isCtrlShift = (vkCode == VK_SHIFT && (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) ||
                                  (vkCode == VK_CONTROL && (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0);

                bool isAltZ = vkCode == VK_Z && ((GetAsyncKeyState(VK_MENU) & 0x8000) != 0);

                if (isCtrlShift || isAltZ)
                {
                    // Debounce 300ms tránh kích hoạt kép khi nhả từng phím
                    if ((DateTime.Now - _lastInputSwitchTime).TotalMilliseconds > 350)
                    {
                        _lastInputSwitchTime = DateTime.Now;
                        _isVietnamese = !_isVietnamese;

                        LockKeyChanged?.Invoke(this, new LockKeyChangedEventArgs
                        {
                            KeyType = LockKeyType.InputMethod,
                            IsOn = _isVietnamese,
                            Title = _isVietnamese ? "Bộ gõ Tiếng Việt" : "Bộ gõ Tiếng Anh",
                            StatusText = _isVietnamese ? "VIE" : "ENG",
                            Subtitle = _isVietnamese ? "[V] Telex / VNI" : "[E] English Mode",
                            IconUnicode = "\uE774" // Globe / Language
                        });
                    }
                    return;
                }

                // 5. Windows Language Switch (Win + Space)
                bool isWinSpace = vkCode == VK_SPACE &&
                                 ((GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0);

                if (isWinSpace)
                {
                    Task.Run(async () =>
                    {
                        await Task.Delay(100);
                        CheckWindowsKeyboardLayout();
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LockKeyService] HandleKeyUp error: {ex.Message}");
            }
        }

        private void CheckWindowsKeyboardLayout()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                uint threadId = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
                IntPtr hkl = GetKeyboardLayout(threadId);
                int lcid = (int)hkl & 0xFFFF;

                bool isVie = lcid == 0x042A;
                _isVietnamese = isVie;

                LockKeyChanged?.Invoke(this, new LockKeyChangedEventArgs
                {
                    KeyType = LockKeyType.InputMethod,
                    IsOn = isVie,
                    Title = isVie ? "Bộ gõ Tiếng Việt" : "Bộ gõ Tiếng Anh",
                    StatusText = isVie ? "VIE" : "ENG",
                    Subtitle = isVie ? "Bàn phím Windows (vi-VN)" : "Bàn phím Windows (en-US)",
                    IconUnicode = "\uE774"
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LockKeyService] CheckLayout error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Stop();
        }

        #region Win32 P/Invoke
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);
        #endregion
    }
}
