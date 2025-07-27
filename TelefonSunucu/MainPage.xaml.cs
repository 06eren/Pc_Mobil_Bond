using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Devices;

namespace TelefonSunucu
{
    public class PairedDevice
    {
        public string PcId { get; set; }
        public string Name { get; set; }
    }

    public partial class MainPage : ContentPage
    {
        private TcpListener tcpListener;
        private const int TcpPort = 9999;
        private const int UdpPort = 9998;
        private CancellationTokenSource broadcastCancellationTokenSource;
        private string currentPin;
        private readonly Random random = new Random();

        private string _phoneId;
        public ObservableCollection<PairedDevice> PairedDevices { get; set; }

        public MainPage()
        {
            InitializeComponent();
            PairedDevices = new ObservableCollection<PairedDevice>();
            PairedDevicesCollectionView.ItemsSource = PairedDevices;
            LoadPhoneId();
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            if (tcpListener == null)
            {
                DisplayIpAddress();
                StatusLabel.Text = "Sunucu başlatılmadı.";
                StatusLabel.TextColor = Colors.Red;
                PinLabel.Text = "PIN: -";
                StartServerButton.IsEnabled = true;
            }
            LoadPairedDevices();
        }

        // DÜZELTME: Sunucuyu başlatma mantığı güncellendi.
        private void OnStartServerClicked(object sender, EventArgs e)
        {
            try
            {
                if (tcpListener != null) { Log("Sunucu zaten çalışıyor."); return; }

                string localIpAddress = GetLocalIpAddress();
                if (string.IsNullOrEmpty(localIpAddress))
                {
                    DisplayAlert("Hata", "Wi-Fi IP adresi bulunamadı. Lütfen ağ bağlantınızı kontrol edin.", "Tamam");
                    return;
                }

                currentPin = random.Next(100000, 999999).ToString();

                // Sunucuyu IPAddress.Any yerine doğrudan cihazın Wi-Fi IP'si üzerinden başlat.
                tcpListener = new TcpListener(IPAddress.Parse(localIpAddress), TcpPort);
                tcpListener.Start();
                Task.Run(() => ListenForClients());

                broadcastCancellationTokenSource = new CancellationTokenSource();
                Task.Run(() => BroadcastPresence(broadcastCancellationTokenSource.Token));

                MainThread.BeginInvokeOnMainThread(() => {
                    StatusLabel.Text = $"Sunucu {localIpAddress}:{TcpPort} adresinde çalışıyor.";
                    StatusLabel.TextColor = Colors.Green;
                    PinLabel.Text = $"PIN: {currentPin}";
                    StartServerButton.IsEnabled = false;
                    Log($"Sunucu başlatıldı. Yeni PIN: {currentPin}");
                });
            }
            catch (Exception ex)
            {
                Log($"Sunucu başlatılırken hata oluştu: {ex.Message}");
                DisplayAlert("Hata", $"Sunucu başlatılamadı: {ex.Message}", "Tamam");
            }
        }

