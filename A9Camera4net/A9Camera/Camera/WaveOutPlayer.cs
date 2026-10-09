using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace A9Camera.Camera
{
    /// <summary>
    /// 基于 WinMM waveOut 的流式 PCM 播放器（等价于 node-speaker 的角色）。
    /// 默认参数与 speaker.js 一致：单声道、16-bit、8000Hz。
    /// 无第三方依赖：4 个缓冲轮转写入，队列空时补静音防止断流。
    /// </summary>
    public class WaveOutPlayer : IDisposable
    {
        private const int WAVE_MAPPER = -1;
        private const int CALLBACK_FUNCTION = 0x30000;
        private const uint WOM_DONE = 0x3BD;
        private const uint WHDR_DONE = 0x1;
        private const int BufferCount = 4;

        private readonly int _sampleRate;
        private readonly int _bufferBytes;
        private readonly ConcurrentQueue<byte> _queue = new ConcurrentQueue<byte>();
        private readonly AutoResetEvent _dataAvailable = new AutoResetEvent(false);

        private IntPtr _hWaveOut;
        private readonly IntPtr[] _headers = new IntPtr[BufferCount];
        private readonly GCHandle[] _pins = new GCHandle[BufferCount];
        private readonly byte[][] _buffers = new byte[BufferCount][];
        private WaveOutProc _callback; // 防 GC 回收
        private Thread _feedThread;
        private volatile bool _running;

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        private delegate void WaveOutProc(IntPtr hwo, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

        [DllImport("winmm.dll")]
        private static extern int waveOutOpen(out IntPtr phwo, int uDeviceID, ref WAVEFORMATEX pwfx, WaveOutProc dwCallback, IntPtr dwInstance, int fdwOpen);

        [DllImport("winmm.dll")]
        private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveOutReset(IntPtr hwo);

        [DllImport("winmm.dll")]
        private static extern int waveOutClose(IntPtr hwo);

        /// <param name="sampleRate">采样率（摄像头音频为 8000Hz）</param>
        /// <param name="channels">声道数（1 = 单声道）</param>
        /// <param name="bitsPerSample">位深（16）</param>
        /// <param name="bufferMs">每块缓冲时长（毫秒）</param>
        public WaveOutPlayer(int sampleRate, int channels, int bitsPerSample, int bufferMs)
        {
            _sampleRate = sampleRate;
            int blockAlign = channels * bitsPerSample / 8;
            _bufferBytes = sampleRate * blockAlign * bufferMs / 1000;

            var fmt = new WAVEFORMATEX();
            fmt.wFormatTag = 1; // PCM
            fmt.nChannels = (ushort)channels;
            fmt.nSamplesPerSec = (uint)sampleRate;
            fmt.wBitsPerSample = (ushort)bitsPerSample;
            fmt.nBlockAlign = (ushort)blockAlign;
            fmt.nAvgBytesPerSec = (uint)(sampleRate * blockAlign);
            fmt.cbSize = 0;

            _callback = OnWaveOutCallback;
            int hr = waveOutOpen(out _hWaveOut, WAVE_MAPPER, ref fmt, _callback, IntPtr.Zero, CALLBACK_FUNCTION);
            if (hr != 0)
                throw new InvalidOperationException("waveOutOpen 失败，错误码 " + hr + "（无可用音频设备？）");

            for (int i = 0; i < BufferCount; i++)
            {
                _buffers[i] = new byte[_bufferBytes];
                _pins[i] = GCHandle.Alloc(_buffers[i], GCHandleType.Pinned);
                var hdr = new WAVEHDR();
                hdr.lpData = _pins[i].AddrOfPinnedObject();
                hdr.dwBufferLength = 0; // 首次写入前不播放
                _headers[i] = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WAVEHDR)));
                Marshal.StructureToPtr(hdr, _headers[i], false);
                waveOutPrepareHeader(_hWaveOut, _headers[i], Marshal.SizeOf(typeof(WAVEHDR)));
            }

            _running = true;
            _feedThread = new Thread(FeedLoop);
            _feedThread.IsBackground = true;
            _feedThread.Start();
        }

        /// <summary>推入 PCM 数据（16-bit 小端）</summary>
        public void Write(byte[] pcm)
        {
            if (pcm == null || !_running) return;
            for (int i = 0; i < pcm.Length; i++)
            {
                _queue.Enqueue(pcm[i]);
            }
            _dataAvailable.Set();
        }

        /// <summary>清空未播放数据</summary>
        public void Clear()
        {
            byte b;
            while (_queue.TryDequeue(out b)) { }
        }

        private void OnWaveOutCallback(IntPtr hwo, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
        {
            if (uMsg == WOM_DONE)
            {
                _dataAvailable.Set(); // 唤醒填充线程
            }
        }

        private void FeedLoop()
        {
            int next = 0;
            while (_running)
            {
                _dataAvailable.WaitOne(200); // 空闲时定期唤醒检查

                var hdr = (WAVEHDR)Marshal.PtrToStructure(_headers[next], typeof(WAVEHDR));
                if ((hdr.dwFlags & WHDR_DONE) == 0) continue; // 该缓冲还在播放

                // 取一块数据；不足处补静音，保持播放时钟连续
                int filled = 0;
                byte b;
                while (filled < _bufferBytes && _queue.TryDequeue(out b))
                {
                    _buffers[next][filled++] = b;
                }
                for (int i = filled; i < _bufferBytes; i++)
                {
                    _buffers[next][i] = 0;
                }

                hdr.lpData = _pins[next].AddrOfPinnedObject();
                hdr.dwBufferLength = (uint)_bufferBytes;
                hdr.dwFlags = 0;
                Marshal.StructureToPtr(hdr, _headers[next], false);
                waveOutWrite(_hWaveOut, _headers[next], Marshal.SizeOf(typeof(WAVEHDR)));

                next = (next + 1) % BufferCount;
            }
        }

        public void Dispose()
        {
            _running = false;
            if (_feedThread != null)
            {
                try { _feedThread.Join(500); } catch { }
                _feedThread = null;
            }

            if (_hWaveOut != IntPtr.Zero)
            {
                waveOutReset(_hWaveOut);
                for (int i = 0; i < BufferCount; i++)
                {
                    if (_pins[i].IsAllocated) _pins[i].Free();
                    if (_headers[i] != IntPtr.Zero) Marshal.FreeHGlobal(_headers[i]);
                }
                waveOutClose(_hWaveOut);
                _hWaveOut = IntPtr.Zero;
            }
        }
    }
}
