using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.IO;
using System.Text.Json;

namespace PCIstemci
{
    public class DiscoveredDevice
    {
        public string Name { get; set; }
        public string IpAddress { get; set; }
        public string Pin { get; set; }
        
        public string PhoneId { get; set; }
    }

    public class PairedDevice
    {
        public string Name { get; set; }
    }

    public partial class MainWindow : Window
    {
        private const int TcpPort = 9999;
        private const int UdpPort = 9998;
        public ObservableCollection<DiscoveredDevice> DiscoveredDevices { get; set; }
        public ObservableCollection<PairedDevice> PairedDevices { get; set; }

        private string _pcId;
        private readonly string _pcName = Environment.MachineName;
        private readonly string _pairedDevicesFilePath;
        public string StartupMessage { get; set; }

        
        private UdpClient _udpClient;

        public MainWindow()
        {
            InitializeComponent();

            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string configDir = Path.Combine(appDataPath, "PcKontrolIstemci");
            Directory.CreateDirectory(configDir);
            _pairedDevicesFilePath = Path.Combine(configDir, "paired_devices.json");

            LoadPcId();

            DiscoveredDevices = new ObservableCollection<DiscoveredDevice>();
            DeviceListView.ItemsSource = DiscoveredDevices;

            PairedDevices = new ObservableCollection<PairedDevice>();
            PairedDeviceListView.ItemsSource = PairedDevices;
            LoadPairedDevices();

            StartUdpListener();

            Loaded += (s, e) => {
                if (!string.IsNullOrEmpty(StartupMessage))
                {
                    Log(StartupMessage);
                }
            };
            
            this.Closing += MainWindow_Closing;
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _udpClient?.Close();
            Log("UDP dinleyici durduruldu.");
        }

        private void StartUdpListener()
        {
            Task.Run(() =>
            {
                
                _udpClient = new UdpClient(UdpPort);
                Log("Cihaz keşfi için UDP dinleyicisi başlatıldı.");
                IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

                while (true)
                {
                    try
                    {
                        byte[] receivedBytes = _udpClient.Receive(ref remoteEP);
                        string receivedString = Encoding.UTF8.GetString(receivedBytes);
                        var parts = receivedString.Split(';');

                        Dispatcher.Invoke(() =>
                        {
                            
                            if (parts.Length == 5 && parts[0] == "DEVICE_ANNOUNCE")
                            {
                                var device = new DiscoveredDevice { Name = parts[1], IpAddress = parts[2], Pin = parts[3], PhoneId = parts[4] };
                                if (!DiscoveredDevices.Any(d => d.IpAddress == device.IpAddress))
                                {
                                    DiscoveredDevices.Add(device);
                                    Log($"Yeni cihaz bulundu: {device.Name} ({device.IpAddress})");
                                }
                            }
                        });
                    }
                    catch (SocketException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"UDP dinleme hatası: {ex.Message}");
                    }
                }
            });
        }

        private void LoadPcId()
        {
            string configDir = Path.GetDirectoryName(_pairedDevicesFilePath)!;
            string idFile = Path.Combine(configDir, "id.dat");

            if (File.Exists(idFile))
            {
                _pcId = File.ReadAllText(idFile);
            }
            else
            {
                _pcId = Guid.NewGuid().ToString();
                File.WriteAllText(idFile, _pcId);
            }
            Log($"PC Kimliği: {_pcId}");
        }

        private void LoadPairedDevices()
        {
            try
            {
                if (File.Exists(_pairedDevicesFilePath))
                {
                    string json = File.ReadAllText(_pairedDevicesFilePath);
                    var devices = JsonSerializer.Deserialize<List<PairedDevice>>(json);
                    PairedDevices.Clear();
                    foreach (var device in devices)
                    {
                        PairedDevices.Add(device);
                    }
                    Log($"{PairedDevices.Count} kayıtlı cihaz yüklendi.");
                }
            }
            catch (Exception ex)
            {
                Log($"Kayıtlı cihazlar yüklenirken hata oluştu: {ex.Message}");
            }
        }

        private void SavePairedDevices()
        {
            try
            {
                string json = JsonSerializer.Serialize(PairedDevices.ToList());
                File.WriteAllText(_pairedDevicesFilePath, json);
            }
            catch (Exception ex)
            {
                Log($"Kayıtlı cihazlar kaydedilirken hata oluştu: {ex.Message}");
            }
        }