        // Diğer tüm metotlar aynı kalır.
        private void ConnectToPairedDevice_Clicked(object sender, EventArgs e) { if (PairedDevicesCollectionView.SelectedItem is PairedDevice selectedPc) { try { using (var udpClient = new UdpClient()) { udpClient.EnableBroadcast = true; var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, UdpPort); string message = $"CONNECT_REQUEST;{_phoneId};{selectedPc.PcId}"; byte[] data = Encoding.UTF8.GetBytes(message); udpClient.Send(data, data.Length, broadcastEndpoint); Log($"'{selectedPc.Name}' cihazına bağlantı isteği gönderildi."); DisplayAlert("İstek Gönderildi", $"'{selectedPc.Name}' adlı PC'ye bağlantı isteği gönderildi. Lütfen PC'den gelecek yanıtı bekleyin.", "Tamam"); } } catch (Exception ex) { Log($"Bağlantı isteği gönderilirken hata: {ex.Message}"); DisplayAlert("Hata", "Bağlantı isteği gönderilemedi. Ağ bağlantınızı kontrol edin.", "Tamam"); } } else { DisplayAlert("Hata", "Lütfen listeden bir cihaz seçin.", "Tamam"); } }
        private void PairedDevicesCollectionView_SelectionChanged(object sender, SelectionChangedEventArgs e) { ConnectToPairedDeviceButton.IsEnabled = e.CurrentSelection.Any(); }
        private async Task HandleClient(TcpClient client) { var clientEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "Bilinmeyen istemci"; NetworkStream stream = null; try { stream = client.GetStream(); byte[] buffer = new byte[4096]; var readTask = stream.ReadAsync(buffer, 0, buffer.Length); if (await Task.WhenAny(readTask, Task.Delay(5000)) != readTask) { Log($"Veri alınamadı (zaman aşımı)."); client.Close(); return; } int bytesRead = readTask.Result; var messageParts = Encoding.UTF8.GetString(buffer, 0, bytesRead).Split(';'); if (messageParts.Length < 3) { Log("Geçersiz mesaj formatı."); client.Close(); return; } string receivedPin = messageParts[0]; string pcId = messageParts[1]; string pcName = messageParts[2]; if (receivedPin == currentPin) { Log($"PIN doğru. İstemci doğrulandı: {pcName}"); PairDevice(pcId, pcName); byte[] response = Encoding.UTF8.GetBytes("PIN_OK"); await stream.WriteAsync(response, 0, response.Length); MainThread.BeginInvokeOnMainThread(async () => { await Navigation.PushAsync(new ControlPanelPage(client, stream, pcName)); }); return; } else { Log($"YANLIŞ PIN: '{receivedPin}'. Bağlantı reddedildi."); byte[] response = Encoding.UTF8.GetBytes("PIN_FAIL"); await stream.WriteAsync(response, 0, response.Length); } } catch (Exception ex) { Log($"İstemci ({clientEndPoint}) ile iletişimde hata: {ex.Message}"); } stream?.Close(); client?.Close(); }
        private void LoadPhoneId() { _phoneId = Preferences.Get("PhoneId", string.Empty); if (string.IsNullOrEmpty(_phoneId)) { _phoneId = Guid.NewGuid().ToString(); Preferences.Set("PhoneId", _phoneId); } }
        private void PairDevice(string id, string name) { if (!PairedDevices.Any(d => d.PcId == id)) { var newDevice = new PairedDevice { PcId = id, Name = name }; PairedDevices.Add(newDevice); SavePairedDevices(); Log($"Yeni cihaz eşleştirildi: {name}"); } }
        private async Task BroadcastPresence(CancellationToken token) { using (var udpClient = new UdpClient()) { udpClient.EnableBroadcast = true; var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, UdpPort); while (!token.IsCancellationRequested) { try { string ipAddress = GetLocalIpAddress(); string deviceName = DeviceInfo.Name?.Replace(";", "") ?? "Bilinmeyen Cihaz"; if (!string.IsNullOrEmpty(ipAddress) && !string.IsNullOrEmpty(currentPin)) { string message = $"DEVICE_ANNOUNCE;{deviceName};{ipAddress};{currentPin};{_phoneId}"; byte[] data = Encoding.UTF8.GetBytes(message); await udpClient.SendAsync(data, data.Length, broadcastEndpoint); } await Task.Delay(3000, token); } catch (TaskCanceledException) { break; } catch (Exception ex) { Log($"Yayın sırasında hata: {ex.Message}"); } } } }
        private void LoadPairedDevices() { string devicesJson = Preferences.Get("PairedDevices", "[]"); var devices = JsonSerializer.Deserialize<List<PairedDevice>>(devicesJson); PairedDevices.Clear(); foreach (var device in devices) { PairedDevices.Add(device); } }
        private void SavePairedDevices() { string devicesJson = JsonSerializer.Serialize(PairedDevices); Preferences.Set("PairedDevices", devicesJson); }
        private async Task ListenForClients() { Log("İstemci bağlantısı bekleniyor..."); try { while (true) { TcpClient client = await tcpListener.AcceptTcpClientAsync(); Log($"İstemci bağlandı: {((IPEndPoint)client.Client.RemoteEndPoint).Address}."); _ = Task.Run(() => HandleClient(client)); } } catch (Exception ex) { Log($"Dinleme durduruldu veya hata oluştu: {ex.Message}"); } }
        private string GetLocalIpAddress() { try { return NetworkInterface.GetAllNetworkInterfaces().SelectMany(ni => ni.GetIPProperties().UnicastAddresses).FirstOrDefault(ip => ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address))?.Address.ToString(); } catch { return null; } }
        private void DisplayIpAddress() { string ipAddress = GetLocalIpAddress(); if (!string.IsNullOrEmpty(ipAddress)) { IpAddressLabel.Text = $"IP Adresi: {ipAddress}"; } else { IpAddressLabel.Text = "IP Adresi bulunamadı."; } }
        private void Log(string message) { MainThread.BeginInvokeOnMainThread(() => { LogEditor.Text += $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}"; }); }
    }
}
