using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicIsland.Services
{
    public class LocalSendDeviceInfo
    {
        [JsonPropertyName("alias")]
        public string Alias { get; set; } = "Phone";

        [JsonPropertyName("version")]
        public string Version { get; set; } = "2.0";

        [JsonPropertyName("deviceModel")]
        public string? DeviceModel { get; set; }

        [JsonPropertyName("deviceType")]
        public string? DeviceType { get; set; }

        [JsonPropertyName("fingerprint")]
        public string? Fingerprint { get; set; }

        [JsonPropertyName("port")]
        public int Port { get; set; } = 53317;

        [JsonPropertyName("protocol")]
        public string Protocol { get; set; } = "http";

        [JsonIgnore]
        public string IpAddress { get; set; } = string.Empty;

        public string DisplayName => !string.IsNullOrWhiteSpace(DeviceModel) 
            ? $"{Alias} ({DeviceModel})" 
            : Alias;
    }

    public class LocalSendService
    {
        private static readonly Lazy<LocalSendService> _lazy = new(() => new LocalSendService());
        public static LocalSendService Instance => _lazy.Value;

        private readonly HttpClient _httpClient;

        private LocalSendService()
        {
            var handler = new HttpClientHandler
            {
                // Bỏ qua chứng chỉ SSL tự ký nếu LocalSend bật HTTPS
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(25)
            };
        }

        /// <summary>
        /// Thử kết nối tới một địa chỉ IP chỉ định
        /// </summary>
        public async Task<LocalSendDeviceInfo?> TestDeviceAsync(string ip, int port = 53317)
        {
            if (string.IsNullOrWhiteSpace(ip)) return null;

            // Thử HTTP trước, nếu thất bại thử HTTPS
            string[] protocols = { "http", "https" };
            foreach (var proto in protocols)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    string url = $"{proto}://{ip}:{port}/api/localsend/v2/info";
                    var response = await _httpClient.GetAsync(url, cts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync(cts.Token);
                        var device = JsonSerializer.Deserialize<LocalSendDeviceInfo>(json);
                        if (device != null)
                        {
                            device.IpAddress = ip;
                            device.Port = port;
                            device.Protocol = proto;
                            return device;
                        }
                    }
                }
                catch
                {
                    // Tiếp tục thử HTTPS
                }
            }

            return null;
        }

        /// <summary>
        /// Tự động tìm kiếm thiết bị LocalSend trong mạng Wi-Fi (UDP Multicast + Quét nhanh Subnet)
        /// </summary>
        public async Task<LocalSendDeviceInfo?> DiscoverDeviceAsync(string? fallbackIp = null, int port = 53317)
        {
            // 1. Nếu có IP chỉ định hoặc IP đã lưu, ưu tiên kiểm tra trước
            if (!string.IsNullOrWhiteSpace(fallbackIp))
            {
                var test = await TestDeviceAsync(fallbackIp, port);
                if (test != null) return test;
            }

            // 2. Thử UDP Multicast Discovery
            var udpResult = await DiscoverViaMulticastAsync(port, timeoutMs: 1500);
            if (udpResult != null) return udpResult;

            // 3. Quét nhanh dải IP mạng cục bộ (Subnet Probe)
            return await ScanSubnetForDeviceAsync(port);
        }

        private async Task<LocalSendDeviceInfo?> DiscoverViaMulticastAsync(int port, int timeoutMs)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var udpClient = new UdpClient();
                    udpClient.EnableBroadcast = true;
                    udpClient.Client.ReceiveTimeout = timeoutMs;

                    var multicastEp = new IPEndPoint(IPAddress.Parse("224.0.0.167"), port);

                    // Gói tin thông báo LocalSend v2
                    string announcement = JsonSerializer.Serialize(new
                    {
                        alias = "Dynamic Island",
                        version = "2.0",
                        deviceModel = "Windows PC",
                        deviceType = "desktop",
                        fingerprint = "dynamic-island-" + Environment.MachineName,
                        port = 53317,
                        protocol = "http",
                        download = false,
                        announce = true
                    });

                    byte[] data = Encoding.UTF8.GetBytes(announcement);
                    udpClient.Send(data, data.Length, multicastEp);

                    var remoteEp = new IPEndPoint(IPAddress.Any, 0);
                    DateTime start = DateTime.Now;

                    while ((DateTime.Now - start).TotalMilliseconds < timeoutMs)
                    {
                        try
                        {
                            byte[] received = udpClient.Receive(ref remoteEp);
                            string text = Encoding.UTF8.GetString(received);
                            var device = JsonSerializer.Deserialize<LocalSendDeviceInfo>(text);
                            if (device != null && device.Fingerprint != ("dynamic-island-" + Environment.MachineName))
                            {
                                device.IpAddress = remoteEp.Address.ToString();
                                return device;
                            }
                        }
                        catch
                        {
                            break;
                        }
                    }
                }
                catch
                {
                    // Multicast có thể bị chặn bởi Router hoặc Windows Firewall
                }

                return null;
            });
        }

        private async Task<LocalSendDeviceInfo?> ScanSubnetForDeviceAsync(int port)
        {
            try
            {
                // Tìm IP của card mạng Wi-Fi/Ethernet hiện tại
                string? localIp = null;
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus == OperationalStatus.Up && 
                        (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet))
                    {
                        foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (addr.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr.Address))
                            {
                                localIp = addr.Address.ToString();
                                break;
                            }
                        }
                    }
                    if (localIp != null) break;
                }

                if (string.IsNullOrEmpty(localIp)) return null;

                int lastDot = localIp.LastIndexOf('.');
                if (lastDot <= 0) return null;
                string subnetPrefix = localIp.Substring(0, lastDot + 1);

                // Quét song song các IP phổ biến trong mạng cục bộ (1 đến 254)
                var tasks = new List<Task<LocalSendDeviceInfo?>>();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));

                for (int i = 1; i <= 254; i++)
                {
                    string targetIp = subnetPrefix + i;
                    if (targetIp == localIp) continue;

                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            string url = $"http://{targetIp}:{port}/api/localsend/v2/info";
                            var res = await _httpClient.GetAsync(url, cts.Token);
                            if (res.IsSuccessStatusCode)
                            {
                                string json = await res.Content.ReadAsStringAsync(cts.Token);
                                var dev = JsonSerializer.Deserialize<LocalSendDeviceInfo>(json);
                                if (dev != null)
                                {
                                    dev.IpAddress = targetIp;
                                    dev.Port = port;
                                    dev.Protocol = "http";
                                    return dev;
                                }
                            }
                        }
                        catch {}
                        return null;
                    }));
                }

                while (tasks.Count > 0)
                {
                    var completed = await Task.WhenAny(tasks);
                    var result = await completed;
                    if (result != null)
                    {
                        cts.Cancel(); // Dừng các task còn lại khi đã tìm thấy thiết bị
                        return result;
                    }
                    tasks.Remove(completed);
                }
            }
            catch {}

            return null;
        }

        /// <summary>
        /// Gửi danh sách tệp tin qua LocalSend API v2
        /// </summary>
        public async Task<(bool Success, string Message, string DeviceAlias)> SendFilesAsync(
            string[] filePaths, 
            string targetIp, 
            int port = 53317, 
            string protocol = "http",
            Action<double>? onProgress = null)
        {
            if (filePaths == null || filePaths.Length == 0)
            {
                return (false, "Không có tệp nào để gửi", "");
            }

            // Lọc ra các file thực sự tồn tại
            var validFiles = filePaths.Where(File.Exists).ToList();
            if (validFiles.Count == 0)
            {
                return (false, "Tệp tin không tồn tại trên máy", "");
            }

            try
            {
                // Bước 1: Chuẩn bị metadata cho /api/localsend/v2/prepare-upload
                var fileMetadataMap = new Dictionary<string, object>();
                long totalSize = 0;

                for (int i = 0; i < validFiles.Count; i++)
                {
                    var fi = new FileInfo(validFiles[i]);
                    string fileId = $"file_{i}_{Guid.NewGuid():N}";
                    totalSize += fi.Length;

                    fileMetadataMap[fileId] = new
                    {
                        id = fileId,
                        fileName = fi.Name,
                        size = fi.Length,
                        fileType = GetMimeType(fi.Extension)
                    };
                }

                var preparePayload = new
                {
                    info = new
                    {
                        alias = "Dynamic Island",
                        version = "2.0",
                        deviceModel = "Windows PC",
                        deviceType = "desktop",
                        fingerprint = "dynamic-island-" + Environment.MachineName,
                        port = 53317,
                        protocol = protocol
                    },
                    files = fileMetadataMap
                };

                string prepareUrl = $"{protocol}://{targetIp}:{port}/api/localsend/v2/prepare-upload";
                string jsonBody = JsonSerializer.Serialize(preparePayload);
                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                onProgress?.Invoke(10);
                var prepareResponse = await _httpClient.PostAsync(prepareUrl, content);

                if (!prepareResponse.IsSuccessStatusCode)
                {
                    if (prepareResponse.StatusCode == HttpStatusCode.Forbidden)
                    {
                        return (false, "Bị từ chối trên điện thoại", "");
                    }
                    if (prepareResponse.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        return (false, "Cần nhập mã PIN LocalSend", "");
                    }
                    return (false, $"Lỗi kết nối ({prepareResponse.StatusCode})", "");
                }

                string responseJson = await prepareResponse.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseJson);
                var root = doc.RootElement;

                if (!root.TryGetProperty("sessionId", out var sessionProp))
                {
                    return (false, "Phản hồi từ LocalSend không hợp lệ", "");
                }
                string sessionId = sessionProp.GetString() ?? "";

                if (!root.TryGetProperty("files", out var filesTokenProp))
                {
                    return (false, "Không nhận được token tệp tin", "");
                }

                // Bước 2: Upload từng file qua /api/localsend/v2/upload
                int fileIndex = 0;
                long transferredBytes = 0;

                foreach (var fileProp in filesTokenProp.EnumerateObject())
                {
                    string fileId = fileProp.Name;
                    string token = fileProp.Value.GetString() ?? "";
                    string filePath = validFiles[fileIndex];
                    var fileInfo = new FileInfo(filePath);

                    string uploadUrl = $"{protocol}://{targetIp}:{port}/api/localsend/v2/upload?sessionId={Uri.EscapeDataString(sessionId)}&fileId={Uri.EscapeDataString(fileId)}&token={Uri.EscapeDataString(token)}";

                    using var fileStream = File.OpenRead(filePath);
                    using var streamContent = new StreamContent(fileStream);
                    streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                    var uploadResponse = await _httpClient.PostAsync(uploadUrl, streamContent);
                    if (!uploadResponse.IsSuccessStatusCode)
                    {
                        return (false, $"Lỗi tải tệp {fileInfo.Name}", "");
                    }

                    transferredBytes += fileInfo.Length;
                    double percent = 10 + ((double)transferredBytes / Math.Max(1, totalSize)) * 85;
                    onProgress?.Invoke(Math.Min(95, percent));

                    fileIndex++;
                }

                onProgress?.Invoke(100);
                return (true, "Đã gửi thành công!", "");
            }
            catch (Exception ex)
            {
                return (false, $"Lỗi truyền file: {ex.Message}", "");
            }
        }

        private static string GetMimeType(string extension)
        {
            return extension.ToLower() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".mp4" => "video/mp4",
                ".mp3" => "audio/mpeg",
                ".pdf" => "application/pdf",
                ".zip" => "application/zip",
                ".txt" => "text/plain",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                _ => "application/octet-stream"
            };
        }
    }
}
