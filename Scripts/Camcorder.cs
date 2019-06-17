using UnityEngine;
using UnityEngine.XR;
using UnityEngine.Rendering;
using System;
using System.IO;
using System.Collections.Generic;
using SnapXR.Encoder;
using ThreadPriority = System.Threading.ThreadPriority;

namespace SnapXR
{
    using UnityObject = UnityEngine.Object;

    #region Enums

    public enum CamcorderSate
    {
        Recording,
        Suspending,
        Stopped
    }

    public enum CamcorderFileStyle
    {
        Timestamp,
        Numbered
    }

    public enum CamcorderAspectRatio
    {
        Square,
        FromCamera,
        RawCamera,
        Custom
    }

    #endregion

    [AddComponentMenu("Miscellaneous/SnapXR Camcorder")]
    [RequireComponent(typeof(Camera)), DisallowMultipleComponent]
    public sealed class Camcorder : MonoBehaviour
    {
        #region Editor Statistics
        // Collects data for the custom inspector/editor
#if UNITY_EDITOR
        int maxFrameBufferSize = 0;
        int failedAsyncGPUReadbackRequest = 0;
        string lastfile;
        float saveProgress = 0;
#endif
        #endregion

        #region Editor Fields

        [SerializeField, Min(8)]
        int width = 480;

        [SerializeField, Min(8)]
        int height = 270;

        [SerializeField]
        CamcorderAspectRatio aspectRatio = CamcorderAspectRatio.Square;

        [SerializeField, Range(1, 30)]
        int framesPerSecond = 20;

        [SerializeField, Range(1, 100)]
        int quality = 15;

        [SerializeField, Min(-1)]
        int repeat = 0;

        [SerializeField, Min(0.1f)]
        float frameBufferSize = 5f;

        [SerializeField, Range(0f, 2f)]
        float longpressDelay = 0.5f;

        [SerializeField]
        string currentSaveFolder = "";
        public string SaveFolder
        {
            get
            {
                GeneratePath();
                return currentSaveFolder;
            }
            set
            {
                currentSaveFolder = value;
                GeneratePath();
            }
        }

        [SerializeField]
        string currentFilePrefix = "SnapXR";
        public string FilePrefix
        {
            get
            {
                UpdateNumberedGifCount();
                return currentFilePrefix;
            }
            set
            {
                currentFilePrefix = value;
                UpdateNumberedGifCount();
            }
        }

        [SerializeField]
        CamcorderFileStyle currentFileStyle = CamcorderFileStyle.Timestamp;
        public CamcorderFileStyle FileStyle
        {
            get
            {
                return currentFileStyle;
            }
            set
            {
                currentFileStyle = value;
                UpdateNumberedGifCount();
            }
        }

        public ThreadPriority EncodingPriority = ThreadPriority.BelowNormal;
        public bool AutoStart = true;

        #endregion

        #region Editor Helpers

#if UNITY_EDITOR
        public string Statistics
        {
            get
            {
                string stats = "Initializing";
                if (isInitialzied)
                {
                    stats = "Recorder State : " + State + Environment.NewLine; ;
                    stats += Environment.NewLine;
                    stats += "Frames to keep: " + maxFramesToKeep + Environment.NewLine;
                    stats += "Frames to capture: " + maxFramesToCapture + Environment.NewLine;
                    stats += "Current FrameBuffer size: " + rawFrames.Count + Environment.NewLine;
                    stats += "Max FrameBuffer size: " + maxFrameBufferSize + Environment.NewLine;
                    stats += Environment.NewLine;
                    stats += "Active AsyncGPUReadbackRequest: " + asyncRequests.Count + Environment.NewLine;
                    stats += "Failed AsyncGPUReadbackRequest: " + failedAsyncGPUReadbackRequest + Environment.NewLine;
                    stats += Environment.NewLine;
                    stats += "Grabbed Gif Frames: " + grabbedGifFrames.Count + Environment.NewLine;
                    stats += "Encoding Jobs: " + encodingJobs.Count;
                    if (IsSaving)
                    {
                        stats += Environment.NewLine;
                        stats += "Progress Report: " + saveProgress.ToString("F2") + "%";
                    }
                    if (!string.IsNullOrEmpty(lastfile))
                    {
                        stats += Environment.NewLine;
                        stats += "Last File Saved: " + Environment.NewLine + lastfile;
                    }
                    if (isStereoScopic)
                    {
                        stats += Environment.NewLine;
                        stats += Environment.NewLine;
                        stats += "HMD Model: " + XRDevice.model;
                    }
                }
                return stats;
            }
        }

