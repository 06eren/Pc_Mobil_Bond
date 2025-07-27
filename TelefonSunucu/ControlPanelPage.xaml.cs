using System.Net.Sockets;
using System.Text;
using Microsoft.Maui.Storage;
using System.Threading;
using CommunityToolkit.Maui.Storage;

namespace TelefonSunucu
{
    public partial class ControlPanelPage : TabbedPage
    {
        private TcpClient _client;
        private NetworkStream _stream;
        private CancellationTokenSource _listenerCts;
        private bool _isReceivingScreenshot = false;
        private string _screenshotFilePath = "";
        private long _screenshotFileSize = 0;
        private long _totalScreenshotBytesReceived = 0;
        private FileStream _screenshotFileStream = null;

        public ControlPanelPage(TcpClient client, NetworkStream stream, string pcName)
        {
            InitializeComponent();
            _client = client;
            _stream = stream;

            var connectedLabel = this.FindByName<Label>("ConnectedPcLabel");
            if (connectedLabel != null)
            {
                connectedLabel.Text = $"Baðlý Cihaz: {pcName}";
            }

            // DÜZELTME: Hatalara neden olan ve kaydýrma özelliðini
            // devre dýþý býrakan blok geçici olarak kaldýrýldý.
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            _listenerCts = new CancellationTokenSource();
            Task.Run(() => ListenForPcMessages(_listenerCts.Token));
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _listenerCts?.Cancel();
        }

        private async Task ListenForPcMessages(CancellationToken token)
        {
            byte[] buffer = new byte[8192];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, token);
                    if (bytesRead == 0) break;

                    if (_isReceivingScreenshot)
                    {
                        if (_screenshotFileStream != null)
                        {
                            await _screenshotFileStream.WriteAsync(buffer, 0, bytesRead);
                            _totalScreenshotBytesReceived += bytesRead;

                            if (_totalScreenshotBytesReceived >= _screenshotFileSize)
                            {
                                var tempFilePath = _screenshotFilePath;
                                _screenshotFileStream.Close();
                                _screenshotFileStream = null;
                                _isReceivingScreenshot = false;

                                MainThread.BeginInvokeOnMainThread(async () => {
                                    using var stream = File.OpenRead(tempFilePath);
                                    var result = await FileSaver.Default.SaveAsync("Pictures/PcKontrol", Path.GetFileName(tempFilePath), stream, token);
                                    if (result.IsSuccessful)
                                    {
                                        await DisplayAlert("Baþarýlý", $"Ekran görüntüsü galeriye kaydedildi: {result.FilePath}", "Tamam");
                                    }
                                    else
                                    {
                                        await DisplayAlert("Hata", $"Galeriye kaydedilemedi: {result.Exception?.Message}", "Tamam");
                                    }
                                    File.Delete(tempFilePath);
                                });
                            }
                        }
                    }
                    else
                    {
                        string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        MainThread.BeginInvokeOnMainThread(() => ProcessPcMessage(message));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }

