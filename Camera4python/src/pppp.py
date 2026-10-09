# 移植自 A9_PPPP-master/pppp.js
# PPPP 协议客户端（A9/365Cam 系列），通过 UDP 广播发现设备，建立会话后
# 用 JSON 指令（check_user / stream 等）控制摄像头，接收 MJPEG 视频与 ADPCM 音频。
#
# 报文头（8 字节，均大端）: magic1(0xf1) type size magic2 channel index
# 视频帧边界前缀: 55aa15a803；音频帧边界前缀: 55aa15a8aa01（均带 0x20 字节头）

import struct
import threading
import socket
from log import log

import pppp_crypt as crypt
import pppp_adpcm as adpcm

MCAM = 0xF1
MDRW = 0xD1

MSG_PUNCH = 0x41
MSG_P2P_RDY = 0x42
MSG_DRW = 0xD0
MSG_DRW_ACK = 0xD1
MSG_ALIVE = 0xE0
MSG_ALIVE_ACK = 0xE1
MSG_CLOSE = 0xF0

TYPE_DICT = {
    MSG_PUNCH: 'MSG_PUNCH',
    MSG_P2P_RDY: 'MSG_P2P_RDY',
    MSG_DRW: 'MSG_DRW',
    MSG_DRW_ACK: 'MSG_DRW_ACK',
    MSG_ALIVE: 'MSG_ALIVE',
    MSG_ALIVE_ACK: 'MSG_ALIVE_ACK',
    MSG_CLOSE: 'MSG_CLOSE',
}

HDR = struct.Struct('>BBHBBH')

VIDEO_BOUNDARY = bytes.fromhex('55aa15a803')
AUDIO_BOUNDARY = bytes.fromhex('55aa15a8aa01')