        public float EstimatedMemoryUse
        {
            get
            {
                float mem = framesPerSecond * frameBufferSize;
                mem *= width * height * 4;
                mem /= 1024 * 1024;
                return mem;
            }
        }
#endif

        #endregion

        #region Public Fields

        public CamcorderSate State { get; private set; } = CamcorderSate.Stopped;
        public bool IsSaving { get; private set; } = false;

        #endregion

        #region Unity Events

        public SnapXRSaveProgressEvent OnFileSaveProgress;
        public SnapXRSavedEvent OnFileSaved;
        public SnapXRStoppedEvent OnStopped;

        #endregion

        #region Internal Fields

        private static uint frameID = 0;

        int maxFramesToKeep;
        int maxFramesToCapture;
        float passedTime;
        float timePerFrame;

        int folderNumberedGifCount = 1;

        Queue<RenderTexture> rawFrames;
        List<GifFrame> grabbedGifFrames;
        int gifFramesToGrab;
        Queue<AsyncGPUReadbackRequest> asyncRequests;
        List<EncoderWorker> encodingJobs;
        Queue<Action> unityEventQueue = new Queue<Action>();

        ReflectionUtils<Camcorder> reflectionUtils;
        Camera attachedCamera;

        bool isStereoScopic;
        Vector2 blitScale;
        Vector2 blitOffset;

        bool isInitialzied = false;

        #endregion

        #region Unity Lifecycle

        void Awake()
        {
            int capacity = Mathf.RoundToInt((frameBufferSize + longpressDelay) * framesPerSecond);
            reflectionUtils = new ReflectionUtils<Camcorder>(this);

            rawFrames = new Queue<RenderTexture>(capacity);
            grabbedGifFrames = new List<GifFrame>(capacity * 2);
            encodingJobs = new List<EncoderWorker>(10);
            asyncRequests = new Queue<AsyncGPUReadbackRequest>(5);
            attachedCamera = GetComponent<Camera>();

            if (OnFileSaveProgress == null)
            {
                OnFileSaveProgress = new SnapXRSaveProgressEvent();
            }
            if (OnFileSaved == null)
            {
                OnFileSaved = new SnapXRSavedEvent();
            }
            if (OnStopped == null)
            {
                OnStopped = new SnapXRStoppedEvent();
            }

            Init();
        }

        void Update()
        {
            // process UnityEvents invoked from the worker threads
            while (unityEventQueue.Count > 0)
            {
                unityEventQueue.Dequeue().Invoke();
            }

            passedTime += Time.unscaledDeltaTime;
            if (passedTime >= timePerFrame)
            {
                if (State != CamcorderSate.Stopped && !attachedCamera.enabled)
                {
                    attachedCamera.Render();
                }

                if (IsSaving && gifFramesToGrab > 0)
                {
                    if (rawFrames.Peek() != null)
                    {
                        asyncRequests.Enqueue(AsyncGPUReadback.Request(rawFrames.Peek()));
                    }
                }
            }

            if (asyncRequests.Count > 0 && asyncRequests.Peek().done)
            {
                var req = asyncRequests.Dequeue();
                if (!req.hasError)
                {
                    GifFrame frame = new GifFrame()
                    {
                        Width = width,
                        Height = height,
                        Data = req.GetData<Color32>().ToArray(),
                        ID = frameID++
                    };
                    // Sanity check the frame
                    if (frame.Width * frame.Height == frame.Data.Length)
                    {
                        lock (grabbedGifFrames)
                        {
                            grabbedGifFrames.Add(frame);
                        }
                        gifFramesToGrab--;
                    }
                    else
                    {
                        Debug.Log("Discarding bad frame.");
                    }
                }
#if UNITY_EDITOR
                else
                {
                    failedAsyncGPUReadbackRequest++;
                }
#endif
            }

            if (encodingJobs.Count > 0 && !IsSaving)
            {
                IsSaving = true;
                encodingJobs[0].Start();
                encodingJobs.RemoveAt(0);
            }
        }

