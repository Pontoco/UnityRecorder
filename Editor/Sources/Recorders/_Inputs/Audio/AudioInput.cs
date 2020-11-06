using System;
using System.Collections.Generic;
using UnityEngine;
using System.Reflection;
using FMOD;
using FMODUnity;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine.Assertions;
using Debug = UnityEngine.Debug;

namespace UnityEditor.Recorder.Input
{
    class AudioRenderer : ScriptableSingleton<AudioRenderer>
    {
        readonly MethodInfo s_StartMethod;
        readonly MethodInfo s_StopMethod;
        readonly MethodInfo s_GetSampleCountForCaptureFrameMethod;
        readonly MethodInfo s_RenderMethod;

        [SerializeField]
        int s_StartCount = 0;

        AudioRenderer()
        {
            const string className = "UnityEngine.AudioRenderer";
            const string dllName = "UnityEngine";
            var audioRecorderType = Type.GetType(className + ", " + dllName);
            if (audioRecorderType == null)
            {
                Debug.Log("AudioInput could not find " + className + " type in " + dllName);
                return;
            }
            s_StartMethod = audioRecorderType.GetMethod("Start");
            s_StopMethod = audioRecorderType.GetMethod("Stop");
            s_GetSampleCountForCaptureFrameMethod =
                audioRecorderType.GetMethod("GetSampleCountForCaptureFrame");
            s_RenderMethod = audioRecorderType.GetMethod("Render");
            s_StartCount = 0;
        }

        public static void Start()
        {
            if (instance.s_StartCount == 0)
                instance.s_StartMethod.Invoke(null, null);

            ++instance.s_StartCount;
        }

        public static void Stop()
        {
            --instance.s_StartCount;

            if (instance.s_StartCount <= 0)
                instance.s_StopMethod.Invoke(null, null);
        }

        public static uint GetSampleCountForCaptureFrame()
        {
            var count = (int)instance.s_GetSampleCountForCaptureFrameMethod.Invoke(null, null);
            return (uint)count;
        }

        public static void Render(NativeArray<float> buffer)
        {
            instance.s_RenderMethod.Invoke(null, new object[] { buffer });
        }
    }

    internal abstract class AudioInputBase : RecorderInput
    {
        public abstract ushort channelCount { get; }
        public abstract int sampleRate { get; }
        public abstract NativeArray<float> mainBuffer { get; }
        public abstract AudioInputSettings audioSettings { get; }
    }

    class UnityAudioInput : AudioInputBase
    {
        class BufferManager : IDisposable
        {
            readonly NativeArray<float>[] m_Buffers;

            public BufferManager(ushort bufferCount, uint sampleFrameCount, ushort channelCount)
            {
                m_Buffers = new NativeArray<float>[bufferCount];
                for (int i = 0; i < m_Buffers.Length; ++i)
                    m_Buffers[i] = new NativeArray<float>((int)sampleFrameCount * channelCount, Allocator.Persistent);
            }

            public NativeArray<float> GetBuffer(int index)
            {
                return m_Buffers[index];
            }

            public void Dispose()
            {
                foreach (var a in m_Buffers)
                    a.Dispose();
            }
        }

        ushort m_ChannelCount;

        public override ushort channelCount
        {
            get { return m_ChannelCount; }
        }

        public override int sampleRate
        {
            get { return AudioSettings.outputSampleRate; }
        }

        public override NativeArray<float> mainBuffer
        {
            get { return s_BufferManager.GetBuffer(0); }
        }

        static UnityAudioInput s_Handler;
        static BufferManager s_BufferManager;

        public override AudioInputSettings audioSettings
        {
            get { return (AudioInputSettings)settings; }
        }

        protected internal override void BeginRecording(RecordingSession session)
        {
            m_ChannelCount = new Func<ushort>(() => {
                switch (AudioSettings.speakerMode)
                {
                    case AudioSpeakerMode.Mono:        return 1;
                    case AudioSpeakerMode.Stereo:      return 2;
                    case AudioSpeakerMode.Quad:        return 4;
                    case AudioSpeakerMode.Surround:    return 5;
                    case AudioSpeakerMode.Mode5point1: return 6;
                    case AudioSpeakerMode.Mode7point1: return 7;
                    case AudioSpeakerMode.Prologic:    return 2;
                    default: return 1;
                }
            })();

            if (RecorderOptions.VerboseMode)
                Debug.Log(string.Format("AudioInput.BeginRecording for capture frame rate {0}", Time.captureFramerate));

            if (audioSettings.PreserveAudio)
                AudioRenderer.Start();
        }

