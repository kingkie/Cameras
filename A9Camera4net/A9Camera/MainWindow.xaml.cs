using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using A9Camera.Camera;
using System.Web.Script.Serialization;

namespace A9Camera
{
    public partial class MainWindow : Window
    {
        private PpppClient _client;
        private WaveOutPlayer _audioPlayer;
        private long _frameCount;
        private long _audioCount;
        private DateTime _fpsTime = DateTime.Now;
        private int _fps;
        private readonly object _connectLock = new object();

        public MainWindow()
        {
            InitializeComponent();
            AddLog("程序启动。连接前请确保电脑已接入摄像头热点 (DGOA/DGOC)。");
        }

        #region 连接控制

        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            lock (_connectLock)
            {
                if (_client != null) return;

                IPAddress localIp, broadcast;
                if (!IPAddress.TryParse(TxtLocalIp.Text.Trim(), out localIp) ||
                    !IPAddress.TryParse(TxtBroadcast.Text.Trim(), out broadcast))
                {
                    MessageBox.Show("IP 地址格式不正确。", "A9 摄像头",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var client = new PpppClient(localIp, broadcast);
                client.Log += OnClientLog;
                client.Connected += OnClientConnected;
                client.CmdResponse += OnCmdResponse;
                client.VideoFrame += OnVideoFrame;
                client.AudioFrame += OnAudioFrame;
                _client = client;
                client.Start();

                BtnConnect.IsEnabled = false;
                BtnDisconnect.IsEnabled = true;
                SetStatus("搜索摄像头中...", "#B8860B");
            }
        }

        #region 远程控制命令

        private void BtnScanWifi_Click(object sender, RoutedEventArgs e)
        {
            if (_client != null) _client.SendScanWifi();
        }

        private void BtnGetWifi_Click(object sender, RoutedEventArgs e)
        {
            if (_client != null) _client.SendGetWifi();
        }

        private void BtnIrOn_Click(object sender, RoutedEventArgs e)
        {
            if (_client != null) _client.SendIr(true);
        }

        private void BtnIrOff_Click(object sender, RoutedEventArgs e)
        {
            if (_client != null) _client.SendIr(false);
        }

        private void BtnSetWifi_Click(object sender, RoutedEventArgs e)
        {
            if (_client == null) return;
            string ssid = TxtSsid.Text.Trim();
            string pwd = TxtWifiPwd.Text.Trim();
            if (ssid.Length == 0 || pwd.Length == 0)
            {
                MessageBox.Show("请填写 WiFi 名称和密码。", "A9 摄像头",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (MessageBox.Show("确定把摄像头切换到 WiFi \"" + ssid + "\"？切换后摄像头热点会消失。",
                    "设置WiFi", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            {
                _client.SendSetWifi(ssid, pwd);
            }
        }

        private void BtnReboot_Click(object sender, RoutedEventArgs e)
        {
            if (_client == null) return;
            if (MessageBox.Show("确定重启摄像头？", "重启设备",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            {
                _client.SendReboot();
            }
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            if (_client == null) return;
            if (MessageBox.Show("确定恢复出厂设置？所有配置将丢失！", "恢复出厂",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            {
                _client.SendReset();
            }
        }

        /// <summary>连接成功后启用控制面板，断开时禁用</summary>
        private void SetCommandsEnabled(bool enabled)
        {
            Dispatcher.BeginInvoke(new Action(() => { PnlCommands.IsEnabled = enabled; }));
        }

        #endregion

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            Disconnect();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            Disconnect();
        }

        private void Disconnect()
        {
            lock (_connectLock)
            {
                if (_client != null)
                {
                    var client = _client;
                    _client = null;
                    client.Log -= OnClientLog;
                    client.Connected -= OnClientConnected;
                    client.CmdResponse -= OnCmdResponse;
                    client.VideoFrame -= OnVideoFrame;
                    client.AudioFrame -= OnAudioFrame;
                    client.Dispose();
                }

                if (_audioPlayer != null)
                {
                    _audioPlayer.Dispose();
                    _audioPlayer = null;
                }
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                BtnConnect.IsEnabled = true;
                BtnDisconnect.IsEnabled = false;
                TxtNoVideo.Visibility = Visibility.Visible;
                PnlCommands.IsEnabled = false;
                SetStatus("未连接", "#888888");
            }));
            AddLog("已断开。");
        }

        #endregion

        #region 协议事件（后台线程触发）

        /// <summary>连接建立后按新固件流程初始化：登录 → 参数 → 视频 → 音频</summary>
        private void OnClientConnected(IPEndPoint camEp)
        {
            AddLog("已连接摄像头: " + camEp);
            SetStatus("已连接 " + camEp.Address, "#2E8B57");
            SetCommandsEnabled(true);

            var client = _client;
            if (client == null) return;

            client.SendCheckUser();
            client.SendGetParams();

            // 等价 JS 版的 300ms/600ms 延迟请求
            Task.Delay(300).ContinueWith(_ =>
            {
                if (_client == client) client.SendRequestVideo1();
            });
            Task.Delay(600).ContinueWith(_ =>
            {
                if (_client == client)
                {
                    client.SendRequestAudio();
                    StartAudioPlayer();
                }
            });
        }

        private void OnCmdResponse(string json)
        {
            AddLog("CMD << " + json);

            // get_parms 响应：解析关键字段显示
            if (json.Contains("\"batValue\""))
            {
                try
                {
                    var ser = new JavaScriptSerializer();
                    var dict = ser.Deserialize<Dictionary<string, object>>(json);
                    var sb = new StringBuilder();
                    AppendInfo(sb, dict, "sysver", "固件");
                    AppendInfo(sb, dict, "sensor", "传感器");
                    AppendInfo(sb, dict, "batValue", "电量(%)");
                    AppendInfo(sb, dict, "signal", "信号");
                    AppendInfo(sb, dict, "mcuver", "MCU版本");
                    AppendInfo(sb, dict, "tz", "时区");
                    AppendInfo(sb, dict, "lamp", "补光灯");
                    UpdateCamInfo(sb.ToString());
                }
                catch
                {
                    // JSON 解析失败不影响主流程
                }
            }
        }

        private void OnVideoFrame(byte[] jpeg)
        {
            _frameCount++;
            _fps++;

            // FPS 统计，每秒刷新一次
            var now = DateTime.Now;
            long frames = _frameCount;
            if ((now - _fpsTime).TotalMilliseconds >= 1000)
            {
                int fps = _fps;
                _fps = 0;
                _fpsTime = now;
                UpdateStats("帧: " + frames + "  |  " + fps + " fps  |  音频包: " + _audioCount);
            }
            else if (frames % 25 == 0)
            {
                UpdateStats("帧: " + frames + "  |  音频包: " + _audioCount);
            }

            // 定位完整 JPEG (FFD8 ... FFD9)，在后台线程解码并冻结
            var bmp = DecodeJpeg(jpeg);
            if (bmp == null) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                ImgVideo.Source = bmp;
                TxtNoVideo.Visibility = Visibility.Collapsed;
            }));
        }

        private void OnAudioFrame(byte[] pcm)
        {
            _audioCount++;
            var player = _audioPlayer;
            if (player != null)
            {
                try { player.Write(pcm); }
                catch { /* 播放异常不影响取流 */ }
            }
        }

        private void OnClientLog(string msg)
        {
            AddLog(msg);
        }

        #endregion

        #region 辅助

        private void StartAudioPlayer()
        {
            lock (_connectLock)
            {
                if (_audioPlayer != null) return;
                try
                {
                    // 与 speaker.js 一致：单声道 16-bit 8000Hz
                    _audioPlayer = new WaveOutPlayer(8000, 1, 16, 120);
                    AddLog("音频播放器已启动 (8kHz/16bit/单声道)");
                }
                catch (Exception ex)
                {
                    AddLog("音频播放器启动失败: " + ex.Message);
                }
            }
        }

        private static BitmapImage DecodeJpeg(byte[] data)
        {
            try
            {
                int start = Find(data, 0, data.Length - 1, 0xFF, 0xD8);
                if (start < 0) return null;
                int end = FindLast(data, 0xFF, 0xD9);
                if (end < start) return null;

                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(data, start, end + 2 - start, false))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                }
                bmp.Freeze(); // 允许跨线程交给 UI
                return bmp;
            }
            catch
            {
                return null; // 半帧数据解码失败直接跳过
            }
        }

        private static int Find(byte[] data, int from, int to, byte a, byte b)
        {
            for (int i = from; i < to - 1; i++)
            {
                if (data[i] == a && data[i + 1] == b) return i;
            }
            return -1;
        }

        private static int FindLast(byte[] data, byte a, byte b)
        {
            for (int i = data.Length - 2; i >= 0; i--)
            {
                if (data[i] == a && data[i + 1] == b) return i;
            }
            return -1;
        }

        private static void AppendInfo(StringBuilder sb, Dictionary<string, object> dict, string key, string label)
        {
            object value;
            if (dict.TryGetValue(key, out value))
            {
                sb.AppendLine(label + ": " + value);
            }
        }

        private void UpdateCamInfo(string text)
        {
            Dispatcher.BeginInvoke(new Action(() => { TxtCamInfo.Text = text; }));
        }

        private void UpdateStats(string text)
        {
            Dispatcher.BeginInvoke(new Action(() => { TxtStats.Text = text; }));
        }

        private void SetStatus(string text, string colorHex)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                TxtStatus.Text = text;
                var brush = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex));
                TxtStatus.Foreground = brush;
            }));
        }

        private void AddLog(string msg)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LstLog.Items.Add("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg);
                // 限制日志条数，避免长期运行内存增长
                while (LstLog.Items.Count > 300)
                {
                    LstLog.Items.RemoveAt(0);
                }
                if (LstLog.Items.Count > 0)
                {
                    LstLog.ScrollIntoView(LstLog.Items[LstLog.Items.Count - 1]);
                }
            }));
        }

        #endregion

        private void Get720P_Click(object sender, RoutedEventArgs e)
        {
            if (_client != null) _client.SendRequestVideo2();

        }

        private void Get1080P_Click(object sender, RoutedEventArgs e)
        {
            if (_client != null) _client.SendRequestVideo1();

        }
    }
}
