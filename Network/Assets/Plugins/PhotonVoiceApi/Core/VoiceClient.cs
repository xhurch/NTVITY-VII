// -----------------------------------------------------------------------
// <copyright file="Voice.cs" company="Exit Games GmbH">
//   Photon Voice API Framework for Photon - Copyright (C) 2015 Exit Games GmbH
// </copyright>
// <summary>
//   Photon audio streaming support.
// </summary>
// <author>developer@photonengine.com</author>
// ----------------------------------------------------------------------------


using POpusCodec;
using POpusCodec.Enums;
using System;
using System.Collections.Generic;

namespace ExitGames.Client.Photon.Voice
{
    /// <summary>
    /// Single event code for all events to save codes for user.
    /// Change if conflicts with other code.
    /// </summary>
    enum EventCode
    {
        VoiceEvent = 201
    }

    /// <summary>
    /// Interface to feed LocalVoice with audio data.
    /// Implement it in class wrapping platform-specific autio source.
    /// </summary>
    public interface IAudioStream
    {
        /// <summary>
        /// Read data if it's enough to fill entire buffer.
        /// Return false otherwise.
        /// </summary>
        bool GetData(float[] buffer);

        /// <summary>Sampling rate (frequency).</summary>
        int SamplingRate { get; }
    }


    public enum FrameDuration
    {
        Frame2dot5ms = 2500,
        Frame5ms = 5000,
        Frame10ms = 10000,
        Frame20ms = 20000,
        Frame40ms = 40000,
        Frame60ms = 60000
    }

    /// <summary>Describes audio stream properties.</summary>
    public class VoiceInfo
    {
        public VoiceInfo(int samplingRate, int channels, int frameDurationUs, int bitrate, object userdata)
        {
            this.SamplingRate = samplingRate;
            this.Channels = channels;
            this.FrameDurationUs = frameDurationUs;
            this.Bitrate = bitrate;
            this.UserData = userdata;
        }

        /// <summary>Audio sampling rate (frequency).</summary>
        public int SamplingRate { get; private set; }
        /// <summary>Number of channels.</summary>
        public int Channels { get; private set; }
        /// <summary>Uncompressed frame (audio packet) size in microseconds.</summary>
        public int FrameDurationUs { get; private set; }
        /// <summary>Compression quality in terms of bits per second.</summary>
        public int Bitrate { get; private set; }
        /// <summary>Optional user data. Should be serializable by Photon.</summary>
        public object UserData { get; private set; }

        /// <summary>Uncompressed frame (audio packet) size in samples.</summary>
        public int FrameDurationSamples { get { return (int)(this.SamplingRate * (long)this.FrameDurationUs / 1000000); } }
        /// <summary>Uncompressed frame (audio packet) size in samples.</summary>
        public int FrameSize { get { return this.FrameDurationSamples * this.Channels; } }
    }

    /// <summary>Helper to provide remote voices infos via Client.RemoteVoiceInfos iterator.</summary>
    public class RemoteVoiceInfo
    {
        internal RemoteVoiceInfo(int channelId, int playerId, byte voiceId, VoiceInfo info)
        {
            this.ChannelId = channelId;
            this.PlayerId = playerId;
            this.VoiceId = voiceId;
            this.Info = info;
        }
        /// <summary>Remote voice info.</summary>
        public VoiceInfo Info { get; private set; }
        /// <summary>Id of channel used for transmission.</summary>
        public int ChannelId { get; private set; }
        /// <summary>Player Id of voice owner.</summary>
        public int PlayerId { get; private set; }
        /// <summary>Voice id unique in the room.</summary>
        public byte VoiceId { get; private set; }
    }

    enum EventSubcode : byte
    {
        VoiceInfo = 1,
        VoiceRemove = 2,
        Frame = 3,
        DebugEchoRemoveMyVoices = 10
    }

    enum EventParam : byte
    {
        VoiceId = 1,
        SamplingRate = 2,
        Channels = 3,
        FrameDurationUs = 4,
        Bitrate = 5,
        UserData = 10,
        EventNumber = 11
    }

    /// <summary>
    /// Represents outgoing audio stream. Compresses audio data provided via IAudioStream and broadcasts it to all players in the room.
    /// </summary>
    public class LocalVoice : IDisposable
    {
        static public LocalVoice Dummy = new LocalVoice();
        /// <summary>If AudioGroup != 0, voice's data is sent only to clients listening to this group.</summary>
        /// <see cref="LoadBalancingFrontend.ChangeAudioGroups(byte[], byte[])"/>
        public byte AudioGroup { get; set; }

        /// <summary>If true, stream data broadcasted unconditionally.</summary>
        public bool Transmit { set; get; }

        /// <summary>Returns true if stream broadcasts.</summary>
        public bool IsTransmitting
        {
            get { return this.Transmit && (!this.VoiceDetector.On || this.VoiceDetector.Detected); }
        }

        /// <summary>Sent frames counter.</summary>
        public int FramesSent { get; private set; }

        /// <summary>Sent frames bytes counter.</summary>
        public int FramesSentBytes { get; private set; }

