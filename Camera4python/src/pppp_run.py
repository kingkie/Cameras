#!/usr/bin/env python3
# 移植自 A9_PPPP-master/run_with_audio.js
# 用法: python -u src/pppp_run.py -b 192.168.1.255 -p 3000
# 打开 http://localhost:3000 查看视频（MJPEG），加 --save-wav out.wav 保存音频。

import argparse
import queue
import threading
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from log import log
from pppp import PPPP

MJPEG_BOUNDARY = b'xxxxxxkkdkdkdkdkdk__BOUNDARY'
HTML_PAGE = ('<!DOCTYPE html>\r\n<http><head></head><body>'
             '<img src="/v.mjpg"></body></html>')


class MjpegBroadcaster:
    """把视频帧分发给所有 /v.mjpg 订阅者（每客户端队列，慢客户端丢旧帧）"""

    def __init__(self):
        self._subs = set()
        self._lock = threading.Lock()

    def subscribe(self):
        q = queue.Queue(maxsize=2)
        with self._lock:
            self._subs.add(q)
        return q

    def unsubscribe(self, q):
        with self._lock:
            self._subs.discard(q)

    def publish(self, frame: bytes):
        with self._lock:
            subs = list(self._subs)
        for q in subs:
            try:
                q.put_nowait(frame)
            except queue.Full:
                try:
                    q.get_nowait()
                    q.put_nowait(frame)
                except (queue.Empty, queue.Full):
                    pass


class Handler(BaseHTTPRequestHandler):
    broadcaster: MjpegBroadcaster = None

    def do_GET(self):
        if self.path == '/v.mjpg':
            self.send_response(200)
            self.send_header('Content-Type',
                             'multipart/x-mixed-replace; boundary="%s"'
                             % MJPEG_BOUNDARY.decode())
            self.end_headers()
            q = self.broadcaster.subscribe()
            try:
                while True:
                    frame = q.get()
                    buf = (b'--' + MJPEG_BOUNDARY + b'\r\n'
                           b'Content-Type: image/jpeg\r\n\r\n')
                    self.wfile.write(buf)
                    self.wfile.write(frame)
                    self.wfile.write(b'\r\n')
            except (ConnectionAbortedError, ConnectionResetError, BrokenPipeError):
                pass
            finally:
                self.broadcaster.unsubscribe(q)
        elif self.path == '/':
            self.send_response(200)
            self.send_header('Content-Type', 'text/html; charset=utf-8')
            self.end_headers()
            self.wfile.write(HTML_PAGE.encode())
        else:
            self.send_response(404)
            self.end_headers()

    def log_message(self, fmt, *args):
        pass  # 屏蔽 http.server 默认日志


def main():
    ap = argparse.ArgumentParser(
        description='A9/365Cam PPPP 客户端（MJPEG HTTP 输出）')
    ap.add_argument('-p', '--port', type=int, default=3000,
                    help='HTTP 端口，默认 3000')
    ap.add_argument('-t', '--thisip', default=None,
                    help='绑定的本机网卡 IP')
    ap.add_argument('-b', '--broadcastip', default='255.255.255.255',
                    help='发现广播地址，默认 255.255.255.255')
    ap.add_argument('--save-wav', default=None,
                    help='把解码后的音频保存为 WAV 文件')
    args = ap.parse_args()

    logger = log('main')
    broadcaster = MjpegBroadcaster()
    Handler.broadcaster = broadcaster

    wav_file = None
    if args.save_wav:
        wav_file = wave.open(args.save_wav, 'wb')
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)
        wav_file.setframerate(8000)

    p = PPPP(broadcastip=args.broadcastip, thisip=args.thisip)

    def on_connected(addr):
        logger.info('connected! %s:%d', addr[0], addr[1])
        p.send_cmd_check_user()
        threading.Timer(0.3, p.send_cmd_request_video2).start()
        threading.Timer(0.6, p.send_cmd_request_audio).start()
        p.send_cmd_get_params()

    def on_video_frame(frame):
        broadcaster.publish(frame)

    def on_audio_frame(frame, packet_index):
        if wav_file is not None:
            wav_file.writeframes(frame)

    def on_cmd(text):
        logger.info('CMD: %s', text)

    p.on_connected = on_connected
    p.on_video_frame = on_video_frame
    p.on_audio_frame = on_audio_frame
    p.on_cmd = on_cmd

    server = ThreadingHTTPServer(('0.0.0.0', args.port), Handler)
    server.daemon_threads = True
    logger.info('http server on http://localhost:%d', args.port)

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        logger.info('exiting.')
    finally:
        server.shutdown()
        p.destroy()
        if wav_file is not None:
            wav_file.close()
            logger.info('audio saved to %s', args.save_wav)


if __name__ == '__main__':
    main()
