using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using System.Drawing.Imaging;
using LibreHardwareMonitor.Hardware;
using System.Linq;

namespace PCIstemci
{
    public partial class ControlPanelWindow : Window
    {
        private TcpClient _client;
        private NetworkStream _stream;
        private string _deviceName;
        private bool _isReceivingFile = false;
        private string _receivingFilePath = "";
        private long _receivingFileSize = 0;
        private long _totalBytesReceived = 0;
        private FileStream _fileStream = null;
        private bool _isWaitingForScreenshotAck = false;
        private byte[] _screenshotBytesToSend = null;

        // Donanım takibi için
        private Computer _computer;
        private IHardware _cpu;
        private IHardware _gpu;
        private IHardware _network;
        private IHardware _ram;
        private System.Timers.Timer _perfTimer;

        [DllImport("user32.dll")]
        public static extern void LockWorkStation();
        [DllImport("Powrprof.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageW(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
        private const int APPCOMMAND_VOLUME_MUTE = 0x80000;
        private const int APPCOMMAND_VOLUME_UP = 0xA0000;
        private const int APPCOMMAND_VOLUME_DOWN = 0x90000;
        private const int APPCOMMAND_MEDIA_PLAY_PAUSE = 0xE0000;
        private const int APPCOMMAND_MEDIA_NEXTTRACK = 0xB0000;
        private const int APPCOMMAND_MEDIA_PREVIOUSTRACK = 0xC0000;
        private const int WM_APPCOMMAND = 0x319;
        private const int SC_MONITORPOWER = 0xF170;
        private const int MONITOR_OFF = 2;

        [DllImport("user32.dll")]
        static extern void mouse_event(int dwFlags, int dx, int dy, int dwData, int dwExtraInfo);
        private const int MOUSEEVENTF_LEFTDOWN = 0x02;
        private const int MOUSEEVENTF_LEFTUP = 0x04;
        private const int MOUSEEVENTF_RIGHTDOWN = 0x08;
        private const int MOUSEEVENTF_RIGHTUP = 0x10;


        public ControlPanelWindow(TcpClient client, NetworkStream stream, string deviceName)
        {
            InitializeComponent();
            _client = client;
            _stream = stream;
            _deviceName = deviceName;

            TitleLabel.Content = $"Bağlı Cihaz: {deviceName}";
            this.Closing += ControlPanelWindow_Closing;

            InitializeHardwareMonitor();

            _ = ListenForMessages();
        }

        private void InitializeHardwareMonitor()
        {
            try
            {
                _computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = true,
                    IsNetworkEnabled = true
                };
                _computer.Open();

                _cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                _gpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia || h.HardwareType == HardwareType.GpuAmd);
                _ram = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);
                _network = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Network);