        void LateUpdate()
        {
            if (State == CamcorderSate.Suspending && !IsSaving)
            {
                CleanUp();
                OnStopped.Invoke();
            }
        }

        void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (passedTime >= timePerFrame)
            {
                passedTime -= timePerFrame;

                if (State != CamcorderSate.Stopped)
                {
                    if (attachedCamera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Left || !isStereoScopic)
                    {
                        RenderTexture rt = null;
                        // Clean up superflous frames and recycle the last one for the new frame
                        if (rawFrames.Count >= maxFramesToKeep)
                        {
                            rt = rawFrames.Dequeue();
                        }
                        if (rt == null)
                        {
                            rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
                            rt.wrapMode = TextureWrapMode.Clamp;
                            rt.filterMode = FilterMode.Bilinear;
                            rt.anisoLevel = 0;
                        }
                        Graphics.Blit(source, rt, blitScale, blitOffset);
                        rawFrames.Enqueue(rt);
#if UNITY_EDITOR
                        maxFrameBufferSize = Math.Max(maxFrameBufferSize, rawFrames.Count);
#endif
                    }
                }
            }
            Graphics.Blit(source, destination);
        }

        void OnDestroy()
        {
            Stop();
        }

        #endregion

        #region Setup

        void Init()
        {
            maxFramesToCapture = Mathf.RoundToInt(frameBufferSize * framesPerSecond);
            maxFramesToKeep = maxFramesToCapture + Mathf.RoundToInt(longpressDelay * framesPerSecond);
            timePerFrame = 1f / framesPerSecond;
            passedTime = 0f;

            ComputeHeight();
            GeneratePath();
            blitScale = Vector2.one;
            blitOffset = Vector2.zero;
            if (XRSettings.enabled && XRDevice.isPresent && attachedCamera.stereoTargetEye != StereoTargetEyeMask.None)
            {
                isStereoScopic = true;
                if (aspectRatio != CamcorderAspectRatio.RawCamera)
                {
                    ComputeOcclusionMask();
                    if (width > height)
                    {
                        float aspectAdjust = (float)height / (float)width;
                        blitOffset.y += aspectAdjust * 0.5f * blitScale.y;
                        blitScale.y *= aspectAdjust;
                    }
                    if (height > width)
                    {
                        float aspectAdjust = (float)width / (float)height;
                        blitOffset.x += aspectAdjust * 0.5f * blitScale.x;
                        blitScale.x *= aspectAdjust;
                    }
                }
                // Adjust scale and offset for half texture width in singlepass mode (one doublewide texture for both eyes)
                if (XRSettings.stereoRenderingMode != XRSettings.StereoRenderingMode.MultiPass)
                {
                    blitScale.x *= 0.5f;
                    blitOffset.x *= 0.5f;
                }
            }
            else
            {
                isStereoScopic = false;
            }

            // warm starting
            for (int i = 0; i < maxFramesToKeep; i++)
            {
                rawFrames.Enqueue(null);
            }
            gifFramesToGrab = 0;

            // Make sure the output folder is set or use the default one
            if (AutoStart)
            {
                State = CamcorderSate.Recording;
            }
            isInitialzied = true;
        }

        void CleanUp()
        {
            isInitialzied = false;
            if (rawFrames != null)
            {
                foreach (RenderTexture rt in rawFrames)
                {
                    Flush(rt);
                }
                rawFrames.Clear();
            }
            grabbedGifFrames.Clear();
            asyncRequests.Clear();
            State = CamcorderSate.Stopped;
        }