        private void PairDevice(string deviceName)
        {
            if (!PairedDevices.Any(d => d.Name == deviceName))
            {
                PairedDevices.Add(new PairedDevice { Name = deviceName });
                SavePairedDevices();
                Log($"Cihaz kaydedildi: {deviceName}");
            }
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            TcpClient client = null;
            NetworkStream stream = null;
            string ipAddress = "";
            DiscoveredDevice selectedDevice = null;
            if (DeviceListView.SelectedItem is DiscoveredDevice device) { selectedDevice = device; ipAddress = device.IpAddress; } else { ipAddress = IpAddressTextBox.Text; }
            string pin = PinPasswordBox.Password;
            if (string.IsNullOrWhiteSpace(ipAddress) || string.IsNullOrWhiteSpace(pin)) { System.Windows.MessageBox.Show("Lütfen bir cihaz seçin veya IP adresi ile PIN kodunu girin.", "Hata", MessageBoxButton.OK, MessageBoxImage.Error); return; }

            ConnectButton.IsEnabled = false;
            UpdateStatus("Bağlanılıyor...", System.Windows.Media.Brushes.Orange);

            try
            {
                client = new TcpClient();
                await client.ConnectAsync(ipAddress, TcpPort).WaitAsync(TimeSpan.FromSeconds(5));
                stream = client.GetStream();

                string messageToSend = $"{pin};{_pcId};{_pcName}";
                byte[] pinMessage = Encoding.UTF8.GetBytes(messageToSend);
                await stream.WriteAsync(pinMessage, 0, pinMessage.Length);

                byte[] buffer = new byte[1024];
                var readTask = stream.ReadAsync(buffer, 0, buffer.Length);
                if (await Task.WhenAny(readTask, Task.Delay(5000)) != readTask)
                {
                    throw new Exception("Sunucudan PIN onayı alınamadı (zaman aşımı).");
                }

                int bytesRead = readTask.Result;
                string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                if (response == "PIN_OK")
                {
                    if (selectedDevice != null)
                    {
                        PairDevice(selectedDevice.Name);
                    }

                    Dispatcher.Invoke(() =>
                    {
                        UpdateStatus("Doğrulandı! Kontrol paneli açılıyor...", System.Windows.Media.Brushes.Green);
                        string deviceName = selectedDevice?.Name ?? "Manuel Bağlantı";
                        var controlPanel = new ControlPanelWindow(client, stream, deviceName);
                        controlPanel.Show();
                        this.Close();
                    });
                }
                else
                {
                    throw new Exception("Sunucu tarafından PIN reddedildi.");
                }
            }
            catch (Exception ex)
            {
                stream?.Close(); client?.Close(); UpdateStatus("Bağlantı başarısız!", System.Windows.Media.Brushes.Red); Log($"Hata: {ex.Message}"); ConnectButton.IsEnabled = true;
            }
        }

        private void PairedDeviceListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if ((sender as System.Windows.Controls.ListView).SelectedItem is PairedDevice pairedDevice)
            {
                var discovered = DiscoveredDevices.FirstOrDefault(d => d.Name == pairedDevice.Name);
                if (discovered != null)
                {
                    DeviceListView.SelectedItem = discovered;
                    Log($"Kayıtlı cihaz '{pairedDevice.Name}' ağda bulundu ve seçildi.");
                }
                else
                {
                    Log($"Kayıtlı cihaz '{pairedDevice.Name}' şu anda ağda aktif değil.");
                }
            }
        }

        private void DeviceListView_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (DeviceListView.SelectedItem is DiscoveredDevice selectedDevice) { PinPasswordBox.Password = selectedDevice.Pin; Log($"Cihaz seçildi: {selectedDevice.Name}. PIN otomatik olarak dolduruldu."); } }
        private void RefreshButton_Click(object sender, RoutedEventArgs e) { DiscoveredDevices.Clear(); Log("Cihaz listesi temizlendi. Yeni cihazlar aranıyor..."); }
        private void UpdateStatus(string message, System.Windows.Media.Brush color) { Dispatcher.Invoke(() => { StatusLabel.Content = message; StatusLabel.Foreground = color; }); }
        private void Log(string message) { Dispatcher.Invoke(() => { LogTextBlock.Text += $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}"; }); }
    }
}