        private async void ProcessPcMessage(string message)
        {
            var parts = message.Split(';');
            if (parts.Length > 0)
            {
                string messageType = parts[0];

                if (messageType == "FILE_START" && parts.Length == 3)
                {
                    string fileName = parts[1];
                    long.TryParse(parts[2], out long fileSize);

                    bool canSave = await Permissions.RequestAsync<Permissions.StorageWrite>() == PermissionStatus.Granted;
                    if (!canSave)
                    {
                        await DisplayAlert("Ýzin Gerekli", "Dosyayý kaydetmek için depolama izni vermeniz gerekiyor.", "Tamam");
                        return;
                    }

                    _screenshotFilePath = Path.Combine(FileSystem.Current.CacheDirectory, fileName);
                    _screenshotFileSize = fileSize;
                    _totalScreenshotBytesReceived = 0;

                    try
                    {
                        _screenshotFileStream = new FileStream(_screenshotFilePath, FileMode.Create, FileAccess.Write);
                        _isReceivingScreenshot = true;
                        await DisplayAlert("Ekran Görüntüsü", $"PC'den '{fileName}' alýnýyor...", "Tamam");
                        await SendRawMessageAsync("FILE_OK_TO_SEND");
                    }
                    catch (Exception ex)
                    {
                        await DisplayAlert("Hata", $"Dosya oluþturulamadý: {ex.Message}", "Tamam");
                        _isReceivingScreenshot = false;
                    }
                }
                else if (messageType == "PERF_UPDATE" && parts.Length == 9)
                {
                    var cpuLabel = this.FindByName<Label>("CpuUsageLabel");
                    var ramLabel = this.FindByName<Label>("RamUsageLabel");
                    var netDownLabel = this.FindByName<Label>("NetworkDownLabel");
                    var netUpLabel = this.FindByName<Label>("NetworkUpLabel");
                    var cpuTempLabel = this.FindByName<Label>("CpuTempLabel");
                    var gpuTempLabel = this.FindByName<Label>("GpuTempLabel");
                    var cpuPowerLabel = this.FindByName<Label>("CpuPowerLabel");
                    var gpuPowerLabel = this.FindByName<Label>("GpuPowerLabel");

                    if (cpuLabel != null) cpuLabel.Text = $"{parts[1]}%";
                    if (ramLabel != null) ramLabel.Text = $"{parts[2]} GB";
                    if (netDownLabel != null) netDownLabel.Text = $"{parts[3]} Mbps";
                    if (netUpLabel != null) netUpLabel.Text = $"{parts[4]} Mbps";
                    if (cpuTempLabel != null) cpuTempLabel.Text = $"{parts[5]}°C";
                    if (gpuTempLabel != null) gpuTempLabel.Text = $"{parts[6]}°C";
                    if (cpuPowerLabel != null) cpuPowerLabel.Text = $"{parts[7]} W";
                    if (gpuPowerLabel != null) gpuPowerLabel.Text = $"{parts[8]} W";
                }
            }
        }

        private async Task SendRawMessageAsync(string message)
        {
            if (_stream != null && _stream.CanWrite)
            {
                try
                {
                    byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                    await _stream.WriteAsync(messageBytes, 0, messageBytes.Length);
                }
                catch (Exception ex)
                {
                    await DisplayAlert("Hata", $"Mesaj gönderilemedi: {ex.Message}", "Tamam");
                }
            }
        }

        private async Task SendCommandAsync(string command)
        {
            await SendRawMessageAsync($"CMD;{command}");
        }

        private async void VolumeSlider_DragCompleted(object sender, EventArgs e)
        {
            if (sender is Slider slider)
            {
                int volumeLevel = (int)slider.Value;
                await SendCommandAsync($"SET_VOLUME:{volumeLevel}");
            }
        }

