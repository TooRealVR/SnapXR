using UnityEngine;
using System;
using System.Collections.Generic;
using System.Threading;
using SnapXR.Encoder;
using ThreadPriority = System.Threading.ThreadPriority;

namespace SnapXR
{
    internal sealed class EncoderWorker
    {
        static int workerId = 1;

        internal List<GifFrame> gifFrames;
        internal int framesToEncode;
        internal uint startFrame;
        internal string filePath;
        internal GifEncoder encoder;
        internal Action<int, float> OnFileSaveProgress;
        internal Action<int, string> OnFileSaved;

        Thread thread;
        int currentId;
        int frameToGet;
        byte[] pixels;

        internal EncoderWorker(ThreadPriority priority)
        {
            currentId = workerId++;
            thread = new Thread(Run);
            thread.Priority = priority;
            frameToGet = 0;
            pixels = null;
        }

        internal void Start()
        {
            thread.Start();
        }

        void Run()
        {
            encoder.Start(filePath);
            while (framesToEncode > 0)
            {
                if (gifFrames.Count > frameToGet)
                {
                    lock (gifFrames)
                    {
                        if (gifFrames[frameToGet].ID < startFrame)
                        {
                            gifFrames.RemoveAt(0);
                        }
                        else
                        {
                            pixels = gifFrames[frameToGet].ExtractImagePixels();
                        }
                    }
                    if (pixels != null)
                    {
                        encoder.AddFrame(pixels);
                        OnFileSaveProgress?.Invoke(currentId, (float)frameToGet / (float)(framesToEncode + frameToGet));
                        frameToGet++;
                        framesToEncode--;
                    }
                }
                else
                {
                    Thread.Yield();
                }
            }
            encoder.Finish();
            OnFileSaved?.Invoke(currentId, filePath);
        }
    }
}
