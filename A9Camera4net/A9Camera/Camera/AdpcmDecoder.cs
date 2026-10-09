using System;

namespace A9Camera.Camera
{
    /// <summary>
    /// IMA-ADPCM 解码器（移植自 adpcm.js，参考 jwzhangjie/Adpcm_Pcm）。
    /// 摄像头的音频帧每帧独立解码：每次调用都从 index=0、valpred=stepsizeTable[0] 开始，
    /// 与 JS 版行为保持一致。
    /// 输入：ADPCM 字节流（每字节 2 个 4-bit 采样，高半字节在前）。
    /// 输出：16-bit 小端 PCM，采样数为输入字节数的 2 倍。
    /// </summary>
    public static class AdpcmDecoder
    {
        private static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };

        private static readonly int[] StepSizeTable = new int[]
        {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
            50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230,
            253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963,
            1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327,
            3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442,
            11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794,
            32767
        };

        public static byte[] Decode(byte[] inbuf)
        {
            if (inbuf == null) throw new ArgumentNullException("inbuf");

            int index = 0;                          // 步长索引
            int valpred = StepSizeTable[index];     // 预测值（JS: stateValpred = stepsizeTable[0]）
            int step = StepSizeTable[index];

            var outbuf = new byte[inbuf.Length * 2 * 2];
            int inp = 0;
            int outp = 0; // 已输出的采样数
            int inputbuffer = 0;
            bool bufferstep = false; // false = 先取高 4-bit
            int len = inbuf.Length * 2; // 总采样数

            while (len > 0)
            {
                len--;

                // Step 1 - 取 4-bit 增量
                int delta;
                if (bufferstep)
                {
                    delta = inputbuffer & 0xF;
                }
                else
                {
                    inputbuffer = inbuf[inp++];
                    delta = (inputbuffer >> 4) & 0xF;
                }
                bufferstep = !bufferstep;

                // Step 2 - 更新步长索引
                index += IndexTable[delta];
                if (index < 0) index = 0;
                if (index > 88) index = 88;

                // Step 3 - 分离符号与幅度
                int sign = delta & 8;
                delta = delta & 7;

                // Step 4 - 计算差值并更新预测值
                int vpdiff = step >> 3;
                if ((delta & 4) != 0) vpdiff += step;
                if ((delta & 2) != 0) vpdiff += step >> 1;
                if ((delta & 1) != 0) vpdiff += step >> 2;

                if (sign != 0) valpred -= vpdiff;
                else valpred += vpdiff;

                // Step 5 - 限幅
                if (valpred > 32767) valpred = 32767;
                else if (valpred < -32768) valpred = -32768;

                // Step 6 - 更新步长
                step = StepSizeTable[index];

                // Step 7 - 输出 16-bit 小端
                int v = valpred & 0xFFFF;
                outbuf[2 * outp] = (byte)(v & 0xFF);
                outbuf[2 * outp + 1] = (byte)(v >> 8);
                outp++;
            }

            return outbuf;
        }
    }
}