        protected internal override void NewFrameReady(RecordingSession session)
        {
            if (!audioSettings.PreserveAudio)
                return;

            if (s_Handler == null)
                s_Handler = this;

            if (s_Handler == this)
            {
                var sampleFrameCount = AudioRenderer.GetSampleCountForCaptureFrame();
                if (RecorderOptions.VerboseMode)
                    Debug.Log(string.Format("AudioInput.NewFrameReady {0} audio sample frames @ {1} ch",
                        sampleFrameCount, m_ChannelCount));

                const ushort bufferCount = 1;

                if (s_BufferManager != null)
                    s_BufferManager.Dispose();

                s_BufferManager = new BufferManager(bufferCount, sampleFrameCount, m_ChannelCount);

                AudioRenderer.Render(mainBuffer);
            }
        }

        protected internal override void FrameDone(RecordingSession session)
        {
        }

        protected internal override void EndRecording(RecordingSession session)
        {
            if (s_BufferManager != null)
            {
                s_BufferManager.Dispose();
                s_BufferManager = null;
            }

            if (s_Handler == null)
                return;

            s_Handler = null;

            if (audioSettings.PreserveAudio)
                AudioRenderer.Stop();
        }
    }

    /// <summary>
    /// (ASG) An Audio Input for FMOD. This reads the raw audio coming out of the FMOD system and forwards it to Unity Recorder.
    /// Implemented as a custom DSP that is added to the end of the FMOD Master Bus. We read audio blocks from the DSP callback.
    /// </summary>
    class FmodAudioInput : AudioInputBase
    {
        private ushort mChannelCount;
        public override ushort channelCount => mChannelCount;

        private int mSampleRate;
        public override int sampleRate => mSampleRate;

        // A list of received audio blocks waiting to be encoded. Stores blocks until we send them to Unity Recorder in NewFrameReady.
        // These arrays are reused every frame, as blocks are always the same size.
        private readonly List<float[]> mixBlockQueue = new List<float[]>();
        private int mixBlockQueueSize;

        private NativeArray<float> mMainBuffer; // Allocated temp every frame, with all the unencoded samples
        public override NativeArray<float> mainBuffer => mMainBuffer;

        public override AudioInputSettings audioSettings => (AudioInputSettings) settings;

        // Keep a reference to the dsp callback so it doesn't get garbage collected.
        private static DSP_READCALLBACK dspCallback;
        private DSP dsp;

        protected internal override void BeginRecording(RecordingSession session)
        {
            var dspName = "RecordSessionVideo(Audio)".ToCharArray();
            Array.Resize(ref dspName, 32);
            dspCallback = DspReadCallback;
            var dspDescription = new DSP_DESCRIPTION
            {
                version = 0x00010000,
                name = dspName,
                numinputbuffers = 1,
                numoutputbuffers = 1,
                read = dspCallback,
                numparameters = 0
            };

            FMOD.System system = RuntimeManager.CoreSystem;
            CheckError(system.getMasterChannelGroup(out ChannelGroup masterGroup));
            CheckError(masterGroup.getDSP(CHANNELCONTROL_DSP_INDEX.TAIL, out DSP masterDspTail));
            CheckError(masterDspTail.getChannelFormat(out CHANNELMASK channelMask, out int numChannels,
                out SPEAKERMODE sourceSpeakerMode));

            if (RecorderOptions.VerboseMode)
            {
                Debug.Log(
                    $"(UnityRecorder) Listening to FMOD Audio. Setting DSP to [{channelMask}] [{numChannels}] [{sourceSpeakerMode}]");
            }

            // Create a new DSP with the format of the existing master group.
            CheckError(system.createDSP(ref dspDescription, out dsp));
            CheckError(dsp.setChannelFormat(channelMask, numChannels, sourceSpeakerMode));
            CheckError(masterGroup.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, dsp));

            // Fill in some basic information for the Unity audio encoder.
            mChannelCount = (ushort) numChannels;
            CheckError(system.getDriver(out int driverId));
            CheckError(system.getDriverInfo(driverId, out Guid _, out int systemRate, out SPEAKERMODE _, out int _));
            mSampleRate = systemRate;

            if (RecorderOptions.VerboseMode)
                Debug.Log($"FmodAudioInput.BeginRecording for capture frame rate {Time.captureFramerate}");

            if (audioSettings.PreserveAudio)
                AudioRenderer.Start();
        }

