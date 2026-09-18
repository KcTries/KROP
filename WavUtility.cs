using System;
using System.Text;
using UnityEngine;

namespace QOL_Realisim_Fixes
{
    // Minimal hand-rolled PCM WAV parser -- a BepInEx plugin has no access
    // to Unity's Editor-only audio import pipeline, and the usual runtime
    // alternative (UnityWebRequestMultimedia.GetAudioClip) needs a real
    // file on disk plus an extra module reference just to decode one embedded
    // clip. Walks the RIFF chunk list generically (rather than assuming
    // fixed byte offsets) since real-world WAV files often carry extra
    // chunks (LIST/fact/etc.) before the "fmt "/"data" pair. Covers the
    // common cases: 8/16/24/32-bit PCM and 32-bit IEEE float.
    internal static class WavUtility
    {
        internal static AudioClip ToAudioClip(byte[] fileBytes, string clipName)
        {
            if (fileBytes == null || fileBytes.Length < 12
                || fileBytes[0] != 'R' || fileBytes[1] != 'I' || fileBytes[2] != 'F' || fileBytes[3] != 'F'
                || fileBytes[8] != 'W' || fileBytes[9] != 'A' || fileBytes[10] != 'V' || fileBytes[11] != 'E')
            {
                return null;
            }

            int audioFormat = 0;
            int channels = 0;
            int sampleRate = 0;
            int bitsPerSample = 0;
            int dataOffset = -1;
            int dataLength = 0;

            int pos = 12;
            while (pos + 8 <= fileBytes.Length)
            {
                string chunkId = Encoding.ASCII.GetString(fileBytes, pos, 4);
                int chunkSize = BitConverter.ToInt32(fileBytes, pos + 4);
                int chunkDataStart = pos + 8;
                if (chunkDataStart + chunkSize > fileBytes.Length)
                {
                    break;
                }

                if (chunkId == "fmt ")
                {
                    audioFormat = BitConverter.ToInt16(fileBytes, chunkDataStart);
                    channels = BitConverter.ToInt16(fileBytes, chunkDataStart + 2);
                    sampleRate = BitConverter.ToInt32(fileBytes, chunkDataStart + 4);
                    bitsPerSample = BitConverter.ToInt16(fileBytes, chunkDataStart + 14);
                }
                else if (chunkId == "data")
                {
                    dataOffset = chunkDataStart;
                    dataLength = chunkSize;
                }

                // Chunks are word-aligned -- an odd-sized chunk has a single
                // pad byte after it that isn't counted in chunkSize.
                pos = chunkDataStart + chunkSize + (chunkSize % 2);
            }

            if (dataOffset < 0 || channels <= 0 || sampleRate <= 0 || bitsPerSample <= 0)
            {
                return null;
            }
            dataLength = Mathf.Min(dataLength, fileBytes.Length - dataOffset);

            float[] samples = audioFormat == 3
                ? DecodeFloat(fileBytes, dataOffset, dataLength, bitsPerSample)
                : DecodePcm(fileBytes, dataOffset, dataLength, bitsPerSample);
            if (samples == null || samples.Length == 0)
            {
                return null;
            }

            int sampleCount = samples.Length / channels;
            AudioClip clip = AudioClip.Create(clipName, sampleCount, channels, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private static float[] DecodePcm(byte[] data, int offset, int length, int bitsPerSample)
        {
            switch (bitsPerSample)
            {
                case 8:
                {
                    // 8-bit PCM is stored unsigned (0-255, centered on 128).
                    float[] samples = new float[length];
                    for (int i = 0; i < length; i++)
                    {
                        samples[i] = (data[offset + i] - 128) / 128f;
                    }
                    return samples;
                }
                case 16:
                {
                    int count = length / 2;
                    float[] samples = new float[count];
                    for (int i = 0; i < count; i++)
                    {
                        samples[i] = BitConverter.ToInt16(data, offset + i * 2) / 32768f;
                    }
                    return samples;
                }
                case 24:
                {
                    int count = length / 3;
                    float[] samples = new float[count];
                    for (int i = 0; i < count; i++)
                    {
                        int byteIndex = offset + i * 3;
                        int value = data[byteIndex] | (data[byteIndex + 1] << 8) | (data[byteIndex + 2] << 16);
                        if ((value & 0x800000) != 0)
                        {
                            value |= unchecked((int)0xFF000000);
                        }
                        samples[i] = value / 8388608f;
                    }
                    return samples;
                }
                case 32:
                {
                    int count = length / 4;
                    float[] samples = new float[count];
                    for (int i = 0; i < count; i++)
                    {
                        samples[i] = BitConverter.ToInt32(data, offset + i * 4) / 2147483648f;
                    }
                    return samples;
                }
                default:
                    return null;
            }
        }

        private static float[] DecodeFloat(byte[] data, int offset, int length, int bitsPerSample)
        {
            if (bitsPerSample != 32)
            {
                return null;
            }
            int count = length / 4;
            float[] samples = new float[count];
            for (int i = 0; i < count; i++)
            {
                samples[i] = BitConverter.ToSingle(data, offset + i * 4);
            }
            return samples;
        }
    }
}