        /// <summary>Use to enable or disable voice detector and set its parameters.</summary>
        public VoiceDetector VoiceDetector { get; private set; }

        /// <summary>
        /// Level meter utility.
        /// </summary>
        public LevelMeter LevelMeter { get; private set; }

        /// <summary>If true, voice detector calibration is in progress.</summary>
        public bool VoiceDetectorCalibrating { get { return voiceDetectorCalibrateCount > 0; } }
        private int voiceDetectorCalibrateCount;

        /// <summary>Trigger voice detector calibration process.
        /// While calibrating, keep silence. Voice detector sets threshold basing on measured backgroud noise level.
        /// </summary>
        /// <param name="durationMs">Duration of calibration in milliseconds.</param>
        public void VoiceDetectorCalibrate(int durationMs)
        {
            voiceDetectorCalibrateCount = this.sourceSamplingRateHz * (int)this.opusEncoder.InputChannels * durationMs / 1000;
            LevelMeter.ResetAccumAvgPeakAmp();
        }
        #region nonpublic

        internal VoiceInfo info;
        private OpusEncoder opusEncoder;
        internal byte id;
        internal int channelId;
        internal byte evNumber = 0; // sequence used by receivers to detect loss. will overflow.
        private IVoiceFrontend frontend;
        private IAudioStream audioStream;
        private int sourceSamplingRateHz;
        private float[] frameBuffer = null;
        internal int sourceFrameSize = 0;
        private float[] sourceFrameBuffer = null;

        //        OpusDecoder _debug_decoder;

        internal LocalVoice()
        {
            this.LevelMeter = new LevelMeter(0, 0);
            this.VoiceDetector = new VoiceDetector(0, 0);
        }

        internal LocalVoice(IVoiceFrontend client, byte id, IAudioStream audioStream, VoiceInfo voiceInfo, int channelId)
        {
            this.info = voiceInfo;
            this.channelId = channelId;
            this.opusEncoder = new OpusEncoder((SamplingRate)voiceInfo.SamplingRate, (Channels)voiceInfo.Channels, voiceInfo.Bitrate, OpusApplicationType.Voip, (POpusCodec.Enums.Delay)(voiceInfo.FrameDurationUs * 2 / 1000));
            this.frontend = client;
            this.id = id;
            this.audioStream = audioStream;
            this.sourceSamplingRateHz = audioStream.SamplingRate;
            this.sourceFrameSize = this.info.FrameSize * this.sourceSamplingRateHz / (int)this.opusEncoder.InputSamplingRate;
            this.frameBuffer = new float[this.info.FrameSize];
            if (this.sourceFrameSize == this.info.FrameSize)
            {
                this.sourceFrameBuffer = this.frameBuffer;
            }
            else
            {
                this.sourceSamplingRateHz = audioStream.SamplingRate;
                this.sourceFrameBuffer = new float[this.sourceFrameSize];

                this.frontend.DebugReturn(DebugLevel.WARNING, "[PV] Local voice #" + this.id + " audio source frequency " + this.sourceSamplingRateHz + " and encoder sampling rate " + (int)this.opusEncoder.InputSamplingRate + " do not match. Resampling will occur before encoding.");
            }

            this.LevelMeter = new LevelMeter(this.sourceSamplingRateHz, this.info.Channels); //1/2 sec
            this.VoiceDetector = new VoiceDetector(this.sourceSamplingRateHz, this.info.Channels);
            //            _debug_decoder = new OpusDecoder(this.InputSamplingRate, this.InputChannels);
        }

        internal void service()
        {
            while (processStream()) ;
        }

        private bool readStream()
        {
            if (!this.audioStream.GetData(this.sourceFrameBuffer))
            {
                return false;
            }

            this.LevelMeter.process(this.sourceFrameBuffer);

            // process VAD calibration (could be moved to process method of yet another processor)
            if (this.voiceDetectorCalibrateCount != 0)
            {
                this.voiceDetectorCalibrateCount -= this.sourceFrameBuffer.Length;
                if (this.voiceDetectorCalibrateCount <= 0)
                {
                    this.voiceDetectorCalibrateCount = 0;
                    this.VoiceDetector.Threshold = LevelMeter.AccumAvgPeakAmp * 2;
                }
            }

            if (this.VoiceDetector.On) {
                this.VoiceDetector.process(this.sourceFrameBuffer);
                if (!this.VoiceDetector.Detected)
                {
                    return false;
                }
            }
            if (this.sourceFrameBuffer != this.frameBuffer)
            {
                VoiceUtil.Resample(this.sourceFrameBuffer, this.frameBuffer, (int)this.opusEncoder.InputChannels);
            }
            return true;
        }

        internal Dictionary<byte, int> eventTimestamps = new Dictionary<byte, int>();

