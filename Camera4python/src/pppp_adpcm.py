# 移植自 A9_PPPP-master/adpcm.js
# IMA ADPCM 解码（参考 https://github.com/jwzhangjie/Adpcm_Pcm/blob/master/adpcm.c）
# 每帧独立解码：状态每次重置（index=0, valpred=stepsizeTable[0]=7），与原 JS 行为一致。
# 输入每字节含 2 个 4bit 采样，输出 16bit LE PCM，8000Hz 单声道。

_INDEX_TABLE = (-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8)
_STEPSIZE_TABLE = (
    7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
    50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230,
    253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963,
    1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327,
    3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442,
    11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794,
    32767,
)


def decode(indata: bytes) -> bytes:
    valpred = _STEPSIZE_TABLE[0]
    index = 0
    step = _STEPSIZE_TABLE[index]

    outbuf = bytearray(len(indata) * 2 * 2)
    outp = 0
    inp = 0
    inputbuffer = 0
    bufferstep = False

    for _ in range(len(indata) * 2):
        # 取 4bit delta（高 nibble 在前）
        if bufferstep:
            delta = inputbuffer & 0x0F
        else:
            inputbuffer = indata[inp]
            inp += 1
            delta = (inputbuffer >> 4) & 0x0F
        bufferstep = not bufferstep

        # 更新步长索引
        index += _INDEX_TABLE[delta]
        if index < 0:
            index = 0
        elif index > 88:
            index = 88

        # 符号 + 幅值
        sign = delta & 0x08
        delta &= 0x07

        # 计算 vpdiff = (delta+0.5)*step/4
        vpdiff = step >> 3
        if delta & 4:
            vpdiff += step
        if delta & 2:
            vpdiff += step >> 1
        if delta & 1:
            vpdiff += step >> 2

        if sign:
            valpred -= vpdiff
        else:
            valpred += vpdiff

        # 限幅
        if valpred > 32767:
            valpred = 32767
        elif valpred < -32768:
            valpred = -32768

        step = _STEPSIZE_TABLE[index]

        # 写出 16bit LE
        outbuf[2 * outp] = valpred & 0xFF
        outbuf[2 * outp + 1] = (valpred >> 8) & 0xFF
        outp += 1

    return bytes(outbuf)