        #endregion

        #region Public Interface

        /// <summary>
        /// Initializes the component. Use this if you need to change the recorder settings in a script.
        /// This will flush the previously saved frames as settings can't be changed while recording.
        /// Can only be called after the camcorder has been stopped.
        /// </summary>
        /// <param name="width">Width in pixels</param>
        /// <param name="height">Height in pixels</param>
        /// <param name="aspectRatio">Automatically compute height from the current aspect ratio</param>
        /// <param name="fps">Recording FPS</param>
        /// <param name="quality">Quality of color quantization (conversion of images to the maximum
        /// 256 colors allowed by the GIF specification). Lower values (minimum = 1) produce better
        /// colors, but slow processing significantly. Higher values will speed up the quantization
        /// pass at the cost of lower image quality (maximum = 100).</param>
        /// <param name="repeat">-1: no repeat, 0: infinite, >0: repeat count</param>
        /// <param name="bufferSize">Maximum amount of seconds to record to memory</param>
        /// <param name="longpressBufferSize">Additional amount of seconds to record to support longpress activated snaps</param>
        public void Setup(int width, int height, CamcorderAspectRatio aspectRatio, int fps, int quality, int repeat, float bufferSize, float longpressBufferSize)
        {
            switch (State)
            {
                case CamcorderSate.Recording:
                    Debug.Log("Still recording. Call Stop() first.");
                    break;
                case CamcorderSate.Suspending:
                    Debug.Log("Still suspending. Wait until stopped. Subscribe to 'OnStopped' to get notified.");
                    break;
                case CamcorderSate.Stopped:
                    // Set values and validate them
                    reflectionUtils.ConstrainMin(x => x.width, width);
                    if (aspectRatio == CamcorderAspectRatio.Custom)
                    {
                        reflectionUtils.ConstrainMin(x => x.height, height);
                    }
                    this.aspectRatio = aspectRatio;
                    reflectionUtils.ConstrainRange(x => x.framesPerSecond, fps);
                    reflectionUtils.ConstrainRange(x => x.quality, quality);
                    reflectionUtils.ConstrainMin(x => x.repeat, repeat);
                    reflectionUtils.ConstrainMin(x => x.frameBufferSize, bufferSize);
                    reflectionUtils.ConstrainRange(x => x.longpressDelay, longpressBufferSize);

                    Init();
                    break;
            }
        }

        /// <summary>
        /// Starts or resumes recording.
        /// </summary>
        public void Record()
        {
            if (State == CamcorderSate.Stopped && !isInitialzied)
            {
                Init();
            }
            State = CamcorderSate.Recording;
        }

        /// <summary>
        /// Stop recording and clear all the saved frames from memory to start fresh.
        /// Running encoding jobs still finish. Queued encoding jobs are canceled.
        /// Subscribe to the <code>OnStopped</code> event to be notified when the
        /// camcorder is stopped.
        /// </summary>
        public void Stop()
        {
            if (State == CamcorderSate.Recording)
            {
                State = CamcorderSate.Suspending;
                encodingJobs.Clear();
            }
        }

        /// <summary>
        /// Saves the stored frames to a gif file. The filename will automatically be generated.
        /// </summary>
        public void Snap()
        {
            Snap(null);
        }

        /// <summary>
        /// Saves the stored frames to a gif file. If the filename is null or empty, an unique one
        /// will be generated. You don't need to add the .gif extension to the name.
        /// </summary>
        /// <param name="filename">File name without extension</param>
        public void Snap(string filename)
        {
            if (rawFrames.Count == 0)
            {
                Debug.LogWarning("Nothing to save. Maybe you forgot to start the camcorder?");
                return;
            }

            if (State == CamcorderSate.Recording)
            {
                if (string.IsNullOrEmpty(filename))
                {
                    filename = GenerateFileName();
                }
                gifFramesToGrab = maxFramesToCapture;
                encodingJobs.Add(new EncoderWorker(EncodingPriority)
                {
                    gifFrames = grabbedGifFrames,
                    framesToEncode = maxFramesToCapture,
                    startFrame = frameID,
                    filePath = SaveFolder + "\\" + filename + ".gif",
                    encoder = new GifEncoder(width, height, repeat, quality, Mathf.RoundToInt(timePerFrame * 1000f)),
                    OnFileSaveProgress = FileSaveProgress,
                    OnFileSaved = FileSaved
                });
            }
        }