                _perfTimer = new System.Timers.Timer(2000);
                _perfTimer.Elapsed += async (sender, e) => await SendPerformanceUpdate();
                _perfTimer.AutoReset = true;
                _perfTimer.Enabled = true;
            }
            catch (Exception ex)
            {
                LogActivity($"Donanım izleyici başlatılamadı: {ex.Message}");
            }
        }

        private async Task SendPerformanceUpdate()
        {
            if (_client.Connected && _stream.CanWrite)
            {
                try
                {
                    _cpu?.Update();
                    _gpu?.Update();
                    _ram?.Update();
                    _network?.Update();

                    float cpuUsage = _cpu?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name == "CPU Total")?.Value ?? 0;
                    float ramUsed = _ram?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name == "Memory Used")?.Value ?? 0;
                    float cpuTemp = _cpu?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Core"))?.Value ?? 0;
                    float gpuTemp = _gpu?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("GPU Core"))?.Value ?? 0;
                    float cpuPower = _cpu?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Name == "CPU Package")?.Value ?? 0;
                    float gpuPower = _gpu?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Name == "GPU Power")?.Value ?? 0;
                    float networkDown = (_network?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Throughput && s.Name == "Download Speed")?.Value ?? 0) * 8 / 1024 / 1024; // Mbps
                    float networkUp = (_network?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Throughput && s.Name == "Upload Speed")?.Value ?? 0) * 8 / 1024 / 1024; // Mbps

                    string message = $"PERF_UPDATE;{cpuUsage:F1};{ramUsed:F1};{networkDown:F2};{networkUp:F2};{cpuTemp:F0};{gpuTemp:F0};{cpuPower:F0};{gpuPower:F0}";
                    byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                    await _stream.WriteAsync(messageBytes, 0, messageBytes.Length);
                }
                catch { }
            }
        }

        private void ProcessCommand(string command)
        {
            LogActivity($"Komut alindi: {command}");
            var commandParts = command.Split(':');
            string mainCommand = commandParts[0];

            switch (mainCommand)
            {
                case "MUTE": MuteSystemVolume(); break;
                case "VOLUME_UP": SendMessageW(new IntPtr(-1), WM_APPCOMMAND, new IntPtr(0), new IntPtr(APPCOMMAND_VOLUME_UP)); break;
                case "VOLUME_DOWN": SendMessageW(new IntPtr(-1), WM_APPCOMMAND, new IntPtr(0), new IntPtr(APPCOMMAND_VOLUME_DOWN)); break;
                case "SET_VOLUME":
                    if (commandParts.Length > 1 && int.TryParse(commandParts[1], out int volume)) SetSystemVolume(volume);
                    break;
                case "MEDIA_PLAYPAUSE": SendMessageW(new IntPtr(-1), WM_APPCOMMAND, new IntPtr(0), new IntPtr(APPCOMMAND_MEDIA_PLAY_PAUSE)); break;
                case "MEDIA_NEXT": SendMessageW(new IntPtr(-1), WM_APPCOMMAND, new IntPtr(0), new IntPtr(APPCOMMAND_MEDIA_NEXTTRACK)); break;
                case "MEDIA_PREVIOUS": SendMessageW(new IntPtr(-1), WM_APPCOMMAND, new IntPtr(0), new IntPtr(APPCOMMAND_MEDIA_PREVIOUSTRACK)); break;
                case "LOCK": LockWorkStation(); break;
                case "TASK_MANAGER": Process.Start("taskmgr"); break;
                case "NOTEPAD": Process.Start("notepad"); break;
                case "CALCULATOR": Process.Start("calc"); break;
                case "SLEEP": SetSuspendState(false, true, true); break;
                case "HIBERNATE": SetSuspendState(true, true, true); break;
                case "SIGNOUT": Process.Start("shutdown", "/l"); break;
                case "SCREEN_OFF": SendMessageW(new IntPtr(0xFFFF), 0x0112, new IntPtr(SC_MONITORPOWER), new IntPtr(MONITOR_OFF)); break;
                case "SHUTDOWN": ShutdownSystem(); break;
                case "RESTART": RestartSystem(); break;
                case "SCREENSHOT": TakeAndSendScreenshotAsync(); break;
                case "MOUSE_MOVE":
                    if (commandParts.Length > 2 && int.TryParse(commandParts[1], out int dx) && int.TryParse(commandParts[2], out int dy))
                    {
                        Forms.Cursor.Position = new Drawing.Point(Forms.Cursor.Position.X + dx, Forms.Cursor.Position.Y + dy);
                    }
                    break;
                case "MOUSE_CLICK":
                    if (commandParts.Length > 1)
                    {
                        if (commandParts[1] == "LEFT") mouse_event(MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                        else if (commandParts[1] == "RIGHT") mouse_event(MOUSEEVENTF_RIGHTDOWN | MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0);
                    }
                    break;
                default: LogActivity("Bilinmeyen komut."); break;
            }
        }

        private void SetSystemVolume(int level)
        {
            for (int i = 0; i < 50; i++)
            {
                SendMessageW(new IntPtr(-1), WM_APPCOMMAND, new IntPtr(0), new IntPtr(APPCOMMAND_VOLUME_DOWN));
            }
            for (int i = 0; i < level / 2; i++)
            {
                SendMessageW(new IntPtr(-1), WM_APPCOMMAND, new IntPtr(0), new IntPtr(APPCOMMAND_VOLUME_UP));
            }
        }
        private async Task ListenForMessages()
        {
            byte[] buffer = new byte[8192];
            try
            {
                while (true)
                {
                    int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    if (_isReceivingFile)
                    {
                        if (_fileStream != null)
                        {
                            await _fileStream.WriteAsync(buffer, 0, bytesRead);
                            _totalBytesReceived += bytesRead;

                            if (_totalBytesReceived >= _receivingFileSize)
                            {
                                Dispatcher.Invoke(() => LogActivity($"Dosya alımı tamamlandı: {_receivingFilePath}"));
                                _fileStream.Close();
                                _fileStream = null;
                                _isReceivingFile = false;
                            }
                        }
                    }
                    else
                    {
                        string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                        if (_isWaitingForScreenshotAck && message == "FILE_OK_TO_SEND")
                        {
                            _isWaitingForScreenshotAck = false;
                            if (_screenshotBytesToSend != null)
                            {
                                await _stream.WriteAsync(_screenshotBytesToSend, 0, _screenshotBytesToSend.Length);
                                _screenshotBytesToSend = null;
                                Dispatcher.Invoke(() => LogActivity("Ekran görüntüsü gönderildi."));
                            }
                        }
                        else
                        {
                            ProcessMessage(message);
                        }
                    }
                }
            }
            catch (Exception) { }

            if (_fileStream != null)
            {
                _fileStream.Close();
                _fileStream = null;
            }
            HandleDisconnection();
        }
        private void ProcessMessage(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogActivity($"Gelen mesaj: '{message}'");
                var parts = message.Split(';');
                if (parts.Length > 0)
                {
                    string messageType = parts[0];
                    if (messageType == "CMD" && parts.Length >= 2)
                    {
                        ProcessCommand(parts[1]);
                    }
                    else if (messageType == "FILE_START" && parts.Length == 3)
                    {
                        string fileName = parts[1];
                        if (long.TryParse(parts[2], out long fileSize))
                        {
                            HandleFileTransferRequest(fileName, fileSize);
                        }
                    }
                    else
                    {
                        LogActivity("Bilinmeyen mesaj formatı.");
                    }
                }
            });
        }
        private async void TakeAndSendScreenshotAsync()
        {
            LogActivity("Ekran görüntüsü alınıyor...");
            try
            {
                var bounds = Forms.Screen.PrimaryScreen.Bounds;
                using var bitmap = new Drawing.Bitmap(bounds.Width, bounds.Height);
                using var g = Drawing.Graphics.FromImage(bitmap);
                g.CopyFromScreen(Drawing.Point.Empty, Drawing.Point.Empty, bounds.Size);

                await using var ms = new MemoryStream();
                bitmap.Save(ms, Drawing.Imaging.ImageFormat.Png);

                _screenshotBytesToSend = ms.ToArray();
                _isWaitingForScreenshotAck = true;

                LogActivity("Ekran görüntüsü alındı. Telefona gönderim isteği yapılıyor...");

                string fileName = $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                string header = $"FILE_START;{fileName};{_screenshotBytesToSend.Length}";
                byte[] headerBytes = Encoding.UTF8.GetBytes(header);
                await _stream.WriteAsync(headerBytes, 0, headerBytes.Length);
            }
            catch (Exception ex)
            {
                LogActivity($"Ekran görüntüsü alınırken/gönderilirken hata: {ex.Message}");
                _isWaitingForScreenshotAck = false;
                _screenshotBytesToSend = null;
            }
        }
        private async void HandleFileTransferRequest(string fileName, long fileSize)
        {
            var saveFileDialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = fileName,
                Title = "Gelen Dosyayı Kaydet"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                _receivingFilePath = saveFileDialog.FileName;
                _receivingFileSize = fileSize;
                _totalBytesReceived = 0;

                try
                {
                    _fileStream = new FileStream(_receivingFilePath, FileMode.Create, FileAccess.Write);
                    _isReceivingFile = true;
                    LogActivity($"Dosya alımı başlatıldı: {fileName} ({fileSize} bytes).");

                    byte[] response = Encoding.UTF8.GetBytes("FILE_OK_TO_SEND");
                    await _stream.WriteAsync(response, 0, response.Length);
                }
                catch (Exception ex)
                {
                    LogActivity($"Dosya oluşturulurken hata: {ex.Message}");
                    _isReceivingFile = false;
                }
            }
            else
            {
                LogActivity("Kullanıcı dosya kaydetme işlemini iptal etti.");
                byte[] response = Encoding.UTF8.GetBytes("FILE_CANCEL");
                await _stream.WriteAsync(response, 0, response.Length);
            }
        }
        private void MuteSystemVolume() { SendMessageW(new IntPtr(-1), WM_APPCOMMAND, new IntPtr(0), new IntPtr(APPCOMMAND_VOLUME_MUTE)); }
        private void ShutdownSystem() { Process.Start("shutdown", "/s /t 1"); }
        private void RestartSystem() { Process.Start("shutdown", "/r /t 1"); }
        private void LogActivity(string message) { ActivityLogTextBlock.Text += $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}"; }
        private void HandleDisconnection() { _perfTimer?.Stop(); _perfTimer?.Dispose(); _computer?.Close(); if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(HandleDisconnection); return; } if (!this.IsActive && !this.IsVisible) return; var main = new MainWindow(); main.StartupMessage = $"'{_deviceName}' ile bağlantı kesildi."; main.Show(); this.Closing -= ControlPanelWindow_Closing; this.Close(); }
        private void ControlPanelWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e) { _perfTimer?.Stop(); _perfTimer?.Dispose(); _computer?.Close(); _stream?.Close(); _client?.Close(); }
    }
}