        private async void SendFile_Clicked(object sender, EventArgs e)
        {
            var statusLabel = this.FindByName<Label>("TransferStatusLabel");
            var progressBar = this.FindByName<ProgressBar>("TransferProgressBar");
            try
            {
                var result = await FilePicker.PickAsync();
                if (result == null) { if (statusLabel != null) statusLabel.Text = "Dosya seçilmedi."; return; }
                var fileName = result.FileName;
                using var fileStream = await result.OpenReadAsync();
                var fileSize = fileStream.Length;
                if (statusLabel != null) statusLabel.Text = $"PC onayý bekleniyor: {fileName}";
                if (progressBar != null) await progressBar.ProgressTo(0, 1, Easing.Linear);
                string header = $"FILE_START;{fileName};{fileSize}";
                byte[] headerBytes = Encoding.UTF8.GetBytes(header);
                await _stream.WriteAsync(headerBytes, 0, headerBytes.Length);
                byte[] responseBuffer = new byte[1024];
                var readTask = _stream.ReadAsync(responseBuffer, 0, responseBuffer.Length);
                if (await Task.WhenAny(readTask, Task.Delay(15000)) != readTask) { throw new Exception("PC'den yanýt alýnamadý (zaman aþýmý)."); }
                int bytesReadResponse = readTask.Result;
                string response = Encoding.UTF8.GetString(responseBuffer, 0, bytesReadResponse);
                if (response != "FILE_OK_TO_SEND") { throw new Exception($"PC transferi reddetti veya iptal etti. Yanýt: {response}"); }
                if (statusLabel != null) statusLabel.Text = $"Gönderiliyor: {fileName}";
                byte[] buffer = new byte[8192];
                long totalBytesSent = 0;
                int bytesRead;
                while ((bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await _stream.WriteAsync(buffer, 0, bytesRead);
                    totalBytesSent += bytesRead;
                    if (progressBar != null)
                    {
                        double progress = (double)totalBytesSent / fileSize;
                        await progressBar.ProgressTo(progress, 100, Easing.Linear);
                    }
                }
                if (statusLabel != null) statusLabel.Text = "Gönderim tamamlandý!";
                await DisplayAlert("Baþarýlý", $"{fileName} baþarýyla gönderildi.", "Tamam");
            }
            catch (Exception ex)
            {
                if (statusLabel != null) statusLabel.Text = "Hata oluþtu.";
                await DisplayAlert("Hata", $"Dosya gönderilirken bir hata oluþtu: {ex.Message}", "Tamam");
            }
        }
        private async void MutePc_Clicked(object sender, EventArgs e) => await SendCommandAsync("MUTE");
        private async void VolumeUp_Clicked(object sender, EventArgs e) => await SendCommandAsync("VOLUME_UP");
        private async void VolumeDown_Clicked(object sender, EventArgs e) => await SendCommandAsync("VOLUME_DOWN");
        private async void Screenshot_Clicked(object sender, EventArgs e) => await SendCommandAsync("SCREENSHOT");
        private async void LockPc_Clicked(object sender, EventArgs e) => await SendCommandAsync("LOCK");
        private async void MediaPrevious_Clicked(object sender, EventArgs e) => await SendCommandAsync("MEDIA_PREVIOUS");
        private async void MediaPlayPause_Clicked(object sender, EventArgs e) => await SendCommandAsync("MEDIA_PLAYPAUSE");
        private async void MediaNext_Clicked(object sender, EventArgs e) => await SendCommandAsync("MEDIA_NEXT");
        private async void TaskManager_Clicked(object sender, EventArgs e) => await SendCommandAsync("TASK_MANAGER");
        private async void Notepad_Clicked(object sender, EventArgs e) => await SendCommandAsync("NOTEPAD");
        private async void Calculator_Clicked(object sender, EventArgs e) => await SendCommandAsync("CALCULATOR");
        private async void TurnOffScreen_Clicked(object sender, EventArgs e) => await SendCommandAsync("SCREEN_OFF");
        private async void SignOut_Clicked(object sender, EventArgs e) => await SendCommandAsync("SIGNOUT");
        private async void SleepPc_Clicked(object sender, EventArgs e) => await SendCommandAsync("SLEEP");
        private async void HibernatePc_Clicked(object sender, EventArgs e) => await SendCommandAsync("HIBERNATE");
        private async void RestartPc_Clicked(object sender, EventArgs e) { bool confirm = await DisplayAlert("Onay", "Bilgisayarý yeniden baþlatmak istediðinizden emin misiniz?", "Evet, Yeniden Baþlat", "Ýptal"); if (confirm) { await SendCommandAsync("RESTART"); } }
        private async void ShutdownPc_Clicked(object sender, EventArgs e) { bool confirm = await DisplayAlert("Onay", "Bilgisayarý kapatmak istediðinizden emin misiniz?", "Evet, Kapat", "Ýptal"); if (confirm) { await SendCommandAsync("SHUTDOWN"); } }
        private async void MousePad_PanUpdated(object sender, PanUpdatedEventArgs e)
        {
            if (e.StatusType == GestureStatus.Running)
            {
                int dx = (int)(e.TotalX * 2.0);
                int dy = (int)(e.TotalY * 2.0);
                await SendCommandAsync($"MOUSE_MOVE:{dx}:{dy}");
            }
        }
        private async void LeftClick_Clicked(object sender, EventArgs e) => await SendCommandAsync("MOUSE_CLICK:LEFT");
        private async void RightClick_Clicked(object sender, EventArgs e) => await SendCommandAsync("MOUSE_CLICK:RIGHT");
        private async void Disconnect_Clicked(object sender, EventArgs e) { _listenerCts?.Cancel(); try { _stream?.Close(); _client?.Close(); } catch { } await Navigation.PopToRootAsync(); }
        protected override bool OnBackButtonPressed() { Disconnect_Clicked(this, EventArgs.Empty); return true; }
    }
}