        private bool processStream()
        {
            if (this.frontend.IsChannelJoined(this.channelId) && this.Transmit)
            {
                if (readStream())
                {
                    var compressed = this.compress(this.frameBuffer);
                    this.FramesSent++;
                    this.FramesSentBytes += compressed.Count;
                    var evn = evNumber++;
                    object[] content = new object[] { this.id, evn, compressed };
                    this.frontend.SendFrame(content, this.channelId, this.AudioGroup);
                    this.eventTimestamps[evNumber] = Environment.TickCount;
                    return true;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        private ArraySegment<byte> compress(float[] buffer)
        {
            var res = this.opusEncoder.Encode(buffer);
            //            var tmp = _debug_decoder.DecodePacketFloat(res);
            //            this.client.DebugReturn(DebugLevel.INFO, "[PV] Encode: " + res.Length + "/" + buffer.Length /* *4 */ + " " + VoiceUtil.tostr(res) + " " + VoiceUtil.tostr(buffer));
            //            this.client.DebugReturn(DebugLevel.INFO, "[PV] Decode === : " +  this.FrameSizePerChannel + " "+ tmp.Length + " " + VoiceUtil.tostr(tmp));
            return res;
        }

        public void Dispose()
        {
            if (this.opusEncoder != null)
            {
                this.opusEncoder.Dispose();
            }
        }

        #endregion
    }

    #region nonpublic

    internal class RemoteVoice : IDisposable
    {
        // Client.RemoteVoiceInfos support
        internal VoiceInfo Info { get; private set; }
        private int channelId;
        private int playerId;
        private byte voiceId;
        internal RemoteVoice(VoiceClient client, int channelId, int playerId, byte voiceId, VoiceInfo info, byte lastEventNumber)
        {
            this.opusDecoder = new OpusDecoder((SamplingRate)info.SamplingRate, (Channels)info.Channels);
            this.voiceClient = client;
            this.channelId = channelId;
            this.playerId = playerId;
            this.voiceId = voiceId;
            this.Info = info;
            this.lastEvNumber = lastEventNumber;
        }

        internal byte lastEvNumber = 0;
        private OpusDecoder opusDecoder;
        private VoiceClient voiceClient;
        
        internal void receiveBytes(byte[] receivedBytes, byte evNumber)
        {
            // receive-gap detection and compensation
            if (evNumber != this.lastEvNumber)
            {
                int missing = VoiceUtil.byteDiff(evNumber, this.lastEvNumber);
                if (missing != 0)
                {
                    this.voiceClient.frontend.DebugReturn(DebugLevel.ALL, "[PV] evNumer: " + evNumber + " playerVoice.lastEvNumber: " + this.lastEvNumber + " missing: " + missing);
                }

                this.lastEvNumber = evNumber;

                // restoring missing frames
                for (int i = 0; i < missing; i++)
                {
                    this.receiveFrame(null);
                }
                this.voiceClient.FramesLost += missing;
            }
            this.receiveFrame(receivedBytes);
        }
        internal void receiveFrame(byte[] frame)
        {
            var decodedSamples = this.decompress(frame);
            if (this.voiceClient.frontend.OnAudioFrameAction != null) this.voiceClient.frontend.OnAudioFrameAction(this.channelId,this.playerId, this.voiceId, decodedSamples);
        }

        internal float[] decompress(byte[] buffer)
        {
            float[] res;
            if (buffer == null)
            {
                res = this.opusDecoder.DecodePacketFloat(null);
                this.voiceClient.DebugReturn(DebugLevel.ALL, "[PV] lost packet decoded length: " + res.Length);
            }
            else
            {
                res = this.opusDecoder.DecodePacketFloat(buffer);
            }
//            this.client.DebugReturn(DebugLevel.INFO, "[PV]Decode: " + res.Length /* *4 */ + "/" + buffer.Length + " " + Util.tostr(res) + " " + Util.tostr(buffer));
            return res;
        }

        public void Dispose()
        {
            if (this.opusDecoder != null)
            {
                this.opusDecoder.Dispose();
            }
        }
    }

    #endregion

    interface IVoiceFrontend : IVoiceActions
    {
        void DebugReturn(DebugLevel level, string message);
        bool IsChannelJoined(int channelId);
        void SendVoicesInfo(object content, int channelId, int targetPlayerId);
        void SendVoiceRemove(object content, int channelId);
        void SendFrame(object content, int channelId, byte audioGroup);
        string ChannelIdStr(int channelId);
        string PlayerIdStr(int playerId);

        /// <summary>Lost frames simulation ratio.</summary>
        int DebugLostPercent { get; set; }
    }
    interface IVoiceActions
    { 
        /// <summary>
        /// Register a method to be called when remote voice info arrived (after join or new new remote voice creation).
        /// Metod parameters: (int channelId, int playerId, byte voiceId, VoiceInfo voiceInfo);
        /// </summary>
        Action<int, int, byte, VoiceInfo> OnRemoteVoiceInfoAction { get; set; }
        /// <summary>
        /// Register a method to be called when remote voice removed.
        /// Metod parameters: (int channelId, int playerId, byte voiceId)
        /// </summary>
        Action<int, int, byte> OnRemoteVoiceRemoveAction { get; set; }
        /// <summary>
        /// Register a method to be called when new audio frame received. 
        /// Metod parameters: (int channelId, int playerId, byte voiceId, float[] frame)
        /// </summary>
        Action<int, int, byte, float[]> OnAudioFrameAction { get; set; }
    }

    /// <summary>
    /// Base class for Voice clients implamantations
    /// </summary>        
    internal class VoiceClient
    {
        internal IVoiceFrontend frontend;
        
        /// <summary>Lost frames counter.</summary>
        public int FramesLost { get; internal set; }

        /// <summary>Received frames counter.</summary>
        public int FramesReceived { get; private set; }

        /// <summary>Sent frames counter.</summary>
        public int FramesSent { get { int x = 0; foreach (var v in this.localVoices) { x += v.Value.FramesSent; } return x; } }

        /// <summary>Sent frames bytes counter.</summary>
        public int FramesSentBytes { get { int x = 0; foreach (var v in this.localVoices) { x += v.Value.FramesSentBytes; } return x; } }

        /// <summary>Average time required voice packet to return to sender.</summary>
        public int RoundTripTime { get; private set; }

        /// <summary>Average round trip time variation.</summary>
        public int RoundTripTimeVariance { get; private set; }

        /// <summary>Do not log warning when duplicate info received.</summary>
        public bool SuppressInfoDuplicateWarning { get; set; }

        private int prevRtt = 0;

        /// <summary>Iterates through copy of all local voices list.</summary>
        public IEnumerable<LocalVoice> LocalVoices
        {
            get
            {
                var res = new LocalVoice[this.localVoices.Count];
                this.localVoices.Values.CopyTo(res, 0);
                return res;
            }
        }

        /// <summary>Iterates through copy of all local voices list of given channel.</summary>
        public IEnumerable<LocalVoice> LocalVoicesInChannel(int channelId)
        {
            List<LocalVoice> channelVoices;
            if (this.localVoicesPerChannel.TryGetValue(channelId, out channelVoices))
            {
                var res = new LocalVoice[channelVoices.Count];
                channelVoices.CopyTo(res, 0);
                return res;
            }
            else
            {
                return new LocalVoice[0];
            }
        }

        /// <summary>Iterates through all remote voices infos.</summary>
        public IEnumerable<RemoteVoiceInfo> RemoteVoiceInfos
        { 
            get
            {
                foreach (var channelVoices in this.remoteVoices)
                { 
                    foreach (var playerVoices in channelVoices.Value)
                    {
                        foreach (var voice in playerVoices.Value)
                        {
                            yield return new RemoteVoiceInfo(channelVoices.Key, playerVoices.Key, voice.Key, voice.Value.Info);
                        }
                    }
                }
            } 
        }

        /// <summary>Creates Client instance</summary>
        internal VoiceClient(IVoiceFrontend frontend)
        {
            this.frontend = frontend;
        }

        internal void DebugReturn(DebugLevel level, string message)
        {
            this.frontend.DebugReturn(level, message);
        }
        /// <summary>
        /// This method dispatches all available incoming commands and then sends this client's outgoing commands.
        /// Call this method regularly (2..20 times a second).
        /// </summary>
        public void Service()
        {
            foreach (var v in localVoices)
            {
                v.Value.service();
            }
        }

        /// <summary>
        /// Creates new local voice (outgoing audio stream).
        /// </summary>
        /// <param name="audioStream">Object providing audio data for the outgoing stream.</param>
        /// <param name="voiceInfo">Outgoing audio stream parameters (should be set according to Opus encoder restrictions).</param>
        /// <returns>Outgoing stream handler.</returns>
        /// <remarks>
        /// audioStream.SamplingRate and voiceInfo.SamplingRate may do not match. Automatic resampling will occur in this case.
        /// </remarks>
        public LocalVoice CreateLocalVoice(IAudioStream audioStream, VoiceInfo voiceInfo, int channelId)
        {
            // id assigned starting from 1 and up to 255

            byte newId = 0; // non-zero if successfully assigned
            if (voiceIdCnt == 255)
            {
                // try to reuse id
                var ids = new bool[256];
                foreach (var v in localVoices) 
                {
                    ids[v.Value.id] = true;
                }
                // ids[0] is not used
                for (byte id = 1; id != 0 /* < 256 */ ; id++)
                {
                    if (!ids[id])
                    {
                        newId = id;
                        break;
                    }
                }
            }
            else
            {
                voiceIdCnt++;
                newId = voiceIdCnt;
            }

            if (newId != 0)
            {
                var v = new LocalVoice(this.frontend, newId, audioStream, voiceInfo, channelId);
                localVoices[newId] = v;

                List<LocalVoice> voiceList;
                if (!localVoicesPerChannel.TryGetValue(channelId, out voiceList))
                {
                    voiceList = new List<LocalVoice>();
                    localVoicesPerChannel[channelId] = voiceList;
                }
                voiceList.Add(v);

                this.frontend.DebugReturn(DebugLevel.INFO, "[PV] Local voice #" + v.id + " at channel " + this.channelStr(channelId) + " added: src_f=" + audioStream.SamplingRate + " enc_f=" + v.info.SamplingRate + " ch=" + v.info.Channels + " d=" + v.info.FrameDurationUs + " s=" + v.info.FrameSize + " b=" + v.info.Bitrate + " ud=" + voiceInfo.UserData);
                if (this.frontend.IsChannelJoined(channelId))
                {
                    this.frontend.SendVoicesInfo(this.buildVoicesInfo(new List<LocalVoice>() { v }, true), channelId, 0); // broadcast if joined
                }
                v.AudioGroup = this.GlobalAudioGroup;
                return v;
            }
            else
            {
                return null;
            }
        }

        /// <summary>
        /// Removes local voice (outgoing audio stream).
        /// <param name="voice">Handler of outgoing stream to be removed.</param>
        /// </summary>
        public void RemoveLocalVoice(LocalVoice voice)
        {
            this.localVoices.Remove(voice.id);

            this.localVoicesPerChannel[voice.channelId].Remove(voice);
            if (this.frontend.IsChannelJoined(voice.channelId))
            {
                var content = this.buildVoiceRemoveMessage(new List<LocalVoice>() { voice });
                this.frontend.SendVoiceRemove(content, voice.channelId);
            }
            this.frontend.DebugReturn(DebugLevel.INFO, "[PV] Local voice #" + voice.id + " at channel " + this.channelStr(voice.channelId) + " removed");
        }

        internal void sendChannelVoicesInfo(int channelId, int targetPlayerId, bool logInfo = true)
        {
            if (this.frontend.IsChannelJoined(channelId))
            {
                List<LocalVoice> voiceList;
                if (this.localVoicesPerChannel.TryGetValue(channelId, out voiceList))
                {
                    this.frontend.SendVoicesInfo(this.buildVoicesInfo(voiceList, logInfo), channelId, targetPlayerId);
                }
            }
        }

        internal void onVoiceEvent(object content0, int channelId, int playerId, int localPlayerId)
        {
            object[] content = (object[])content0;
            if ((byte)content[0] == (byte)0)
            {
                switch ((byte)content[1])
                {
                    case (byte)EventSubcode.VoiceInfo:
                        this.onVoiceInfo(channelId, playerId, content[2]);
                        break;
                    case (byte)EventSubcode.VoiceRemove:
                        this.onVoiceRemove(channelId, playerId, content[2]);
                        break;
                    case (byte)EventSubcode.DebugEchoRemoveMyVoices:
                        this.removePlayerVoices(channelId, localPlayerId);
                        break;
                    default:
                        this.DebugReturn(DebugLevel.ERROR, "[PV] Unknown sevent subcode " + content[1]);
                        break;
                }
            }
            else
            {                
                byte voiceId = (byte)content[0];
                byte evNumber = (byte)content[1];
                byte[] receivedBytes = (byte[])content[2];
                if (playerId == localPlayerId)
                {
                    LocalVoice voice;
                    if (this.localVoices.TryGetValue(voiceId, out voice))
                    {
                        int sendTime;
                        if (voice.eventTimestamps.TryGetValue(evNumber, out sendTime))
                        {
                            int rtt = Environment.TickCount - sendTime;
                            int rttvar = rtt - prevRtt;
                            prevRtt = rtt;
                            if (rttvar < 0) rttvar = -rttvar;
                            this.RoundTripTimeVariance = (rttvar + RoundTripTimeVariance * 19) / 20;
                            this.RoundTripTime = (rtt + RoundTripTime*19) / 20;
                        }
                    }
                    //internal Dictionary<byte, DateTime> localEventTimestamps = new Dictionary<byte, DateTime>();
                }
                this.onFrame(channelId, playerId, voiceId, evNumber, receivedBytes);
            }
        }
        
        internal byte GlobalAudioGroup
        {
            get { return this.globalAudioGroup; }
            set
            {
                this.globalAudioGroup = value;
                foreach (var v in this.localVoices)
                {
                    v.Value.AudioGroup = this.globalAudioGroup;
                }
            }
        }

        #region nonpublic

        private byte globalAudioGroup;
        private byte voiceIdCnt = 0;

        private Dictionary<byte, LocalVoice> localVoices = new Dictionary<byte, LocalVoice>();
        private Dictionary<int, List<LocalVoice>> localVoicesPerChannel = new Dictionary<int, List<LocalVoice>>();
        // player id -> voice id -> voice
        private Dictionary<int, Dictionary<int, Dictionary<byte, RemoteVoice>>> remoteVoices = new Dictionary<int, Dictionary<int, Dictionary<byte, RemoteVoice>>>();

        internal object[] buildVoicesInfo(ICollection<LocalVoice> voicesToSend, bool logInfo)
        {
            object[] infos = new object[voicesToSend.Count];
            object[] content = new object[] { (byte)0, EventSubcode.VoiceInfo, infos };
            int i = 0;
            foreach (var v in voicesToSend)
            {
                infos[i] = new Hashtable() { 
                    { (byte)EventParam.VoiceId, v.id },
                    { (byte)EventParam.SamplingRate, v.info.SamplingRate },
                    { (byte)EventParam.Channels, v.info.Channels },
                    { (byte)EventParam.FrameDurationUs, v.info.FrameDurationUs },
                    { (byte)EventParam.Bitrate, v.info.Bitrate },                    
                    { (byte)EventParam.UserData, v.info.UserData },
                    { (byte)EventParam.EventNumber, v.evNumber }
                };
                i++;

                if (logInfo)
                {
                    this.frontend.DebugReturn(DebugLevel.INFO, "[PV] Sending info for voice #" + v.id + " at channel " + this.channelStr(v.channelId) + ": f=" + v.info.SamplingRate + ", ch=" + v.info.Channels + " d=" + v.info.FrameDurationUs + " s=" + v.info.FrameSize + " b=" + v.info.Bitrate + " ev=" + v.evNumber);
                }
            }
            return content;
        }

        private object[] buildVoiceRemoveMessage(List<LocalVoice> voicesToSend)
        {            
            byte[] ids = new byte[voicesToSend.Count];
            object[] content = new object[] { (byte)0, EventSubcode.VoiceRemove, ids };

            int i = 0;
            foreach (var v in voicesToSend)
            {
                ids[i] = v.id;
                i++;
                this.frontend.DebugReturn(DebugLevel.INFO, "[PV] Voice #" + v.id + " at channel " + this.channelStr(v.channelId) + " remove sent");                
            }

            return content;
        }

        internal void clearRemoteVoices()
        {
            if (this.frontend.OnRemoteVoiceRemoveAction != null)
            {
                foreach (var channelVoices in remoteVoices)
                {
                    foreach (var playerVoices in channelVoices.Value)
                    {
                        foreach (var voice in playerVoices.Value)
                        {
                            this.frontend.OnRemoteVoiceRemoveAction(channelVoices.Key, playerVoices.Key, voice.Key);
                        }
                    }
                }
            }
            remoteVoices.Clear();
            this.frontend.DebugReturn(DebugLevel.INFO, "[PV] Remote voices cleared");
        }

        internal void clearRemoteVoicesInChannel(int channelId)
        {
            Dictionary<int, Dictionary<byte, RemoteVoice>> channelVoices = null;
            if (this.remoteVoices.TryGetValue(channelId, out channelVoices))
            {
                if (this.frontend.OnRemoteVoiceRemoveAction != null)
                {
                    foreach (var playerVoices in channelVoices)
                    {
                        foreach (var voice in playerVoices.Value)
                        {
                            this.frontend.OnRemoteVoiceRemoveAction(channelId, playerVoices.Key, voice.Key);
                        }
                    }
                }
                this.remoteVoices.Remove(channelId);
            }
            this.frontend.DebugReturn(DebugLevel.INFO, "[PV] Remote voices for channel " + this.channelStr(channelId) + " cleared");
        }
        private void onVoiceInfo(int channelId, int playerId, object payload)
        {
            Dictionary<int, Dictionary<byte, RemoteVoice>> channelVoices = null;
            if (!this.remoteVoices.TryGetValue(channelId, out channelVoices))
            {
                channelVoices = new Dictionary<int, Dictionary<byte, RemoteVoice>>();
                this.remoteVoices[channelId] = channelVoices;
            }
            Dictionary<byte, RemoteVoice> playerVoices = null;

            if (!channelVoices.TryGetValue(playerId, out playerVoices))
            {
                playerVoices = new Dictionary<byte, RemoteVoice>();
                channelVoices[playerId] = playerVoices;
            }
            
            foreach (var el in (object[])payload)
            {
                var h = (Hashtable)el;
                var voiceId = (byte)h[(byte)EventParam.VoiceId];
                if (!playerVoices.ContainsKey(voiceId))
                {
                    var samplingRate = (int)h[(byte)EventParam.SamplingRate];
                    var channels = (int)h[(byte)EventParam.Channels];
                    var frameDurationUs = (int)h[(byte)EventParam.FrameDurationUs];
                    var bitrate = (int)h[(byte)EventParam.Bitrate];
                    var userData = h[(byte)EventParam.UserData];

                    var eventNumber = (byte)h[(byte)EventParam.EventNumber];

                    this.frontend.DebugReturn(DebugLevel.INFO, "[PV] Channel " + this.channelStr(channelId) + " player " + this.playerStr(playerId) + " voice #" + voiceId + " info received: f=" + samplingRate + ", ch=" + channels + " d=" + frameDurationUs + " b=" + bitrate + " ud=" + userData + " ev=" + eventNumber);

                    var info = new VoiceInfo((int)samplingRate, (int)channels, frameDurationUs, bitrate, userData);
                    playerVoices[voiceId] = new RemoteVoice(this, channelId, playerId, voiceId, info, eventNumber);
                    if (this.frontend.OnRemoteVoiceInfoAction != null) this.frontend.OnRemoteVoiceInfoAction(channelId, playerId, voiceId, info);
                }
                else
                {
                    if (!this.SuppressInfoDuplicateWarning)
                    {
                        this.frontend.DebugReturn(DebugLevel.WARNING, "[PV] Info duplicate for voice #" + voiceId + " of player " + this.playerStr(playerId) + " at channel " + this.channelStr(channelId));
                    }
                }
            }
        }

        private void onVoiceRemove(int channelId, int playerId, object payload)
        {
            var voiceIds = (byte[])payload;
            Dictionary<int, Dictionary<byte, RemoteVoice>> channelVoices = null;
            if (this.remoteVoices.TryGetValue(channelId, out channelVoices))
            {
                Dictionary<byte, RemoteVoice> playerVoices = null;
                if (channelVoices.TryGetValue(playerId, out playerVoices))
                {
                    foreach (var voiceId in voiceIds)
                    {
                        if (playerVoices.Remove(voiceId))
                        {
                            this.frontend.DebugReturn(DebugLevel.INFO, "[PV] Remote voice #" + voiceId + " of player " + this.playerStr(playerId) + " at channel " + this.channelStr(channelId) + " removed");
                            if (this.frontend.OnRemoteVoiceRemoveAction != null)
                            {
                                this.frontend.OnRemoteVoiceRemoveAction(channelId, playerId, voiceId);
                            }
                        }
                        else
                        {
                            this.frontend.DebugReturn(DebugLevel.WARNING, "[PV] Remote voice #" + voiceId + " of player " + this.playerStr(playerId) + " at channel " + this.channelStr(channelId) + " not found when trying to remove");
                        }
                    }
                }
                else
                {
                    this.frontend.DebugReturn(DebugLevel.WARNING, "[PV] Remote voice list of player " + this.playerStr(playerId) + " at channel " + this.channelStr(channelId) + " not found when trying to remove voice(s)");
                }
            }
            else
            {
                this.frontend.DebugReturn(DebugLevel.WARNING, "[PV] Remote voice list of channel " + this.channelStr(channelId) + " not found when trying to remove voice(s)");
            }
        }

        Random rnd = new Random();
        private void onFrame(int channelId, int playerId, byte voiceId, byte evNumber, byte[] receivedBytes)
        {
            
            if (this.frontend.DebugLostPercent > 0 && rnd.Next(100) < this.frontend.DebugLostPercent)
            {
                this.frontend.DebugReturn(DebugLevel.WARNING, "[PV] Debug Lost Sim: 1 packet dropped");
                return;
            }

            FramesReceived++;

            Dictionary<int, Dictionary<byte, RemoteVoice>> channelVoices = null;
            if (this.remoteVoices.TryGetValue(channelId, out channelVoices))
            {
                Dictionary<byte, RemoteVoice> playerVoices = null;
                if (channelVoices.TryGetValue(playerId, out playerVoices))
                {

                    RemoteVoice voice = null;
                    if (playerVoices.TryGetValue(voiceId, out voice))
                    {
                        voice.receiveBytes(receivedBytes, evNumber);
                    }
                    else
                    {
                        this.frontend.DebugReturn(DebugLevel.WARNING, "[PV] Frame event for not inited voice #" + voiceId + " of player " + this.playerStr(playerId) + " at channel " + this.channelStr(channelId));
                    }
                }
                else
                {
                    this.frontend.DebugReturn(DebugLevel.WARNING, "[PV] Frame event for voice #" + voiceId + " of not inited player " + this.playerStr(playerId) + " at channel " + this.channelStr(channelId));
                }
            }
            else
            {
                this.frontend.DebugReturn(DebugLevel.WARNING, "[PV] Frame event for voice #" + voiceId + " of not inited channel " + this.channelStr(channelId));
            }
        }
        
        internal bool removePlayerVoices(int channelId, int playerId)
        {
            Dictionary<int, Dictionary<byte, RemoteVoice>> channelVoices = null;
            if (this.remoteVoices.TryGetValue(channelId, out channelVoices))
            {
                Dictionary<byte, RemoteVoice> playerVoices = null;
                if (channelVoices.TryGetValue(playerId, out playerVoices))
                {
                    this.remoteVoices.Remove(playerId);
                    foreach (var v in playerVoices)
                    {
                        if (this.frontend.OnRemoteVoiceRemoveAction != null)
                        {
                            this.frontend.OnRemoteVoiceRemoveAction(channelId, playerId, v.Key);
                        }
                    }
                    return true;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        internal string channelStr(int channelId)
        {
            var str = this.frontend.ChannelIdStr(channelId);
            if (str != null)
            {
                return "#" + channelId + "(" + str + ")";
            }
            else
            {
                return "#" + channelId;
            }
        }

        internal string playerStr(int playerId)
        {
            var str = this.frontend.PlayerIdStr(playerId);
            if (str != null)
            {
                return "#" + playerId + "(" + str + ")";
            }
            else
            {
                return "#" + playerId;
            }
        }
        //public string ToStringFull()
        //{
        //    return string.Format("Photon.Voice.Client, local: {0}, remote: {1}",  localVoices.Count, remoteVoices.Count);
        //}

        #endregion

    }

    /// <summary>
    /// Audio parameters and data conversion utilities.
    /// </summary>
    public static class VoiceUtil
    {
        internal static byte byteDiff(byte latest, byte last)
        {
            return (byte)(latest - (last + 1));
        }

        internal static void Resample(float[] src, float[] dst, int channels)
        {
            //TODO: Low-pass filter
            for (int i = 0; i < dst.Length; i += channels)
            {
                var interp = (i * src.Length / dst.Length);
                for (int ch = 0; ch < channels; ch++)
                {
                    dst[i + ch] = src[interp + ch];
                }
            }
        }

        internal static string tostr<T>(T[] x, int lim = 10)
        {
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            for (var i = 0; i < (x.Length < lim ? x.Length : lim); i++)
            {
                b.Append("-");
                b.Append(x[i]);
            }
            return b.ToString();
        }

        internal static int bestEncoderSampleRate(int f)
        {
            int diff = int.MaxValue;
            int res = (int)SamplingRate.Sampling48000;
            foreach (var x in Enum.GetValues(typeof(SamplingRate)))
            {
                var d = Math.Abs((int)x - f);
                if (d < diff)
                {
                    diff = d;
                    res = (int)x;
                }
            }
            return res;
        }
    }

    /// <summary>
    /// Utility for measurement audio signal parameters.
    /// </summary>
    public class LevelMeter
    {
        // sum of all values in buffer
        float ampSum;
        // max of values from start buffer to current pos
        float ampPeak;
        int bufferSize;
        float[] buffer;
        int prevValuesPtr;

        float accumAvgPeakAmpSum;
        int accumAvgPeakAmpCount;

        internal LevelMeter(int samplingRate, int numChannels)
        {
            this.bufferSize = samplingRate * numChannels / 2; // 1/2 sec
            this.buffer = new float[this.bufferSize];
        }

        /// <summary>
        /// Average of last values in current 1/2 sec. buffer.
        /// </summary>
        public float CurrentAvgAmp { get { return ampSum / this.bufferSize; } }

        /// <summary>
        /// Max of last values in 1/2 sec. buffer as it was at last buffer wrap.
        /// </summary>
        public float CurrentPeakAmp
        {
            get;
            private set;
        }

        /// <summary>
        /// Average of CurrentPeakAmp's since last reset.
        /// </summary>
        public float AccumAvgPeakAmp { get { return this.accumAvgPeakAmpCount == 0 ? 0 : accumAvgPeakAmpSum / this.accumAvgPeakAmpCount; } }

        /// <summary>
        /// Reset LevelMeter.AccumAvgPeakAmp.
        /// </summary>
        public void ResetAccumAvgPeakAmp() { this.accumAvgPeakAmpSum = 0; this.accumAvgPeakAmpCount = 0; }

        internal void process(float[] buf)
        {
            foreach (var v in buf)
            {
                var a = v;
                if (a < 0)
                {
                    a = -a;
                }
                ampSum = ampSum + a - this.buffer[this.prevValuesPtr];
                this.buffer[this.prevValuesPtr] = a;

                if (ampPeak < a)
                {
                    ampPeak = a;
                }
                if (this.prevValuesPtr == 0)
                {
                    CurrentPeakAmp = ampPeak;
                    ampPeak = 0;
                    accumAvgPeakAmpSum += CurrentPeakAmp;
                    accumAvgPeakAmpCount++;
                }

                this.prevValuesPtr = (this.prevValuesPtr + 1) % this.bufferSize;
            }            
        }
    }

    /// <summary>
    /// Simple voice activity detector triggered by signal level.
    /// </summary>
    public class VoiceDetector
    {
        /// <summary>If true, voice detection enabled.</summary>
        public bool On { get; set; }
        /// <summary>Voice detected as soon as signal level exceeds threshold.</summary>
        public float Threshold { get; set; }

        /// <summary>If true, voice detected.</summary>
        public bool Detected { get; private set; }

        /// <summary>Keep detected state during this time after signal level dropped below threshold.</summary>
        public int ActivityDelayMs {
            get { return this.activityDelay; }
            set {
                this.activityDelay = value;
                this.activityDelayValuesCount = value * valuesCountPerSec / 1000;
            } 
        }

        int activityDelay;
        int autoSilenceCounter = 0;
        int valuesCountPerSec;
        int activityDelayValuesCount;

        internal VoiceDetector(int samplingRate, int numChannels)
        {
            this.valuesCountPerSec = samplingRate * numChannels;
            this.Threshold = 0.01f;
            this.ActivityDelayMs = 500;
        }

        internal void process(float[] buffer)
        {
            if (this.On)
            {
                foreach (var s in buffer)
                {
                    if (s > this.Threshold)
                    {
                        this.Detected = true;
                        this.autoSilenceCounter = 0;
                    }
                    else
                    {
                        this.autoSilenceCounter++;
                    }
                }
                if (this.autoSilenceCounter > this.activityDelayValuesCount)
                {
                    this.Detected = false;
                }
            }
            else
            {
                this.Detected = false;
            }
        }
    }
}