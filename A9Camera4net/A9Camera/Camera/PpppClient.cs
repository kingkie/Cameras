using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace A9Camera.Camera
{
    /// <summary>
    /// PPPP 协议客户端（移植自 pppp.js）。
    /// 工作流程：
    ///   1. 向 broadcast:32108 每隔 100ms 发送 4 字节发现包 2C BA 5F 5D；
    ///   2. 摄像头回 MSG_PUNCH(0x41)，原样回传最多 5 次；
    ///   3. 摄像头回 MSG_P2P_RDY(0x42) 后认为连接建立（延迟 500ms 触发 Connected）；
    ///   4. 通道 0 = JSON 命令（DRW 载荷 06 0A A0 80 + 长度 + JSON），
    ///      通道 1 = MJPEG 视频分片（帧头 55 AA 15 A8 03），
    ///      通道 2 = ADPCM 音频（帧头 55 AA 15 A8 AA 01）。
    /// </summary>
    public class PpppClient : IDisposable
    {
        // ---- 协议常量 ----
        private const byte MCAM = 0xF1;
        private const byte MDRW = 0xD1;
        private const byte MSG_PUNCH = 0x41;
        private const byte MSG_P2P_RDY = 0x42;
        private const byte MSG_DRW = 0xD0;
        private const byte MSG_DRW_ACK = 0xD1;
        private const byte MSG_ALIVE = 0xE0;
        private const byte MSG_ALIVE_ACK = 0xE1;
        private const byte MSG_CLOSE = 0xF0;

        private const int BroadcastPort = 32108;
        private static readonly byte[] BroadcastPayload = { 0x2C, 0xBA, 0x5F, 0x5D };

        // 新固件 (HQLS_HQT6_20240305) 帧边界魔数：视频 55AA15A8 03xx，音频 55AA15A8 AA01
        private static readonly byte[] VideoBoundaryMagic = { 0x55, 0xAA, 0x15, 0xA8, 0x03 };
        private static readonly byte[] AudioBoundaryMagic = { 0x55, 0xAA, 0x15, 0xA8, 0xAA, 0x01 };
        private static readonly byte[] CmdMagic = { 0x06, 0x0A };

        private const int FrameHeaderSize = 0x20;
        private const int MaxPunchEcho = 5;

        // ---- 状态 ----
        private readonly IPAddress _localIp;
        private readonly IPEndPoint _broadcastEp;

        private UdpClient _udp;
        private Timer _broadcastTimer;
        private volatile bool _running;
        private int _punchCount;
        private int _connectedAnnounced; // 0 = 未宣布, 1 = 已宣布
        private IPEndPoint _camEp;

        private int _drwPacketIndex;
        private int _lastVideoFrame = -1;
        private bool _videoOverflow;
        private readonly HashSet<int> _videoBoundaries = new HashSet<int>();
        private readonly Dictionary<int, byte[]> _videoReceived = new Dictionary<int, byte[]>();
        private int _lastAudioFrame = -1;

        private readonly object _sendLock = new object();
        private readonly object _videoLock = new object();

        // ---- 事件（在后台线程触发，UI 层自行调度）----
        public event Action<string> Log;
        public event Action<IPEndPoint> Connected;
        public event Action<string> CmdResponse;
        public event Action<byte[]> VideoFrame;
        public event Action<byte[]> AudioFrame;

        public bool IsConnected
        {
            get { return Thread.VolatileRead(ref _connectedAnnounced) == 1; }
        }

        public PpppClient(IPAddress localIp, IPAddress broadcastIp)
        {
            _localIp = localIp;
            _broadcastEp = new IPEndPoint(broadcastIp, BroadcastPort);
        }

        #region 生命周期

        /// <summary>绑定本地接口并开始广播发现摄像头</summary>
        public void Start()
        {
            _udp = new UdpClient();
            _udp.EnableBroadcast = true;
            if (_localIp != null)
            {
                _udp.Client.Bind(new IPEndPoint(_localIp, 0));
            }

            _running = true;
            _udp.BeginReceive(OnReceive, null);

            // 等价于 JS 版 100ms 一次的发现广播（punch 收到后自动停发）
            _broadcastTimer = new Timer(_ => SendBroadcastTick(), null, 0, 100);
            LogEvent("开始搜索摄像头 (广播 " + _broadcastEp + ") ...");
        }

        public void Dispose()
        {
            _running = false;
            if (_broadcastTimer != null)
            {
                _broadcastTimer.Dispose();
                _broadcastTimer = null;
            }

            if (_udp != null)
            {
                // 发送 3 次 MSG_CLOSE（与 JS destroy 一致）
                if (_camEp != null)
                {
                    var close = new byte[] { MCAM, MSG_CLOSE, 0, 0 };
                    for (int i = 0; i < 3; i++)
                    {
                        TrySend(Crypt.Encrypt(close), _camEp);
                    }
                }

                try { _udp.Close(); }
                catch { /* 忽略关闭异常 */ }
                _udp = null;
            }
        }

        #endregion

        #region 接收与分发

        private void OnReceive(IAsyncResult ar)
        {
            IPEndPoint remote = null;
            byte[] raw;
            try
            {
                if (_udp == null) return;
                remote = new IPEndPoint(IPAddress.Any, 0);
                raw = _udp.EndReceive(ar, ref remote);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // 关闭瞬间可能抛异常，直接结束本轮
                return;
            }

            if (_running && _udp != null)
            {
                try { _udp.BeginReceive(OnReceive, null); }
                catch { return; }
            }

            try
            {
                HandleDatagram(raw, remote);
            }
            catch (Exception ex)
            {
                LogEvent("处理数据包异常: " + ex.Message);
            }
        }

        private void HandleDatagram(byte[] raw, IPEndPoint remote)
        {
            if (raw == null || raw.Length < 8) return;

            byte[] d = Crypt.Decrypt(raw);
            byte type = d[1];

            switch (type)
            {
                case MSG_PUNCH:
                    // 原样回传以建立打洞，最多 5 次
                    if (_punchCount < MaxPunchEcho)
                    {
                        _punchCount++;
                        TrySend(raw, remote);
                    }
                    break;

                case MSG_P2P_RDY:
                    _camEp = remote;
                    if (Interlocked.CompareExchange(ref _connectedAnnounced, 1, 0) == 0)
                    {
                        LogEvent("P2P 就绪 " + remote);
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            Thread.Sleep(500); // JS 版延迟 500ms 再宣布
                            var handler = Connected;
                            if (handler != null) handler(_camEp);
                        });
                    }
                    break;

                case MSG_ALIVE:
                    // 保活应答
                    var aliveAck = new byte[] { MCAM, MSG_ALIVE_ACK, 0x00, 0x00 };
                    TrySend(Crypt.Encrypt(aliveAck), _camEp);
                    break;

                case MSG_DRW:
                    HandleDrw(d, remote);
                    break;

                case MSG_DRW_ACK:
                case MSG_CLOSE:
                default:
                    break; // 其余类型不处理
            }
        }

        private void HandleDrw(byte[] d, IPEndPoint remote)
        {
            byte channel = d[5];
            int index = (d[6] << 8) | d[7];
            byte[] data = new byte[d.Length - 8];
            Buffer.BlockCopy(d, 8, data, 0, data.Length);

            // 应答 DRW_ACK（JS 版连发两次）
            var ack = new byte[10];
            ack[0] = MCAM;
            ack[1] = MSG_DRW_ACK;
            ack[2] = 0x00; ack[3] = 0x06; // size = 6
            ack[4] = MSG_DRW_ACK;
            ack[5] = channel;
            ack[6] = 0x00; ack[7] = 0x01;
            ack[8] = (byte)(index >> 8);
            ack[9] = (byte)(index & 0xFF);
            byte[] encAck = Crypt.Encrypt(ack);
            TrySend(encAck, remote);
            TrySend(encAck, remote);

            switch (channel)
            {
                case 0:
                    HandleCmdData(data);
                    break;
                case 1:
                    HandleVideoData(index, data);
                    break;
                case 2:
                    HandleAudioData(index, data);
                    break;
            }
        }

        private void HandleCmdData(byte[] data)
        {
            if (data.Length < 10) return;
            if (data[0] != CmdMagic[0] || data[1] != CmdMagic[1]) return;

            // 跳过 06 0A A0 80 + 4 字节长度（共 8 字节），其余为 JSON
            string json = Encoding.ASCII.GetString(data, 8, data.Length - 8);
            var handler = CmdResponse;
            if (handler != null) handler(json);
        }

        #endregion

        #region 视频通道

        private void HandleVideoData(int index, byte[] data)
        {
            lock (_videoLock)
            {
                // 分片序号回绕处理（与 JS 一致：> 65400 进入溢出期）
                if (index > 65400) _videoOverflow = true;
                if (_videoOverflow && index < 65400)
                {
                    _lastVideoFrame = -1;
                    _videoOverflow = false;
                    _videoBoundaries.Clear();
                    _videoReceived.Clear();
                }

                if (StartsWith(data, VideoBoundaryMagic))
                {
                    // 帧起始包前 0x20 字节为自定义帧头，跳过
                    _videoReceived[index] = SubArray(data, FrameHeaderSize);
                    _videoBoundaries.Add(index);
                }
                else
                {
                    _videoReceived[index] = data;
                }

                TryAssembleFrame();
            }
        }

        /// <summary>移植 getVideoFrame()：从倒数第二个边界拼到最近一个边界</summary>
        private void TryAssembleFrame()
        {
            if (_videoBoundaries.Count <= 1) return;

            var sorted = new List<int>(_videoBoundaries);
            sorted.Sort();
            int index = sorted[sorted.Count - 2];
            int lastIndex = sorted[sorted.Count - 1];

            if (index == _lastVideoFrame) return;

            bool complete = true;
            var chunks = new List<byte[]>();
            for (int i = index; i < lastIndex; i++)
            {
                byte[] chunk;
                if (_videoReceived.TryGetValue(i, out chunk) && chunk != null)
                {
                    chunks.Add(chunk);
                }
                else
                {
                    complete = false;
                }
            }

            if (complete)
            {
                _lastVideoFrame = index;
                var frame = Concat(chunks);
                var handler = VideoFrame;
                if (handler != null) handler(frame);
            }

            // 释放已拼帧之前的内存
            foreach (var key in new List<int>(_videoReceived.Keys))
            {
                if (key < index) _videoReceived.Remove(key);
            }
        }

        #endregion

        #region 音频通道

        private void HandleAudioData(int index, byte[] data)
        {
            if (index <= _lastAudioFrame) return; // 丢弃乱序/重复包

            byte[] raw = StartsWith(data, AudioBoundaryMagic)
                ? SubArray(data, FrameHeaderSize)
                : data;

            _lastAudioFrame = index;
            byte[] pcm = AdpcmDecoder.Decode(raw);
            var handler = AudioFrame;
            if (handler != null) handler(pcm);
        }

        #endregion

        #region 发送

        private void SendBroadcastTick()
        {
            if (!_running || _udp == null) return;
            if (IsConnected || _punchCount > 0) return;

            try
            {
                lock (_sendLock)
                {
                    _udp.Send(BroadcastPayload, BroadcastPayload.Length, _broadcastEp);
                }
            }
            catch (SocketException)
            {
                // 广播失败（接口不存在等）静默重试
            }
        }

        private void TrySend(byte[] bytes, IPEndPoint ep)
        {
            if (bytes == null || ep == null || _udp == null || !_running) return;
            try
            {
                lock (_sendLock)
                {
                    _udp.Send(bytes, bytes.Length, ep);
                }
            }
            catch (SocketException)
            {
            }
        }

        /// <summary>发送 DRW 数据包（自动加密、递增分片序号）</summary>
        private void SendDrwPacket(byte channel, byte[] data)
        {
            var buf = new byte[data.Length + 8];
            buf[0] = MCAM;
            buf[1] = MSG_DRW;
            int size = data.Length + 4;
            buf[2] = (byte)(size >> 8);
            buf[3] = (byte)(size & 0xFF);
            buf[4] = MDRW;
            buf[5] = channel;
            int idx = _drwPacketIndex++;
            buf[6] = (byte)(idx >> 8);
            buf[7] = (byte)(idx & 0xFF);
            Buffer.BlockCopy(data, 0, buf, 8, data.Length);

            if (_camEp != null) TrySend(Crypt.Encrypt(buf), _camEp);
        }

        /// <summary>发送 JSON 命令（06 0A A0 80 + LE32 长度 + ASCII）</summary>
        private void SendCmdPacket(string msg)
        {
            byte[] data = Encoding.ASCII.GetBytes(msg);
            var buf = new byte[data.Length + 8];
            buf[0] = 0x06;
            buf[1] = 0x0A;
            buf[2] = 0xA0;
            buf[3] = 0x80;
            int len = data.Length;
            buf[4] = (byte)(len & 0xFF);
            buf[5] = (byte)((len >> 8) & 0xFF);
            buf[6] = (byte)((len >> 16) & 0xFF);
            buf[7] = (byte)((len >> 24) & 0xFF);
            Buffer.BlockCopy(data, 0, buf, 8, data.Length);

            SendDrwPacket(0, buf);
            LogEvent("CMD sent: " + msg);
        }

        #endregion

        #region 公开命令（与 pppp.js 一一对应）

        public void SendCheckUser()
        {
            SendCmdPacket("{\"pro\":\"check_user\",\"cmd\":100,\"devmac\":\"0000\",\"user\":\"admin\",\"pwd\":\"6666\"}");
        }
        /// <summary>
        /// 1080P
        /// </summary>
        public void SendRequestVideo1() // 1080p (640x480)
        {
            SendCmdPacket("{\"pro\":\"stream\",\"cmd\":111,\"video\":1,\"user\":\"admin\",\"pwd\":\"6666\",\"devmac\":\"0000\"}");
        }
        /// <summary>
        /// 720P
        /// </summary>
        public void SendRequestVideo2() // 720p (320x240)
        {
            SendCmdPacket("{\"pro\":\"stream\",\"cmd\":111,\"video\":2,\"user\":\"admin\",\"pwd\":\"6666\",\"devmac\":\"0000\"}");
        }

        public void SendRequestAudio()
        {
            SendCmdPacket("{\"pro\":\"stream\",\"cmd\":111,\"audio\":1,\"user\":\"admin\",\"pwd\":\"6666\"}");
        }

        public void SendGetParams()
        {
            SendCmdPacket("{\"pro\":\"get_parms\",\"cmd\":101,\"user\":\"admin\",\"pwd\":\"6666\",\"devmac\":\"0000\"}");
        }

        public void SendScanWifi()
        {
            SendCmdPacket("{\"pro\":\"scan_wifi\",\"cmd\":113,\"user\":\"admin\",\"pwd\":\"6666\",\"devmac\":\"0000\"}");
        }

        public void SendGetWifi()
        {
            SendCmdPacket("{\"pro\":\"get_wifi\",\"cmd\":112,\"user\":\"admin\",\"pwd\":\"6666\",\"devmac\":\"0000\"}");
        }

        public void SendIr(bool isOn)
        {
            SendCmdPacket("{\"pro\":\"dev_control\",\"cmd\":102,\"user\":\"admin\",\"pwd\":\"6666\",\"icut\":" + (isOn ? 1 : 0) + ",\"devmac\":\"0000\"}");
        }

        public void SendReboot()
        {
            SendCmdPacket("{\"pro\":\"dev_control\",\"cmd\":102,\"user\":\"admin\",\"pwd\":\"6666\",\"reboot\":1,\"devmac\":\"0000\"}");
        }

        public void SendReset()
        {
            SendCmdPacket("{\"pro\":\"dev_control\",\"cmd\":102,\"user\":\"admin\",\"pwd\":\"6666\",\"reset\":1,\"devmac\":\"0000\"}");
        }

        public void SendSetWifi(string ssid, string pwd)
        {
            SendCmdPacket("{\"pro\":\"set_wifi\",\"cmd\":114,\"user\":\"admin\",\"pwd\":\"6666\",\"wifissid\":\"" + ssid +
                          "\",\"wifipwd\":\"" + pwd + "\",\"encwifissid\":\"" + ssid + "\",\"encwifipwd\":\"" + pwd +
                          "\",\"encryption\":1,\"devmac\":\"0000\"}");
        }

        #endregion

        #region 工具方法

        private void LogEvent(string msg)
        {
            var handler = Log;
            if (handler != null) handler(msg);
        }

        private static bool StartsWith(byte[] data, byte[] prefix)
        {
            if (data == null || data.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
            {
                if (data[i] != prefix[i]) return false;
            }
            return true;
        }

        private static byte[] SubArray(byte[] data, int offset)
        {
            if (offset >= data.Length) return new byte[0];
            var result = new byte[data.Length - offset];
            Buffer.BlockCopy(data, offset, result, 0, result.Length);
            return result;
        }

        private static byte[] Concat(List<byte[]> chunks)
        {
            int total = 0;
            foreach (var c in chunks) total += c.Length;
            var result = new byte[total];
            int pos = 0;
            foreach (var c in chunks)
            {
                Buffer.BlockCopy(c, 0, result, pos, c.Length);
                pos += c.Length;
            }
            return result;
        }

        #endregion
    }
}
