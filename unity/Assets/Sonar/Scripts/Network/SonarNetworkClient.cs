using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using Sonar.Core;
using Sonar.Raycasting;

namespace Sonar.Network
{
    /// <summary>
    /// Single TCP connection to Python processor.
    /// Sends length-prefixed depth/material frames, receives length-prefixed
    /// intensity images. See shared/PROTOCOL.md for the wire format.
    /// </summary>
    public class SonarNetworkClient : MonoBehaviour
    {
        [Header("Configuration")]
        public SonarConfig config;

        [Header("Network")]
        public string host = "127.0.0.1";
        public int port = 9000;

        [Header("Status")]
        [SerializeField] private bool connected;
        [SerializeField] private int pingsProcessed;

        /// <summary>Fired on main thread when a new intensity image is ready.</summary>
        public event Action<uint, Texture2D> OnImageReady;

        private const int FRAME_HEADER_SIZE = 88;
        private const int RESPONSE_HEADER_SIZE = 8;

        private TcpClient _tcp;
        private NetworkStream _stream;
        private SonarRaycaster _raycaster;

        // Send from main thread
        private byte[] _sendBuffer;
        private readonly object _sendLock = new object();

        // Receive on background thread, deliver on main thread
        private Thread _receiveThread;
        private volatile bool _running;
        private readonly ConcurrentQueue<(uint pingID, int beams, int bins, float[] data)> _completed = new();

        private Texture2D _texture;
        private float _reconnectTimer;

        private void Start()
        {
            _raycaster = GetComponent<SonarRaycaster>();
            if (_raycaster == null)
            {
                Debug.LogError("SonarNetworkClient: No SonarRaycaster found!");
                enabled = false;
                return;
            }

            int gridCells = config.azimuthSamples * config.elevationSamples;
            int maxPayload = FRAME_HEADER_SIZE + gridCells * (sizeof(float) + sizeof(byte));
            _sendBuffer = new byte[maxPayload];

            _texture = new Texture2D(
                config.numberOfBeams,
                config.numberOfRangeBins,
                TextureFormat.RFloat, false
            );
            _texture.filterMode = FilterMode.Bilinear;
            _texture.wrapMode = TextureWrapMode.Clamp;

            _raycaster.OnFrameReady += HandleFrameReady;

            TryConnect();
        }

        private void OnDestroy()
        {
            if (_raycaster != null)
                _raycaster.OnFrameReady -= HandleFrameReady;

            _running = false;
            _receiveThread?.Join(1000);
            _stream?.Close();
            _tcp?.Close();

            if (_texture != null)
                Destroy(_texture);
        }

        private void Update()
        {
            // Deliver completed images on main thread
            while (_completed.TryDequeue(out var item))
            {
                ApplyToTexture(item.pingID, item.beams, item.bins, item.data);
            }

            // Auto-reconnect
            if (!connected)
            {
                _reconnectTimer += Time.deltaTime;
                if (_reconnectTimer > 2f)
                {
                    _reconnectTimer = 0f;
                    TryConnect();
                }
            }
        }

        private void TryConnect()
        {
            try
            {
                _tcp = new TcpClient();
                _tcp.NoDelay = true;
                _tcp.Connect(host, port);
                _stream = _tcp.GetStream();
                connected = true;

                _running = true;
                _receiveThread = new Thread(ReceiveLoop)
                {
                    IsBackground = true,
                    Name = "SonarNetworkClient.Receive"
                };
                _receiveThread.Start();

                Debug.Log($"SonarNetworkClient: connected to {host}:{port}");
            }
            catch (SocketException)
            {
                connected = false;
                // Will retry in Update
            }
        }