        protected internal override void NewFrameReady(RecordingSession session)
        {
            try
            {
                int totalReadBlocks;
                int totalFloats = 0;
                lock (mixBlockQueue)
                {
                    totalReadBlocks = mixBlockQueueSize;
                    for (int i = 0; i < totalReadBlocks; i++)
                    {
                        totalFloats += mixBlockQueue[i].Length;
                    }
                }

                // Allocate a giant buffer with all of the samples, since the last frame.
                // This is necessary because the Unity audio encoder expects a single native array.
                mMainBuffer = new NativeArray<float>(totalFloats, Allocator.Temp);

                int index = 0;
                for (int i = 0; i < totalReadBlocks; i++)
                {
                    NativeArray<float>.Copy(mixBlockQueue[i], 0, mMainBuffer, index, mixBlockQueue[i].Length);
                    index += mixBlockQueue[i].Length;
                }

                Assert.AreEqual(0, totalFloats % channelCount);
                sampleFrames += totalFloats / channelCount;
            }
            finally
            {
                // Reset the list of blocks, so it can be reused.
                lock (mixBlockQueue)
                {
                    mixBlockQueueSize = 0;
                }
            }
        }

        /// <inheritdoc />
        protected internal override void EndRecording(RecordingSession session)
        {
            base.EndRecording(session);

            CheckError(RuntimeManager.CoreSystem.getMasterChannelGroup(out ChannelGroup master), shouldThrow: false);
            CheckError(master.removeDSP(dsp), shouldThrow: false);

            // This may throw an error, if EndRecording is called more than once for a single BeginRecording.
            // However, this shouldn't be case, and should be treated as a bug, instead.
            CheckError(dsp.release(), shouldThrow: false);

            lock (mixBlockQueue)
            {
                mixBlockQueue.Clear();
                mixBlockQueueSize = 0;
            }
        }

        private RESULT DspReadCallback(ref DSP_STATE dspState, IntPtr inBuffer, IntPtr outBuffer, uint samples,
                                       int inChannels, ref int outChannels)
        {
            try
            {
                // Debug.Log($"Received buffer of samples: {samples}, channels: {inChannels}");
                Assert.AreEqual(inChannels, outChannels);

                const int sampleSizeBytes = 4; // size of a float
                int blockSizeFloats = (int) (samples * inChannels); // size of a float
                int blockSizeBytes = blockSizeFloats * sampleSizeBytes;

                lock (mixBlockQueue)
                {
                    // Copy the audio into the buffer queue
                    float[] buffer;
                    if (mixBlockQueueSize == mixBlockQueue.Count)
                    {
                        // Add a new buffer if there are no empty buffers left in the list.
                        buffer = new float[blockSizeFloats];
                        mixBlockQueue.Add(buffer);
                        if (mixBlockQueue.Count > 500)
                        {
                            // This warning fires if we have been saving blocks, but NewFrameReady hasn't been called recently to drain the queue.
                            // For some reason it's been a while since the Unity recorder requested the latest samples.
                            // This is probably a bug elsewhere.
                            Debug.LogWarning(
                                $"(UnityRecorder/FmodAudioInput) Mix block queue is way too long [{mixBlockQueue.Count}]!" +
                                $" Is it not flushing properly?");
                        }
                    }
                    else
                    {
                        // Use the next free buffer.
                        buffer = mixBlockQueue[mixBlockQueueSize];

                        // Reallocate the buffer if the block size has changed (could happen if the audio device changes).
                        if (buffer.Length != blockSizeFloats)
                        {
                            buffer = new float[blockSizeFloats];
                            mixBlockQueue[mixBlockQueueSize] = buffer;
                        }
                    }

                    mixBlockQueueSize++;

                    // Copy the audio to the block list.
                    unsafe
                    {
                        fixed (float* bufferPtr = buffer)
                        {
                            Buffer.MemoryCopy(inBuffer.ToPointer(), bufferPtr,
                                blockSizeBytes, blockSizeBytes);
                        }
                    }
                }

                // Pass the input through to the output, so we can still hear it.
                unsafe
                {
                    Buffer.MemoryCopy(inBuffer.ToPointer(), outBuffer.ToPointer(),
                        blockSizeBytes,
                        blockSizeBytes);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("There was an error with DSP Processing.");
                Debug.LogException(e);
                return RESULT.ERR_DSP_DONTPROCESS;
            }

            return RESULT.OK;
        }

        // Checks for fmod errors.
        public static void CheckError(RESULT result, bool shouldThrow = true)
        {
            if (result != RESULT.OK)
            {
                if (shouldThrow)
                {
                    throw new Exception(result.ToString());
                }
                else
                {
                    Debug.LogException(
                        new Exception("Got error from FMOD, but suppressing exception. ERROR:  " + result));
                }
            }
        }
    }
}