        #endregion

        #region Event Handlers

        void FileSaveProgress(int id, float progress)
        {
            unityEventQueue.Enqueue(() =>
            {
                OnFileSaveProgress?.Invoke(id, progress);
            });
#if UNITY_EDITOR
            saveProgress = progress * 100f;
#endif
        }

        void FileSaved(int id, string filename)
        {
            IsSaving = false;
            if (encodingJobs.Count == 0)
            {
                grabbedGifFrames.Clear();
            }
            unityEventQueue.Enqueue(() =>
            {
                OnFileSaved?.Invoke(id, filename);
            });
#if UNITY_EDITOR
            saveProgress = 0f;
            lastfile = filename;
#endif
        }

        #endregion

        #region Helper Functions

        public void GeneratePath()
        {
            if (string.IsNullOrEmpty(currentSaveFolder))
            {
#if UNITY_EDITOR
                currentSaveFolder = Directory.GetParent(Application.dataPath).FullName + "\\Snaps";
#else
			    currentSaveFolder = Application.persistentDataPath + "\\Snaps";
#endif
            }
            if (!Directory.Exists(currentSaveFolder))
            {
                Directory.CreateDirectory(currentSaveFolder);
            }
            UpdateNumberedGifCount();
        }

        // Gets a filename
        string GenerateFileName()
        {
            string postfix = "";
            switch (FileStyle)
            {
                case CamcorderFileStyle.Timestamp:
                    postfix = " - " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + "'" + DateTime.Now.Millisecond.ToString("D4");
                    break;
                case CamcorderFileStyle.Numbered:
                    postfix = " " + folderNumberedGifCount.ToString("D4");
                    folderNumberedGifCount++;
                    break;
            }

            return FilePrefix + postfix;
        }

        void UpdateNumberedGifCount()
        {
            folderNumberedGifCount = Directory.GetFiles(currentSaveFolder, currentFilePrefix + " ????.gif").Length + 1;
        }

        // crop the camera texture to get rid of the masked out area for HMDs
        void ComputeOcclusionMask()
        {
            // add more HMD models
            switch (XRDevice.model)
            {
                case "Vive MV":
                    blitScale.x *= 0.6875f;
                    blitScale.y *= 0.6179f;
                    blitOffset.x = 0.1375f;
                    blitOffset.y = 0.2097f;
                    break;
                default:
                    blitScale.x *= 0.6875f;
                    blitScale.y *= 0.6179f;
                    blitOffset.x = 0.1375f;
                    blitOffset.y = 0.2097f;
                    break;
            }
        }

        public void ComputeHeight()
        {
            switch (aspectRatio)
            {
                case CamcorderAspectRatio.Square:
                    height = width;
                    break;
                case CamcorderAspectRatio.FromCamera:
                case CamcorderAspectRatio.RawCamera:
#if UNITY_EDITOR
                    height = Mathf.RoundToInt(width / GetComponent<Camera>().aspect);
#else
                    height = Mathf.RoundToInt(width / attachedCamera.aspect);
#endif
                    break;
                case CamcorderAspectRatio.Custom:
                default:
                    // Set by hand
                    break;
            }
        }

        void Flush(UnityObject obj)
        {
#if UNITY_EDITOR
            if (Application.isPlaying)
            {
                Destroy(obj);
            }
            else
            {
                DestroyImmediate(obj);
            }
#else
            UnityObject.Destroy(obj);
#endif
        }

        #endregion
    }
}