        private void HandleFrameReady(DepthFrame frame)
        {
            if (!connected) return;

            int cells = frame.width * frame.height;
            int payloadSize = FRAME_HEADER_SIZE + cells * (sizeof(float) + sizeof(byte));

            if (_sendBuffer.Length < payloadSize)
                _sendBuffer = new byte[payloadSize];

            int offset = 0;

            // --- Frame header (88 bytes) ---
            WriteUInt32(_sendBuffer, ref offset, frame.pingID);
            WriteFloat64(_sendBuffer, ref offset, frame.timestamp);
            WriteFloat32(_sendBuffer, ref offset, frame.sonarPos.x);
            WriteFloat32(_sendBuffer, ref offset, frame.sonarPos.y);
            WriteFloat32(_sendBuffer, ref offset, frame.sonarPos.z);
            WriteFloat32(_sendBuffer, ref offset, frame.sonarRot.x);
            WriteFloat32(_sendBuffer, ref offset, frame.sonarRot.y);
            WriteFloat32(_sendBuffer, ref offset, frame.sonarRot.z);
            WriteFloat32(_sendBuffer, ref offset, frame.sonarRot.w);
            WriteUInt16(_sendBuffer, ref offset, (ushort)config.numberOfBeams);
            WriteUInt16(_sendBuffer, ref offset, (ushort)config.numberOfRangeBins);
            WriteFloat32(_sendBuffer, ref offset, config.maxRange);
            WriteFloat32(_sendBuffer, ref offset, config.swathAngleDeg);
            WriteFloat32(_sendBuffer, ref offset, config.verticalFOVDeg);
            WriteFloat32(_sendBuffer, ref offset, config.capturePaddingDeg);
            WriteFloat32(_sendBuffer, ref offset, config.mountTiltDeg);
            WriteFloat32(_sendBuffer, ref offset, config.speedOfSound);
            WriteFloat32(_sendBuffer, ref offset, config.sourceLevel_dB);
            WriteFloat32(_sendBuffer, ref offset, config.pulseLength_s);
            WriteFloat32(_sendBuffer, ref offset, config.frequency_Hz);
            WriteUInt16(_sendBuffer, ref offset, (ushort)frame.width);
            WriteUInt16(_sendBuffer, ref offset, (ushort)frame.height);

            // Flags: bit 0 = sonar submerged (Python returns noise only when dry)
            bool inWater = !config.useWaterLevel || frame.sonarPos.y < config.waterLevelY;
            WriteUInt32(_sendBuffer, ref offset, inWater ? 1u : 0u);

            // --- Range plane (float32 × cells) ---
            // Bulk copy: all Unity target platforms are little-endian.
            Buffer.BlockCopy(frame.ranges, 0, _sendBuffer, offset, cells * sizeof(float));
            offset += cells * sizeof(float);

            // --- Material plane (uint8 × cells) ---
            Buffer.BlockCopy(frame.materials, 0, _sendBuffer, offset, cells);
            offset += cells;

            // Send: [4-byte length] + [payload]
            byte[] lengthHeader = BitConverter.GetBytes((uint)payloadSize);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(lengthHeader);

            lock (_sendLock)
            {
                try
                {
                    _stream.Write(lengthHeader, 0, 4);
                    _stream.Write(_sendBuffer, 0, payloadSize);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"SonarNetworkClient: send failed — {ex.Message}");
                    connected = false;
                }
            }
        }

        // --- Receive thread ---

        private void ReceiveLoop()
        {
            while (_running && connected)
            {
                try
                {
                    // Read 4-byte length
                    byte[] lenBuf = ReadExact(4);
                    if (lenBuf == null) break;
                    uint length = BitConverter.ToUInt32(lenBuf, 0);

                    // Read payload
                    byte[] payload = ReadExact((int)length);
                    if (payload == null) break;

                    if (payload.Length < RESPONSE_HEADER_SIZE) continue;

                    uint pingID = BitConverter.ToUInt32(payload, 0);
                    ushort beams = BitConverter.ToUInt16(payload, 4);
                    ushort bins = BitConverter.ToUInt16(payload, 6);

                    int dataLen = beams * bins * 4;
                    if (payload.Length < RESPONSE_HEADER_SIZE + dataLen) continue;

                    float[] data = new float[beams * bins];
                    Buffer.BlockCopy(payload, RESPONSE_HEADER_SIZE, data, 0, dataLen);

                    pingsProcessed++;
                    _completed.Enqueue((pingID, beams, bins, data));
                }
                catch (Exception ex) when (_running)
                {
                    Debug.LogWarning($"SonarNetworkClient: receive error — {ex.Message}");
                    connected = false;
                    break;
                }
            }
        }

        private byte[] ReadExact(int n)
        {
            byte[] buf = new byte[n];
            int read = 0;
            while (read < n)
            {
                int chunk = _stream.Read(buf, read, n - read);
                if (chunk == 0) return null; // disconnected
                read += chunk;
            }
            return buf;
        }

        private void ApplyToTexture(uint pingID, int beams, int bins, float[] data)
        {
            if (_texture.width != beams || _texture.height != bins)
            {
                Destroy(_texture);
                _texture = new Texture2D(beams, bins, TextureFormat.RFloat, false);
                _texture.filterMode = FilterMode.Bilinear;
                _texture.wrapMode = TextureWrapMode.Clamp;
            }

            _texture.SetPixelData(data, 0);
            _texture.Apply(false);
            OnImageReady?.Invoke(pingID, _texture);
        }

        // --- Little-endian writers ---

        private static void WriteUInt32(byte[] buf, ref int off, uint val)
        {
            buf[off++] = (byte)(val);
            buf[off++] = (byte)(val >> 8);
            buf[off++] = (byte)(val >> 16);
            buf[off++] = (byte)(val >> 24);
        }

        private static void WriteUInt16(byte[] buf, ref int off, ushort val)
        {
            buf[off++] = (byte)(val);
            buf[off++] = (byte)(val >> 8);
        }

        private static void WriteFloat32(byte[] buf, ref int off, float val)
        {
            byte[] bytes = BitConverter.GetBytes(val);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            Buffer.BlockCopy(bytes, 0, buf, off, 4);
            off += 4;
        }

        private static void WriteFloat64(byte[] buf, ref int off, double val)
        {
            byte[] bytes = BitConverter.GetBytes(val);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            Buffer.BlockCopy(bytes, 0, buf, off, 8);
            off += 8;
        }
    }
}
