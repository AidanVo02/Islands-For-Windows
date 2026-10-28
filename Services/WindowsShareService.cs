using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinRT;

namespace DynamicIsland.Services
{
    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow([In] IntPtr appWindow);
    }

    public class WindowsShareService
    {
        private static readonly Lazy<WindowsShareService> _lazy = new(() => new WindowsShareService());
        public static WindowsShareService Instance => _lazy.Value;

        // IID của DataTransferManager
        private static readonly Guid _dtm_iid = new Guid("a5caee9b-8708-49d1-8d36-67d25a8da00c");

        private string[] _pendingFiles = Array.Empty<string>();
        private DataTransferManager? _dataTransferManager;
        private IntPtr _currentHwnd = IntPtr.Zero;

        private WindowsShareService() { }

        /// <summary>
        /// Khởi tạo và gắn kết DataTransferManager với HWND của cửa sổ WPF
        /// </summary>
        public void Initialize(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || hwnd == _currentHwnd) return;

            try
            {
                _currentHwnd = hwnd;
                var interop = DataTransferManager.As<IDataTransferManagerInterop>();
                Guid riid = _dtm_iid;
                IntPtr pUnk = interop.GetForWindow(hwnd, ref riid);
                _dataTransferManager = MarshalInterface<DataTransferManager>.FromAbi(pUnk);

                _dataTransferManager.DataRequested -= DataTransferManager_DataRequested;
                _dataTransferManager.DataRequested += DataTransferManager_DataRequested;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WindowsShareService] Init error: {ex.Message}");
            }
        }

        /// <summary>
        /// Mở giao diện chia sẻ gốc của Windows (hỗ trợ Phone Link, Nearby Share, v.v.)
        /// </summary>
        public async Task<bool> ShareFilesAsync(IntPtr hwnd, string[] filePaths)
        {
            if (filePaths == null || filePaths.Length == 0) return false;

            _pendingFiles = filePaths.Where(File.Exists).ToArray();
            if (_pendingFiles.Length == 0) return false;

            Initialize(hwnd);

            return await Task.Run(() =>
            {
                try
                {
                    var interop = DataTransferManager.As<IDataTransferManagerInterop>();
                    interop.ShowShareUIForWindow(hwnd);
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WindowsShareService] ShowShareUI error: {ex.Message}");
                    return false;
                }
            });
        }

        private async void DataTransferManager_DataRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            var deferral = args.Request.GetDeferral();
            try
            {
                var request = args.Request;
                request.Data.Properties.Title = _pendingFiles.Length == 1 
                    ? Path.GetFileName(_pendingFiles[0]) 
                    : $"{_pendingFiles.Length} tệp tin";
                request.Data.Properties.Description = "Chia sẻ từ Dynamic Island";

                var storageFiles = new List<IStorageItem>();
                foreach (var path in _pendingFiles)
                {
                    if (File.Exists(path))
                    {
                        var file = await StorageFile.GetFileFromPathAsync(path);
                        storageFiles.Add(file);
                    }
                }

                if (storageFiles.Count > 0)
                {
                    request.Data.SetStorageItems(storageFiles);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WindowsShareService] DataRequested error: {ex.Message}");
            }
            finally
            {
                deferral.Complete();
            }
        }
    }
}