class PPPP:
    def __init__(self, broadcastip='255.255.255.255', thisip=None):
        self.log = log('PPPP')

        self.broadcast_dest = broadcastip
        self.bind_ip = thisip

        self._sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self._sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 4 * 1024 * 1024)
        if thisip:
            self._sock.bind((thisip, 0))
        else:
            self._sock.bind(('0.0.0.0', 0))

        self._cam_addr = None  # (ip, port)
        self._drw_index = 0
        self._punch_count = 0
        self._is_connected = False
        self._running = True

        # 视频组帧状态
        self._last_video_frame = -1
        self._video_overflow = False
        self._video_boundaries = set()
        self._video_received = {}

        # 音频组帧状态
        self._last_audio_frame = -1

        # 事件回调（对应 JS 的 EventEmitter）
        self.on_connected = None        # fn(addr: tuple)
        self.on_video_frame = None      # fn(frame: bytes)
        self.on_audio_frame = None      # fn(frame: bytes, packet_index: int)
        self.on_cmd = None              # fn(text: str)
        self.on_log = None              # fn(text: str)

        addr, port = self._sock.getsockname()
        self.log.info('socket listening %s:%d', addr, port)

        self._rx_thread = threading.Thread(target=self._rx_loop, daemon=True)
        self._rx_thread.start()

        self._sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        self._send_broadcast()

    # ---------------- 发送 ----------------

    def _send_broadcast(self):
        # 发现广播：原始（未加密）4 字节探测包，端口 32108
        self._sock.sendto(bytes.fromhex('2cba5f5d'), (self.broadcast_dest, 32108))
        self.log.info('broadcast Message sent.')
        if not self._is_connected and self._punch_count == 0:
            t = threading.Timer(0.1, self._send_broadcast)
            t.daemon = True
            t.start()

    def _send(self, msg: bytes):
        if self._cam_addr is None:
            self.log.warn('camera address unknown, drop %d bytes', len(msg))
            return
        self._sock.sendto(msg, self._cam_addr)

    def _send_enc(self, msg):
        if isinstance(msg, str):
            msg = bytes.fromhex(msg)
        self._send(crypt.encrypt(msg))

    def _send_drw_packet(self, channel: int, data: bytes):
        buf = bytearray(len(data) + 8)
        buf[0] = MCAM
        buf[1] = MSG_DRW
        struct.pack_into('>H', buf, 2, len(data) + 4)
        buf[4] = MDRW
        buf[5] = channel
        struct.pack_into('>H', buf, 6, self._drw_index)
        self._drw_index = (self._drw_index + 1) & 0xFFFF
        buf[8:] = data
        self._send_enc(bytes(buf))

    def _send_cmd_packet(self, msg):
        data = msg.encode('ascii') if isinstance(msg, str) else msg
        buf = bytearray(len(data) + 8)
        buf[0] = 0x06
        buf[1] = 0x0A
        buf[2] = 0xA0
        buf[3] = 0x80
        struct.pack_into('<I', buf, 4, len(data))
        buf[8:] = data
        self._send_drw_packet(0, bytes(buf))
        self.log.info('CMD sent: %s', data.decode('ascii', 'replace'))

    # ---------------- JSON 指令 ----------------

    def send_cmd_check_user(self, user='admin', pwd='6666'):
        self._send_cmd_packet(
            '{"pro":"check_user","cmd":100,"devmac":"0000","user":"%s","pwd":"%s"}'
            % (user, pwd))

    def send_cmd_request_video1(self, user='admin', pwd='6666'):
        # "1080p" "HD"，实际 640x480
        self._send_cmd_packet(
            '{"pro":"stream","cmd":111,"video":1,"user":"%s","pwd":"%s","devmac":"0000"}'
            % (user, pwd))

    def send_cmd_request_video2(self, user='admin', pwd='6666'):
        # "720p" "HD"，实际 320x240
        self._send_cmd_packet(
            '{"pro":"stream","cmd":111,"video":2,"user":"%s","pwd":"%s","devmac":"0000"}'
            % (user, pwd))

    def send_cmd_request_audio(self, user='admin', pwd='6666'):
        # ADPCM 格式
        self._send_cmd_packet(
            '{"pro":"stream","cmd":111,"audio":1,"user":"%s","pwd":"%s"}'
            % (user, pwd))

    def send_cmd_scan_wifi(self, user='admin', pwd='6666'):
        self._send_cmd_packet(
            '{"pro":"scan_wifi","cmd": 113,"user":"%s","pwd":"%s","devmac":"0000"}'
            % (user, pwd))

    def send_cmd_get_wifi(self, user='admin', pwd='6666'):
        self._send_cmd_packet(
            '{"pro":"get_wifi","cmd": 112,"user":"%s","pwd":"%s","devmac":"0000"}'
            % (user, pwd))

    def send_cmd_set_wifi(self, ssid, pw, user='admin', pwd='6666'):
        self._send_cmd_packet(
            '{"pro":"set_wifi","cmd":114,"user":"%s","pwd":"%s","wifissid":"%s",'
            '"wifipwd":"%s","encwifissid":"%s","encwifipwd":"%s","encryption":1,"devmac":"0000"}'
            % (user, pwd, ssid, pw, ssid, pw))

    def send_cmd_get_params(self, user='admin', pwd='6666'):
        self._send_cmd_packet(
            '{"pro":"get_parms","cmd":101,"user":"%s","pwd":"%s","devmac":"0000"}'
            % (user, pwd))

    def send_cmd_set_params(self, changes: dict, user='admin', pwd='6666'):
        import json
        data = {'pro': 'dev_control', 'cmd': 102,
                'user': user, 'pwd': pwd, 'devmac': '0000'}
        data.update(changes)
        self._send_cmd_packet(json.dumps(data))

    def send_cmd_ir(self, is_on: bool, user='admin', pwd='6666'):
        self._send_cmd_packet(
            '{"pro":"dev_control","cmd":102,"user":"%s","pwd":"%s","icut":%d,"devmac":"0000"}'
            % (user, pwd, 1 if is_on else 0))

    def send_cmd_reboot(self, user='admin', pwd='6666'):
        self._send_cmd_packet(
            '{"pro":"dev_control","cmd":102,"user":"%s","pwd":"%s","reboot":1,"devmac":"0000"}'
            % (user, pwd))

    def send_cmd_reset(self, user='admin', pwd='6666'):
        self._send_cmd_packet(
            '{"pro":"dev_control","cmd":102,"user":"%s","pwd":"%s","reset":1,"devmac":"0000"}'
            % (user, pwd))

    # ---------------- 接收 ----------------

    def _rx_loop(self):
        try:
            while self._running:
                try:
                    raw, rinfo = self._sock.recvfrom(65536)
                except OSError:
                    break
                self._handle_packet(raw, rinfo)
        except Exception:
            self.log.err('rx loop crashed', exc_info=True)

    def _handle_packet(self, raw: bytes, rinfo):
        d = crypt.decrypt(raw)
        if len(d) < 8:
            return
        magic1, ptype, size, magic2, channel, index = HDR.unpack_from(d, 0)
        data = d[8:]

        name = TYPE_DICT.get(ptype, '0x%02x' % ptype)
        if ptype == MSG_DRW:
            self.log.dbg('Received %s size: %d channel: %d index: %d',
                         name, size, channel, index)
        else:
            self.log.dbg('Received %s size: %d', name, size)

        # 回应 PUNCH 建立连接（原样回显收到的密文包）
        if ptype == MSG_PUNCH:
            if self._punch_count < 5:
                self._punch_count += 1
                self._sock.sendto(raw, rinfo)

        # P2P_RDY：记录摄像头地址，首次延迟 500ms 触发 connected
        if ptype == MSG_P2P_RDY:
            self._cam_addr = rinfo
            if not self._is_connected:
                self._is_connected = True
                t = threading.Timer(0.5, self._fire_connected, args=(rinfo,))
                t.daemon = True
                t.start()

        # 心跳应答
        if ptype == MSG_ALIVE:
            self._send_enc(bytes([MCAM, MSG_ALIVE_ACK, 0, 0]))

        # 数据包
        if ptype == MSG_DRW:
            self._handle_drw(data, channel, index, rinfo)

    def _handle_drw(self, data: bytes, channel: int, index: int, rinfo):
        # 发送 DRW_ACK（原 JS 发两遍）
        buf = bytearray(10)
        buf[0] = MCAM
        buf[1] = MSG_DRW_ACK
        struct.pack_into('>H', buf, 2, 6)
        buf[4] = 0xD1
        buf[5] = channel
        struct.pack_into('>H', buf, 6, 1)
        struct.pack_into('>H', buf, 8, index)
        self._send(crypt.encrypt(bytes(buf)))
        self._send(crypt.encrypt(bytes(buf)))

        # 指令应答（channel 0）
        if channel == 0:
            if data.startswith(b'\x06\x0a'):
                text = data[8:].decode('ascii', 'replace')
                if self.on_cmd:
                    self.on_cmd(text)

        # 视频（channel 1）
        elif channel == 1:
            # 包序号回绕处理
            if index > 65400:
                self._video_overflow = True
            if self._video_overflow and index < 65400:
                self._last_video_frame = -1
                self._video_overflow = False
                self._video_boundaries.clear()
                self._video_received.clear()

            if data.startswith(VIDEO_BOUNDARY):
                self._video_received[index] = data[0x20:]
                self._video_boundaries.add(index)
            else:
                self._video_received[index] = data
            self._get_video_frame()

        # 音频（channel 2）
        elif channel == 2:
            if self._last_audio_frame < index:
                if data.startswith(AUDIO_BOUNDARY):
                    raw = data[0x20:]
                else:
                    raw = data
                decoded = adpcm.decode(raw)
                self._last_audio_frame = index
                if self.on_audio_frame:
                    self.on_audio_frame(decoded, index)

    # ---------------- 视频组帧 ----------------

    def _get_video_frame(self):
        if len(self._video_boundaries) <= 1:
            return
        arr = sorted(self._video_boundaries)
        start = arr[-2]
        last = arr[-1]

        if start == self._last_video_frame:
            return

        out = []
        complete = True
        for i in range(start, last):
            chunk = self._video_received.get(i)
            if chunk is not None:
                out.append(chunk)
            else:
                complete = False

        if complete:
            self._last_video_frame = start
            if self.on_video_frame:
                self.on_video_frame(b''.join(out))
            # 释放旧分片内存
            for i in list(self._video_received.keys()):
                if i < start:
                    del self._video_received[i]

    # ---------------- 生命周期 ----------------

    def _fire_connected(self, rinfo):
        if self.on_connected:
            self.on_connected(rinfo)

    def destroy(self):
        for _ in range(3):
            self._send_enc(bytes([MCAM, MSG_CLOSE, 0, 0]))
        self._running = False
        try:
            self._sock.close()
        except OSError:
            pass


if __name__ == '__main__':
    # 简单自测：只跑发现广播，看是否有设备响应
    p = PPPP()
    p.on_log = print

    def _conn(addr):
        print('CONNECTED:', addr)

    p.on_connected = _conn
    try:
        while True:
            import time
            time.sleep(1)
    except KeyboardInterrupt:
        p.destroy()
