using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TurboCore
{
    public enum PacketType : byte
    {
        AuthChallenge = 1,
        AuthResponse = 2,
        AuthResult = 21,
        ScreenFrame = 3,
        MouseInput = 4,
        KeyInput = 5,
        ConfigUpdate = 6,
        ShellExec = 7,
        ShellOutput = 8,
        FileUploadStart = 9,
        FileUploadChunk = 10,
        FileUploadEnd = 11,
        FileDownloadRequest = 12,
        FileListRequest = 13,
        FileListResponse = 14,
        ClipboardSync = 15,
        SystemAction = 16,
        Ping = 17,
        Pong = 18,
        ForceFullFrame = 19
    }

    public enum BandwidthProfile : byte
    {
        UltraLow_2G = 0,     // 42% scale, Grayscale + 16-Level Posterized, JPEG Q=20, 22 FPS (~10-24 KB/s)
        Low_3G = 1,          // 55% scale, Color JPEG Q=35, 32 FPS
        Balanced_4G = 2,     // 75% scale, Color JPEG Q=52, 45 FPS
        Turbo_Broadband = 3, // 100% scale, Color JPEG Q=78, 60 FPS
        AutoAdaptive = 4     // Automatically shifts up to 60 FPS on fast/LAN links and scales down on slow WAN
    }

    public enum MouseActionType : byte
    {
        Move = 0,
        LeftDown = 1,
        LeftUp = 2,
        RightDown = 3,
        RightUp = 4,
        MiddleDown = 5,
        MiddleUp = 6,
        Wheel = 7
    }

    public class StreamConfig
    {
        public BandwidthProfile Profile = BandwidthProfile.AutoAdaptive;
        public float Scale = 0.65f;
        public long JpegQuality = 45;
        public bool Grayscale = false;
        public bool LowBitPosterize = false;
        public int TargetFps = 20;
        public int MonitorIndex = 0;

        public void ApplyPreset(BandwidthProfile profile)
        {
            Profile = profile;
            switch (profile)
            {
                case BandwidthProfile.UltraLow_2G:
                    Scale = 0.42f;
                    JpegQuality = 20;
                    Grayscale = true;
                    LowBitPosterize = true;
                    TargetFps = 22;
                    break;
                case BandwidthProfile.Low_3G:
                    Scale = 0.55f;
                    JpegQuality = 35;
                    Grayscale = false;
                    LowBitPosterize = false;
                    TargetFps = 32;
                    break;
                case BandwidthProfile.Balanced_4G:
                    Scale = 0.75f;
                    JpegQuality = 52;
                    Grayscale = false;
                    LowBitPosterize = false;
                    TargetFps = 45;
                    break;
                case BandwidthProfile.Turbo_Broadband:
                    Scale = 1.0f;
                    JpegQuality = 78;
                    Grayscale = false;
                    LowBitPosterize = false;
                    TargetFps = 60;
                    break;
            }
        }
    }

    public class SecurityContext : IDisposable
    {
        public readonly byte[] EncKey;
        public readonly byte[] MacKey;
        private long _sendSeq = 0;
        private long _recvSeq = 0;

        private readonly Aes _aes;
        private readonly RandomNumberGenerator _rng;

        public SecurityContext(string secretPinOrPassword, byte[] salt16, byte[] challenge16)
        {
            byte[] combinedSalt = new byte[salt16.Length + challenge16.Length];
            Buffer.BlockCopy(salt16, 0, combinedSalt, 0, salt16.Length);
            Buffer.BlockCopy(challenge16, 0, combinedSalt, salt16.Length, challenge16.Length);

            using (Rfc2898DeriveBytes kdf = new Rfc2898DeriveBytes(secretPinOrPassword.Trim(), combinedSalt, 15000, HashAlgorithmName.SHA256))
            {
                EncKey = kdf.GetBytes(32);
                MacKey = kdf.GetBytes(32);
            }

            _aes = Aes.Create();
            _aes.KeySize = 256;
            _aes.Key = EncKey;
            _aes.Mode = CipherMode.CBC;
            _aes.Padding = PaddingMode.PKCS7;
            _rng = RandomNumberGenerator.Create();
        }

        public byte[] ComputeHandshakeProof(byte[] challenge16, string roleTag)
        {
            using (HMACSHA256 hmac = new HMACSHA256(MacKey))
            {
                byte[] tagBytes = Encoding.UTF8.GetBytes(roleTag);
                byte[] buf = new byte[challenge16.Length + tagBytes.Length];
                Buffer.BlockCopy(challenge16, 0, buf, 0, challenge16.Length);
                Buffer.BlockCopy(tagBytes, 0, buf, challenge16.Length, tagBytes.Length);
                return hmac.ComputeHash(buf);
            }
        }

        public byte[] EncryptAndAuthenticate(PacketType type, byte[] plainPayload)
        {
            if (plainPayload == null) plainPayload = new byte[0];
            long seq = Interlocked.Increment(ref _sendSeq);

            byte[] iv = new byte[16];
            lock (_rng)
            {
                _rng.GetBytes(iv);
            }

            byte[] cipherBytes;
            lock (_aes)
            {
                using (ICryptoTransform enc = _aes.CreateEncryptor(EncKey, iv))
                {
                    cipherBytes = enc.TransformFinalBlock(plainPayload, 0, plainPayload.Length);
                }
            }

            // Envelope: [8B Sequence][16B IV][32B HMAC-SHA256][CipherBytes]
            byte[] seqBytes = BitConverter.GetBytes(seq);
            byte[] mac;
            using (HMACSHA256 hmac = new HMACSHA256(MacKey))
            {
                hmac.TransformBlock( new byte[] { (byte)type }, 0, 1, null, 0);
                hmac.TransformBlock(seqBytes, 0, 8, null, 0);
                hmac.TransformBlock(iv, 0, 16, null, 0);
                hmac.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                mac = hmac.Hash;
            }

            byte[] envelope = new byte[8 + 16 + 32 + cipherBytes.Length];
            Buffer.BlockCopy(seqBytes, 0, envelope, 0, 8);
            Buffer.BlockCopy(iv, 0, envelope, 8, 16);
            Buffer.BlockCopy(mac, 0, envelope, 24, 32);
            Buffer.BlockCopy(cipherBytes, 0, envelope, 56, cipherBytes.Length);
            return envelope;
        }

        public byte[] VerifyAndDecrypt(PacketType type, byte[] envelope)
        {
            if (envelope == null || envelope.Length < 56)
            {
                throw new CryptographicException("Encrypted packet truncated or malformed.");
            }

            long seq = BitConverter.ToInt64(envelope, 0);
            long expectedMin = Interlocked.Read(ref _recvSeq);
            if (seq <= expectedMin)
            {
                throw new CryptographicException("Replay attack detected: duplicate or out-of-order sequence number.");
            }

            byte[] seqBytes = new byte[8];
            byte[] iv = new byte[16];
            byte[] receivedMac = new byte[32];
            int cipherLen = envelope.Length - 56;
            byte[] cipherBytes = new byte[cipherLen];

            Buffer.BlockCopy(envelope, 0, seqBytes, 0, 8);
            Buffer.BlockCopy(envelope, 8, iv, 0, 16);
            Buffer.BlockCopy(envelope, 24, receivedMac, 0, 32);
            Buffer.BlockCopy(envelope, 56, cipherBytes, 0, cipherLen);

            byte[] computedMac;
            using (HMACSHA256 hmac = new HMACSHA256(MacKey))
            {
                hmac.TransformBlock(new byte[] { (byte)type }, 0, 1, null, 0);
                hmac.TransformBlock(seqBytes, 0, 8, null, 0);
                hmac.TransformBlock(iv, 0, 16, null, 0);
                hmac.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                computedMac = hmac.Hash;
            }

            if (!TurboProtocol.ConstantTimeEquals(receivedMac, computedMac))
            {
                throw new CryptographicException("HMAC-SHA256 integrity verification failed (tampered packet).");
            }

            Interlocked.Exchange(ref _recvSeq, seq);

            lock (_aes)
            {
                using (ICryptoTransform dec = _aes.CreateDecryptor(EncKey, iv))
                {
                    return dec.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                }
            }
        }

        public void Dispose()
        {
            if (_aes != null) _aes.Dispose();
            if (_rng != null) _rng.Dispose();
            if (EncKey != null) Array.Clear(EncKey, 0, EncKey.Length);
            if (MacKey != null) Array.Clear(MacKey, 0, MacKey.Length);
        }
    }

    /// <summary>
    /// Per-IP lockout (5 wrong keys, then 5 minutes) for peers whose address identifies them: LAN, Tailscale
    /// and direct WAN. Loopback peers all share one address and go through <see cref="SharedAddressThrottle"/>.
    /// </summary>
    public static class BruteForceProtector
    {
        private class AttemptRecord
        {
            public int Failures;
            public DateTime LockoutUntilUtc;
        }

        private static readonly ConcurrentDictionary<string, AttemptRecord> _records = new ConcurrentDictionary<string, AttemptRecord>();

        public static bool IsIpLockedOut(string ip, out int remainingSeconds)
        {
            remainingSeconds = 0;
            AttemptRecord rec;
            if (_records.TryGetValue(ip, out rec))
            {
                lock (rec)
                {
                    if (DateTime.UtcNow < rec.LockoutUntilUtc)
                    {
                        remainingSeconds = Math.Max(1, (int)(rec.LockoutUntilUtc - DateTime.UtcNow).TotalSeconds);
                        return true;
                    }
                }
            }
            return false;
        }

        public static int RecordFailedAttempt(string ip)
        {
            AttemptRecord rec = _records.GetOrAdd(ip, _ => new AttemptRecord());
            lock (rec)
            {
                // A previous 5-minute lockout has expired: start a fresh window of 5 attempts instead of
                // re-locking the IP on its very next mistake ("Attempt 6/5").
                if (rec.Failures >= 5 && DateTime.UtcNow >= rec.LockoutUntilUtc)
                {
                    rec.Failures = 0;
                }
                rec.Failures++;
                if (rec.Failures >= 5)
                {
                    rec.LockoutUntilUtc = DateTime.UtcNow.AddMinutes(5);
                }
                return rec.Failures;
            }
        }

        public static void ResetAttempts(string ip)
        {
            AttemptRecord removed;
            _records.TryRemove(ip, out removed);
        }
    }

    /// <summary>
    /// Sign-in throttle for peers that connect from a loopback address. Every Internet client arrives through
    /// the pinggy tunnel, whose ssh.exe forwards to 127.0.0.1, so they all share that address. The per-IP
    /// lockout let anyone on the Internet lock every Internet user out with 5 wrong keys, and did nothing
    /// against a single attacker. This throttle never locks anyone out. It checks one key at a time and, while
    /// wrong keys keep coming, spaces the checks out (1, 2, 4, 8, 16, then 30 s), so everyone on the Internet
    /// together gets at most one guess per 30 s. A right key is still accepted at its turn and clears the
    /// slowdown, and 10 minutes without a wrong key clear it too.
    /// </summary>
    public static class SharedAddressThrottle
    {
        public const int FreeFailures = 2;
        public const int MaxDelaySeconds = 30;
        /// <summary>Connections waiting for a key check at once; more are turned away as "busy".</summary>
        public const int MaxWaiting = 16;
        private static readonly TimeSpan QuietReset = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan MaxTurnWait = TimeSpan.FromSeconds(60);

        private static readonly SemaphoreSlim _turnGate = new SemaphoreSlim(1, 1);
        private static readonly object _sync = new object();
        private static int _waiting;
        private static int _failureStreak;
        private static DateTime _lastFailureUtc = DateTime.MinValue;
        private static DateTime _nextCheckUtc = DateTime.MinValue;

        /// <summary>One connection's turn to have its key checked. Dispose it as soon as the check is done.</summary>
        public sealed class Turn : IDisposable
        {
            private bool? _keyWasRight;
            private int _disposed;

            internal Turn() { }

            public void RecordResult(bool keyWasRight)
            {
                _keyWasRight = keyWasRight;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) EndTurn(_keyWasRight);
            }
        }

        public static bool IsSharedAddress(IPAddress address)
        {
            return address != null && IPAddress.IsLoopback(address);
        }

        /// <summary>Pause before the next key check after this many wrong keys in a row.</summary>
        public static TimeSpan DelayAfterFailures(int failures)
        {
            if (failures <= FreeFailures) return TimeSpan.Zero;
            int doublings = failures - FreeFailures - 1;
            if (doublings >= 5) return TimeSpan.FromSeconds(MaxDelaySeconds);
            return TimeSpan.FromSeconds(Math.Min(MaxDelaySeconds, 1 << doublings));
        }

        public static int FailureStreak
        {
            get { lock (_sync) { ForgetIfQuiet(); return _failureStreak; } }
        }

        /// <summary>
        /// Waits until this connection may have its key checked. Returns null, without checking anything, when
        /// the Host is busy: more than <see cref="MaxWaiting"/> connections are waiting, or no turn came in 60 s.
        /// </summary>
        public static async Task<Turn> WaitForTurnAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _waiting) > MaxWaiting)
            {
                Interlocked.Decrement(ref _waiting);
                return null;
            }

            bool entered = false;
            try
            {
                entered = await _turnGate.WaitAsync(MaxTurnWait, ct);
                if (!entered) return null;

                TimeSpan pause;
                lock (_sync)
                {
                    ForgetIfQuiet();
                    pause = _nextCheckUtc - DateTime.UtcNow;
                }
                if (pause > TimeSpan.Zero) await Task.Delay(pause, ct);
                return new Turn();
            }
            catch
            {
                if (entered) _turnGate.Release();
                throw;
            }
            finally
            {
                // From here on the turn is held, not waited for; EndTurn releases it.
                Interlocked.Decrement(ref _waiting);
            }
        }

        private static void EndTurn(bool? keyWasRight)
        {
            lock (_sync)
            {
                if (keyWasRight == true)
                {
                    _failureStreak = 0;
                    _nextCheckUtc = DateTime.MinValue;
                }
                else if (keyWasRight == false)
                {
                    ForgetIfQuiet();
                    _failureStreak++;
                    _lastFailureUtc = DateTime.UtcNow;
                    _nextCheckUtc = _lastFailureUtc + DelayAfterFailures(_failureStreak);
                }
            }
            _turnGate.Release();
        }

        private static void ForgetIfQuiet()
        {
            if (_failureStreak > 0 && DateTime.UtcNow - _lastFailureUtc > QuietReset)
            {
                _failureStreak = 0;
                _nextCheckUtc = DateTime.MinValue;
            }
        }
    }

    /// <summary>
    /// The Host's access key. The old fixed default PIN 2580 faced the whole Internet once the tunnel worked,
    /// so each Windows user now gets a random key on first run, kept for later runs.
    /// </summary>
    public static class AccessKeyStore
    {
        public const string LegacyDefaultPin = "2580";
        public const int MinInternetKeyLength = 8;

        public static string KeyFilePath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurboRemote");
                return Path.Combine(dir, "access_key.txt");
            }
        }

        /// <summary>
        /// Returns the saved key, or makes and saves a new one. <paramref name="note"/> is null when a saved key
        /// was loaded, otherwise a log line saying what happened.
        /// </summary>
        public static string LoadOrCreate(out string note)
        {
            note = null;
            try
            {
                if (File.Exists(KeyFilePath))
                {
                    string saved = File.ReadAllText(KeyFilePath).Trim();
                    if (saved.Length > 0) return saved;
                }
            }
            catch { }

            string key = TurboProtocol.GenerateStrongAccessKey();
            note = Save(key)
                ? "[Security] First run: generated this PC's random access key " + key + " and saved it in " + KeyFilePath + "."
                : "[Security] Generated random access key " + key + " for this run, but could not save it in " + KeyFilePath + ".";
            return key;
        }

        public static bool Save(string key)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(KeyFilePath));
                File.WriteAllText(KeyFilePath, (key ?? "").Trim());
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Null when the key may face the Internet; otherwise why it may not.</summary>
        public static string WhyTooWeakForInternet(string key)
        {
            string k = (key ?? "").Trim();
            if (k.Length == 0) return "it is empty";
            if (k == LegacyDefaultPin) return "it is the old default PIN " + LegacyDefaultPin + ", which anyone can guess";
            string core = new string(k.Where(c => c != '-' && !char.IsWhiteSpace(c)).ToArray());
            if (core.Length < MinInternetKeyLength) return "it has fewer than " + MinInternetKeyLength + " characters";
            if (core.Distinct().Count() < 4) return "it uses fewer than 4 different characters";
            return null;
        }
    }

    public static class TurboProtocol
    {
        /// <summary>
        /// Shown in both windows and sent by the Host after sign-in, so a viewer can tell when the other PC runs an
        /// older TurboRemote. Builds before this one did not report a version. Bump it with every release.
        /// </summary>
        public const string AppVersion = "2026.11.23";

        private static readonly byte[] MagicHeader = new byte[] { 0x54, 0x52, 0x42, 0x4F }; // 'TRBO'

        public static string GenerateStrongAccessKey()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            byte[] rand = new byte[8];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(rand);
            }
            char[] outChars = new char[9];
            for (int i = 0; i < 4; i++) outChars[i] = chars[rand[i] % chars.Length];
            outChars[4] = '-';
            for (int i = 4; i < 8; i++) outChars[i + 1] = chars[rand[i] % chars.Length];
            return new string(outChars);
        }

        public static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= (a[i] ^ b[i]);
            }
            return diff == 0;
        }

        public static byte[] CompressFast(byte[] data)
        {
            if (data == null || data.Length == 0) return new byte[0];
            using (MemoryStream ms = new MemoryStream())
            {
                using (DeflateStream ds = new DeflateStream(ms, CompressionLevel.Fastest, true))
                {
                    ds.Write(data, 0, data.Length);
                }
                return ms.ToArray();
            }
        }

        public static byte[] DecompressFast(byte[] compressed)
        {
            if (compressed == null || compressed.Length == 0) return new byte[0];
            using (MemoryStream input = new MemoryStream(compressed))
            using (DeflateStream ds = new DeflateStream(input, CompressionMode.Decompress))
            using (MemoryStream output = new MemoryStream())
            {
                ds.CopyTo(output);
                return output.ToArray();
            }
        }

        public static async Task SendPacketAsync(Stream stream, SemaphoreSlim writeLock, PacketType type, byte[] payload, CancellationToken ct, SecurityContext sec = null)
        {
            if (payload == null) payload = new byte[0];

            await writeLock.WaitAsync(ct);
            try
            {
                // Encrypt while holding the write lock. EncryptAndAuthenticate assigns the anti-replay
                // sequence number, and the receiver rejects any packet whose number is not higher than the
                // last one. Encrypting before taking the lock let concurrent senders (screen frames, pongs,
                // shell output, key/mouse input) reach the wire out of order, and the receiver then dropped
                // the whole session as a "replay attack".
                byte[] wirePayload = (sec != null) ? sec.EncryptAndAuthenticate(type, payload) : payload;

                // Header and payload in one write, so a small input packet is one TCP segment, not two.
                byte[] frame = new byte[9 + wirePayload.Length];
                Buffer.BlockCopy(MagicHeader, 0, frame, 0, 4);
                frame[4] = (byte)type;
                Buffer.BlockCopy(BitConverter.GetBytes(wirePayload.Length), 0, frame, 5, 4);
                Buffer.BlockCopy(wirePayload, 0, frame, 9, wirePayload.Length);

                await stream.WriteAsync(frame, 0, frame.Length, ct);
                await stream.FlushAsync(ct);
            }
            finally
            {
                writeLock.Release();
            }
        }

        public static async Task<KeyValuePair<PacketType, byte[]>?> ReadPacketAsync(Stream stream, CancellationToken ct, SecurityContext sec = null)
        {
            byte[] header = new byte[9];
            int read = await ReadExactAsync(stream, header, 0, 9, ct);
            if (read < 9) return null;

            if (header[0] != MagicHeader[0] || header[1] != MagicHeader[1] ||
                header[2] != MagicHeader[2] || header[3] != MagicHeader[3])
            {
                throw new InvalidDataException("Invalid TurboRemote packet header.");
            }

            PacketType type = (PacketType)header[4];
            int length = BitConverter.ToInt32(header, 5);
            if (length < 0 || length > 64 * 1024 * 1024)
            {
                throw new InvalidDataException("Invalid packet length: " + length);
            }

            byte[] wirePayload = new byte[length];
            if (length > 0)
            {
                int payloadRead = await ReadExactAsync(stream, wirePayload, 0, length, ct);
                if (payloadRead < length) return null;
            }

            byte[] plainPayload = (sec != null) ? sec.VerifyAndDecrypt(type, wirePayload) : wirePayload;
            return new KeyValuePair<PacketType, byte[]>(type, plainPayload);
        }

        private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int n = await stream.ReadAsync(buffer, offset + total, count - total, ct);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }
    }

    public struct TilePatch
    {
        public ushort GridX;
        public ushort GridY;
        public ushort Width;
        public ushort Height;
        public byte[] JpegBytes;
    }

    public class DeltaScreenEncoder : IDisposable
    {
        public const int GridCols = 8;
        public const int GridRows = 8;

        private readonly ulong[,] _prevTileHashes = new ulong[GridCols, GridRows];
        private int _lastScaledWidth = 0;
        private int _lastScaledHeight = 0;
        private bool _lastGrayscale = false;
        private int _lastMonitorIndex = -1;
        private int _frameCounter = 0;

        private readonly ImageCodecInfo _jpegEncoder;
        private readonly EncoderParameters _encoderParams;
        private long _currentQuality = -1;

        private Bitmap _rawCaptureBmp;
        private Graphics _rawCaptureGfx;
        private Bitmap _scaledBmp;
        private Graphics _scaledGfx;

        public DeltaScreenEncoder()
        {
            foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders())
            {
                if (codec.FormatID == ImageFormat.Jpeg.Guid)
                {
                    _jpegEncoder = codec;
                    break;
                }
            }
            _encoderParams = new EncoderParameters(1);
        }

        public void ForceFullKeyFrame()
        {
            _lastScaledWidth = 0;
            _lastScaledHeight = 0;
        }

        private void EnsureQuality(long quality)
        {
            if (quality < 10L) quality = 10L;
            if (quality > 95L) quality = 95L;
            if (_currentQuality != quality)
            {
                _currentQuality = quality;
                _encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            }
        }

        private void EnsureBuffers(int nativeW, int nativeH, int scaledW, int scaledH)
        {
            if (_rawCaptureBmp == null || _rawCaptureBmp.Width != nativeW || _rawCaptureBmp.Height != nativeH)
            {
                if (_rawCaptureGfx != null) _rawCaptureGfx.Dispose();
                if (_rawCaptureBmp != null) _rawCaptureBmp.Dispose();
                _rawCaptureBmp = new Bitmap(nativeW, nativeH, PixelFormat.Format32bppRgb);
                _rawCaptureGfx = Graphics.FromImage(_rawCaptureBmp);
            }

            if (_scaledBmp == null || _scaledBmp.Width != scaledW || _scaledBmp.Height != scaledH)
            {
                if (_scaledGfx != null) _scaledGfx.Dispose();
                if (_scaledBmp != null) _scaledBmp.Dispose();
                _scaledBmp = new Bitmap(scaledW, scaledH, PixelFormat.Format24bppRgb);
                _scaledGfx = Graphics.FromImage(_scaledBmp);
                _scaledGfx.InterpolationMode = InterpolationMode.Bilinear;
                _scaledGfx.CompositingQuality = CompositingQuality.HighSpeed;
                _scaledGfx.SmoothingMode = SmoothingMode.None;
            }
        }

        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        private const int DESKTOPHORZRES = 118;
        private const int DESKTOPVERTRES = 117;

        /// <summary>
        /// Monitor bounds in this process's (DPI-virtualized) coordinate space. TurboRemote is DPI-unaware,
        /// so these are logical coordinates on a scaled display - the space SetCursorPos works in.
        /// </summary>
        public static Rectangle GetLogicalMonitorBounds(int monitorIndex)
        {
            System.Windows.Forms.Screen[] screens = System.Windows.Forms.Screen.AllScreens;
            if (screens.Length == 0) return new Rectangle(0, 0, 1920, 1080);

            int monIdx = monitorIndex;
            if (monIdx < 0) monIdx = 0;
            if (monIdx >= screens.Length) monIdx = screens.Length - 1;
            return screens[monIdx].Bounds;
        }

        /// <summary>
        /// Monitor bounds in physical pixels - the space screen capture (BitBlt) works in.
        /// </summary>
        public static Rectangle GetPhysicalMonitorBounds(int monitorIndex)
        {
            Rectangle logical = GetLogicalMonitorBounds(monitorIndex);

            try
            {
                IntPtr hdc = GetDC(IntPtr.Zero);
                if (hdc != IntPtr.Zero)
                {
                    int physW = GetDeviceCaps(hdc, DESKTOPHORZRES);
                    int physH = GetDeviceCaps(hdc, DESKTOPVERTRES);
                    ReleaseDC(IntPtr.Zero, hdc);
                    // DESKTOPHORZRES/VERTRES describe the PRIMARY monitor, so compare against the primary
                    // screen (Screen.AllScreens[0] is not guaranteed to be the primary one).
                    Rectangle primary = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                    if (physW > 0 && physH > 0 && primary.Width > 0 && primary.Height > 0)
                    {
                        float scaleX = (float)physW / (float)primary.Width;
                        float scaleY = (float)physH / (float)primary.Height;
                        if (scaleX > 1.01f || scaleY > 1.01f)
                        {
                            return new Rectangle(
                                (int)Math.Round(logical.X * scaleX),
                                (int)Math.Round(logical.Y * scaleY),
                                (int)Math.Round(logical.Width * scaleX),
                                (int)Math.Round(logical.Height * scaleY));
                        }
                    }
                }
            }
            catch { }

            return logical;
        }

        public unsafe byte[] CaptureAndEncodeDelta(StreamConfig config, out int changedTilesCount, out int nativeWidth, out int nativeHeight)
        {
            changedTilesCount = 0;
            System.Windows.Forms.Screen[] screens = System.Windows.Forms.Screen.AllScreens;
            if (screens.Length == 0)
            {
                nativeWidth = 1280;
                nativeHeight = 720;
                return new byte[0];
            }

            int monIdx = config.MonitorIndex;
            if (monIdx < 0) monIdx = 0;
            if (monIdx >= screens.Length) monIdx = screens.Length - 1;

            Rectangle bounds = GetPhysicalMonitorBounds(monIdx);
            nativeWidth = bounds.Width;
            nativeHeight = bounds.Height;

            int scaledW = Math.Max(320, ((int)(nativeWidth * config.Scale)) & ~7);
            int scaledH = Math.Max(240, ((int)(nativeHeight * config.Scale)) & ~7);

            bool forceFull = (_lastScaledWidth != scaledW ||
                              _lastScaledHeight != scaledH ||
                              _lastGrayscale != config.Grayscale ||
                              _lastMonitorIndex != monIdx ||
                              (_frameCounter % 180 == 0));

            _lastScaledWidth = scaledW;
            _lastScaledHeight = scaledH;
            _lastGrayscale = config.Grayscale;
            _lastMonitorIndex = monIdx;
            _frameCounter++;

            EnsureQuality(config.JpegQuality);
            EnsureBuffers(nativeWidth, nativeHeight, scaledW, scaledH);

            _rawCaptureGfx.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            _scaledGfx.DrawImage(_rawCaptureBmp, 0, 0, scaledW, scaledH);

            int tileW = scaledW / GridCols;
            int tileH = scaledH / GridRows;

            BitmapData bmpData = _scaledBmp.LockBits(new Rectangle(0, 0, scaledW, scaledH), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            ulong[,] newHashes = new ulong[GridCols, GridRows];
            bool[,] isDirty = new bool[GridCols, GridRows];
            List<TilePatch> dirtyPatches = new List<TilePatch>(GridCols * GridRows);

            try
            {
                byte* basePtr = (byte*)bmpData.Scan0;
                int stride = bmpData.Stride;

                if (config.Grayscale || config.LowBitPosterize)
                {
                    byte mask = config.LowBitPosterize ? (byte)0xE0 : (byte)0xF8;
                    for (int y = 0; y < scaledH; y++)
                    {
                        byte* row = basePtr + (y * stride);
                        for (int x = 0; x < scaledW * 3; x += 3)
                        {
                            if (config.Grayscale)
                            {
                                byte lum = (byte)(((row[x + 2] * 77) + (row[x + 1] * 150) + (row[x] * 29)) >> 8);
                                lum &= mask;
                                row[x] = lum;
                                row[x + 1] = lum;
                                row[x + 2] = lum;
                            }
                            else
                            {
                                row[x] &= mask;
                                row[x + 1] &= mask;
                                row[x + 2] &= mask;
                            }
                        }
                    }
                }

                for (int gy = 0; gy < GridRows; gy++)
                {
                    int startY = gy * tileH;
                    int h = (gy == GridRows - 1) ? (scaledH - startY) : tileH;

                    for (int gx = 0; gx < GridCols; gx++)
                    {
                        int startX = gx * tileW;
                        int w = (gx == GridCols - 1) ? (scaledW - startX) : tileW;

                        ulong hash = HashTile(basePtr, stride, startX, startY, w, h);

                        newHashes[gx, gy] = hash;
                        if (forceFull || _prevTileHashes[gx, gy] != hash)
                        {
                            isDirty[gx, gy] = true;
                            byte* tilePtr = basePtr + (startY * stride) + (startX * 3);
                            using (Bitmap zeroCopyTile = new Bitmap(w, h, stride, PixelFormat.Format24bppRgb, (IntPtr)tilePtr))
                            using (MemoryStream tileMs = new MemoryStream(4096))
                            {
                                zeroCopyTile.Save(tileMs, _jpegEncoder, _encoderParams);
                                dirtyPatches.Add(new TilePatch
                                {
                                    GridX = (ushort)startX,
                                    GridY = (ushort)startY,
                                    Width = (ushort)w,
                                    Height = (ushort)h,
                                    JpegBytes = tileMs.ToArray()
                                });
                            }
                            _prevTileHashes[gx, gy] = hash;
                        }
                    }
                }
            }
            finally
            {
                _scaledBmp.UnlockBits(bmpData);
            }

            changedTilesCount = dirtyPatches.Count;
            if (changedTilesCount == 0)
            {
                return new byte[0];
            }

            using (MemoryStream packetMs = new MemoryStream(16384))
            using (BinaryWriter bw = new BinaryWriter(packetMs))
            {
                bw.Write((ushort)nativeWidth);
                bw.Write((ushort)nativeHeight);
                bw.Write((ushort)scaledW);
                bw.Write((ushort)scaledH);
                bw.Write((byte)(forceFull ? 1 : 0));
                bw.Write((ushort)dirtyPatches.Count);

                foreach (TilePatch patch in dirtyPatches)
                {
                    bw.Write(patch.GridX);
                    bw.Write(patch.GridY);
                    bw.Write(patch.Width);
                    bw.Write(patch.Height);
                    bw.Write(patch.JpegBytes.Length);
                    bw.Write(patch.JpegBytes);
                }

                bw.Flush();
                return TurboProtocol.CompressFast(packetMs.ToArray());
            }
        }

        /// <summary>
        /// FNV-style hash over EVERY byte of a 24bpp tile, read 8 bytes at a time. The previous version only
        /// sampled every other row and every other pixel with the low 3 bits masked off, so 1-pixel changes
        /// (text caret, thin glyphs like "l" or ".", focus rectangles, subtle hover colours) never marked the
        /// tile dirty and only showed up at the next periodic keyframe, seconds later.
        /// </summary>
        private static unsafe ulong HashTile(byte* basePtr, int stride, int startX, int startY, int w, int h)
        {
            ulong hash = 14695981039346656037UL;
            int rowBytes = w * 3;
            int wordCount = rowBytes >> 3;
            int tailStart = wordCount << 3;
            for (int y = startY; y < startY + h; y++)
            {
                byte* row = basePtr + (y * stride) + (startX * 3);
                ulong* words = (ulong*)row;
                for (int i = 0; i < wordCount; i++)
                {
                    hash ^= words[i];
                    hash *= 1099511628211UL;
                }
                for (int i = tailStart; i < rowBytes; i++)
                {
                    hash ^= row[i];
                    hash *= 1099511628211UL;
                }
            }
            return hash;
        }

        public static void ApplyDeltaFrame(byte[] compressedPayload, ref Bitmap canvasBitmap, out int nativeWidth, out int nativeHeight, out int scaledWidth, out int scaledHeight, out int updatedTiles)
        {
            byte[] raw = TurboProtocol.DecompressFast(compressedPayload);
            using (MemoryStream ms = new MemoryStream(raw))
            using (BinaryReader br = new BinaryReader(ms))
            {
                nativeWidth = br.ReadUInt16();
                nativeHeight = br.ReadUInt16();
                scaledWidth = br.ReadUInt16();
                scaledHeight = br.ReadUInt16();
                byte isKeyFrame = br.ReadByte();
                updatedTiles = br.ReadUInt16();

                if (canvasBitmap == null || canvasBitmap.Width != scaledWidth || canvasBitmap.Height != scaledHeight)
                {
                    if (canvasBitmap != null) canvasBitmap.Dispose();
                    canvasBitmap = new Bitmap(scaledWidth, scaledHeight, PixelFormat.Format24bppRgb);
                }

                using (Graphics g = Graphics.FromImage(canvasBitmap))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    for (int i = 0; i < updatedTiles; i++)
                    {
                        int x = br.ReadUInt16();
                        int y = br.ReadUInt16();
                        int w = br.ReadUInt16();
                        int h = br.ReadUInt16();
                        int len = br.ReadInt32();
                        byte[] jpeg = br.ReadBytes(len);

                        using (MemoryStream tileStream = new MemoryStream(jpeg))
                        using (Image tileImg = Image.FromStream(tileStream))
                        {
                            g.DrawImage(tileImg, x, y, w, h);
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_encoderParams != null) _encoderParams.Dispose();
            if (_rawCaptureGfx != null) _rawCaptureGfx.Dispose();
            if (_rawCaptureBmp != null) _rawCaptureBmp.Dispose();
            if (_scaledGfx != null) _scaledGfx.Dispose();
            if (_scaledBmp != null) _scaledBmp.Dispose();
        }
    }

    public static class NativeInputInjector
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        /// <summary>
        /// Stamped on every key the Host injects. A viewer running on the same PC (e.g. "Launch Both") ignores
        /// keys carrying it; otherwise each injected key was captured and sent back to the Host again, forever.
        /// </summary>
        public static readonly UIntPtr InjectedInputMarker = (UIntPtr)0x54524D54u;

        public static bool IsInjectedByTurboRemote(IntPtr extraInfo)
        {
            return unchecked((ulong)extraInfo.ToInt64()) == InjectedInputMarker.ToUInt64();
        }

        // Private on purpose: only call it through the input thread (see PostToInputThread).
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool BlockInput(bool fBlockIt);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint SetThreadExecutionState(uint esFlags);

        public const uint ES_CONTINUOUS = 0x80000000;
        public const uint ES_SYSTEM_REQUIRED = 0x00000001;
        public const uint ES_DISPLAY_REQUIRED = 0x00000002;

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;

        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        // Every BlockInput call and every injected mouse/key event runs on ONE dedicated thread, in order.
        // Windows only lets the thread that called BlockInput(true) unblock input again, but session packets
        // are handled on arbitrary thread-pool threads, so "Unlock Input" (and the unlock when a viewer
        // disconnects) could silently fail and leave the host's physical keyboard and mouse locked.
        private static volatile BlockingCollection<Action> _inputQueue;
        private static readonly object _inputQueueInitLock = new object();

        private static void PostToInputThread(Action action)
        {
            BlockingCollection<Action> queue = _inputQueue;
            if (queue == null)
            {
                lock (_inputQueueInitLock)
                {
                    queue = _inputQueue;
                    if (queue == null)
                    {
                        BlockingCollection<Action> newQueue = new BlockingCollection<Action>();
                        Thread worker = new Thread(() =>
                        {
                            foreach (Action work in newQueue.GetConsumingEnumerable())
                            {
                                try { work(); } catch { }
                            }
                        });
                        worker.IsBackground = true;
                        worker.Name = "TurboRemote Input Injector";
                        worker.Start();
                        _inputQueue = newQueue;
                        queue = newQueue;
                    }
                }
            }
            queue.Add(action);
        }

        public static void PreventSystemSleep()
        {
            try
            {
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
            }
            catch { }
        }

        public static bool IsRunningAsAdmin()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool RestartAsAdmin(string arguments)
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool InstallUnattendedAdminStartupTask(string port, string pin)
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                string args = string.Format("/Create /F /RL HIGHEST /SC ONLOGON /TN \"TurboRemote_Unattended_Host\" /TR \"\\\"{0}\\\" --port {1} --pin {2}\"", exePath, port, pin);
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(4000);
                    return p.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Queues a mouse event; <paramref name="onInjected"/> runs right after it was injected.</summary>
        public static void InjectMouse(MouseActionType action, float normX, float normY, int wheelDelta, int monitorIndex, Action onInjected = null)
        {
            PostToInputThread(() =>
            {
                InjectMouseNow(action, normX, normY, wheelDelta, monitorIndex);
                if (onInjected != null) onInjected();
            });
        }

        private static void InjectMouseNow(MouseActionType action, float normX, float normY, int wheelDelta, int monitorIndex)
        {
            try
            {
                // TurboRemote runs DPI-unaware, so SetCursorPos takes LOGICAL (DPI-virtualized) coordinates even
                // though frames are captured in physical pixels. Using the physical bounds here put every click
                // 1.25x / 1.5x too far right and down on a scaled display (and pinned it to the screen edge).
                Rectangle bounds = DeltaScreenEncoder.GetLogicalMonitorBounds(monitorIndex);

                if (float.IsNaN(normX) || normX < 0f) normX = 0f;
                if (normX > 1f) normX = 1f;
                if (float.IsNaN(normY) || normY < 0f) normY = 0f;
                if (normY > 1f) normY = 1f;

                // Clamp to the last pixel so x = 1.0 cannot land on the first column of the next monitor.
                int targetX = bounds.X + Math.Min(bounds.Width - 1, (int)Math.Round(normX * bounds.Width));
                int targetY = bounds.Y + Math.Min(bounds.Height - 1, (int)Math.Round(normY * bounds.Height));

                SetCursorPos(targetX, targetY);

                switch (action)
                {
                    case MouseActionType.LeftDown:
                        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                        break;
                    case MouseActionType.LeftUp:
                        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                        break;
                    case MouseActionType.RightDown:
                        mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
                        break;
                    case MouseActionType.RightUp:
                        mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
                        break;
                    case MouseActionType.MiddleDown:
                        mouse_event(MOUSEEVENTF_MIDDLEDOWN, 0, 0, 0, UIntPtr.Zero);
                        break;
                    case MouseActionType.MiddleUp:
                        mouse_event(MOUSEEVENTF_MIDDLEUP, 0, 0, 0, UIntPtr.Zero);
                        break;
                    case MouseActionType.Wheel:
                        mouse_event(MOUSEEVENTF_WHEEL, 0, 0, wheelDelta, UIntPtr.Zero);
                        break;
                }
            }
            catch { }
        }

        /// <summary>
        /// Queues a key event; <paramref name="onInjected"/> runs right after it was injected.
        /// <paramref name="scanCode"/> 0 and <paramref name="extended"/> null mean "not sent by the viewer"
        /// (older viewers send only the virtual key); both are then derived from the virtual key.
        /// </summary>
        public static void InjectKey(ushort virtualKeyCode, bool isKeyDown, byte scanCode, bool? extended, Action onInjected = null)
        {
            PostToInputThread(() =>
            {
                InjectKeyNow(virtualKeyCode, isKeyDown, scanCode, extended);
                if (onInjected != null) onInjected();
            });
        }

        private const byte VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
        private const byte VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
        private const byte VK_CAPITAL = 0x14, VK_NUMLOCK = 0x90, VK_SCROLL = 0x91;

        private static void InjectKeyNow(ushort virtualKeyCode, bool isKeyDown, byte scanCode = 0, bool? extended = null)
        {
            try
            {
                byte vk = (byte)(virtualKeyCode & 0xFF);
                bool isExtended = extended ?? IsExtendedKey(vk);

                // A viewer's window messages only say "Shift/Ctrl/Alt"; press the side it was pressed on, so
                // Right Alt stays AltGr and Right Ctrl stays Right Ctrl on the remote PC.
                switch (vk)
                {
                    case VK_SHIFT: vk = scanCode == 0x36 ? VK_RSHIFT : VK_LSHIFT; break;
                    case VK_CONTROL: vk = isExtended ? VK_RCONTROL : VK_LCONTROL; break;
                    case VK_MENU: vk = isExtended ? VK_RMENU : VK_LMENU; break;
                }

                // Scan code 0 left programs that read scan codes (games, terminals, some Java/Electron apps)
                // with no key at all.
                if (scanCode == 0) scanCode = (byte)MapVirtualKey(vk, 0 /* MAPVK_VK_TO_VSC */);

                uint flags = isKeyDown ? 0u : KEYEVENTF_KEYUP;
                if (isExtended) flags |= KEYEVENTF_EXTENDEDKEY;
                keybd_event(vk, scanCode, flags, InjectedInputMarker);
            }
            catch { }
        }

        private static bool IsExtendedKey(byte vk)
        {
            return vk == 0x21 || vk == 0x22 || vk == 0x23 || vk == 0x24 ||   // PgUp PgDn End Home
                   vk == 0x25 || vk == 0x26 || vk == 0x27 || vk == 0x28 ||   // arrows
                   vk == 0x2D || vk == 0x2E || vk == 0x5B || vk == 0x5C ||   // Ins Del LWin RWin
                   vk == 0x03 || vk == 0x2C || vk == 0x5D || vk == 0x6F ||   // Break PrtSc Apps Num/
                   vk == VK_NUMLOCK || vk == VK_RCONTROL || vk == VK_RMENU ||
                   (vk >= 0xA6 && vk <= 0xB7);                                // browser, media and launch keys
        }

        /// <summary>Makes Caps/Num/Scroll Lock on this PC match the viewer's, toggling only what differs.</summary>
        public static void SyncLockKeys(bool capsOn, bool numOn, bool scrollOn)
        {
            PostToInputThread(() =>
            {
                SyncOneLockKey(VK_CAPITAL, capsOn);
                SyncOneLockKey(VK_NUMLOCK, numOn);
                SyncOneLockKey(VK_SCROLL, scrollOn);
            });
        }

        private static void SyncOneLockKey(byte vk, bool wantOn)
        {
            bool isOn = (GetKeyState(vk) & 1) != 0;
            if (isOn == wantOn) return;
            InjectKeyNow(vk, true);
            InjectKeyNow(vk, false);
        }

        public static void SendSpecialCombo(string comboName)
        {
            PostToInputThread(() => SendSpecialComboNow(comboName));
        }

        private static void SendSpecialComboNow(string comboName)
        {
            switch (comboName)
            {
                case "WIN_D":
                    InjectKeyNow(0x5B, true);
                    InjectKeyNow(0x44, true);
                    InjectKeyNow(0x44, false);
                    InjectKeyNow(0x5B, false);
                    break;
                case "ALT_TAB":
                    InjectKeyNow(0x12, true);
                    InjectKeyNow(0x09, true);
                    InjectKeyNow(0x09, false);
                    InjectKeyNow(0x12, false);
                    break;
                case "TASK_MGR":
                    InjectKeyNow(0x11, true);
                    InjectKeyNow(0x10, true);
                    InjectKeyNow(0x1B, true);
                    InjectKeyNow(0x1B, false);
                    InjectKeyNow(0x10, false);
                    InjectKeyNow(0x11, false);
                    break;
                case "ALT_F4":
                    InjectKeyNow(0x12, true);
                    InjectKeyNow(0x73, true);
                    InjectKeyNow(0x73, false);
                    InjectKeyNow(0x12, false);
                    break;
                case "WIN_R":
                    InjectKeyNow(0x5B, true);
                    InjectKeyNow(0x52, true);
                    InjectKeyNow(0x52, false);
                    InjectKeyNow(0x5B, false);
                    break;
                case "LOCK_INPUT_ON":
                    BlockInput(true);
                    break;
                case "LOCK_INPUT_OFF":
                    BlockInput(false);
                    break;
            }
        }
    }

    public class EndpointCandidate
    {
        public string Host;
        public int Port;
        public string RouteLabel; // "LAN", "P2P/Tailscale", "WAN/Internet"
    }

    public static class SmartRouteEngine
    {
        public const int DiscoveryUdpPort = 9878;
        private const int CloudLookupTimeoutMs = 8000;

        private const int CloudKdfIterations = 100000;
        private const string TicketPrefix = "TRBO_TICKET_V2:";

        /// <summary>
        /// Derives the ntfy topic URL and the ticket encryption secret from Host ID + access key. V1 used one
        /// plain SHA-256 for the topic, so whoever saw the topic name (ntfy.sh itself, a log) could test billions
        /// of keys a second offline. PBKDF2 salted with the Host ID makes every guess cost 100,000 rounds.
        /// </summary>
        public static void DeriveCloudDirectory(string roomCode, string pin, out string topicUrl, out string ticketSecret)
        {
            byte[] salt = Encoding.UTF8.GetBytes("TURBOREMOTE_CLOUD_DIRECTORY_V2::" + roomCode.Trim().ToUpperInvariant());
            using (Rfc2898DeriveBytes kdf = new Rfc2898DeriveBytes(pin.Trim(), salt, CloudKdfIterations, HashAlgorithmName.SHA256))
            {
                byte[] block = kdf.GetBytes(32);
                // The topic is public to ntfy.sh; the other half never leaves this PC.
                topicUrl = "https://ntfy.sh/trbo2_" + ToHex(block, 0, 16);
                ticketSecret = ToHex(block, 16, 16);
            }
        }

        private static string ToHex(byte[] bytes, int offset, int count)
        {
            StringBuilder hex = new StringBuilder(count * 2);
            for (int i = offset; i < offset + count; i++) hex.Append(bytes[i].ToString("x2"));
            return hex.ToString();
        }

        public static string EncryptTicketForCloud(string plainTicket, string ticketSecret)
        {
            byte[] salt = Encoding.UTF8.GetBytes("TRBO_CLOUD_SALT!");
            byte[] chal = Encoding.UTF8.GetBytes("TRBO_CLOUD_CHAL!");
            using (SecurityContext sec = new SecurityContext(ticketSecret, salt, chal))
            {
                byte[] enc = sec.EncryptAndAuthenticate(PacketType.AuthChallenge, Encoding.UTF8.GetBytes(plainTicket));
                return TicketPrefix + Convert.ToBase64String(enc);
            }
        }

        public static string DecryptTicketFromCloud(string line, string ticketSecret)
        {
            int idx = line.IndexOf(TicketPrefix);
            if (idx < 0) return null;
            string b64 = line.Substring(idx + TicketPrefix.Length).Trim();
            int quoteIdx = b64.IndexOf('"');
            if (quoteIdx > 0) b64 = b64.Substring(0, quoteIdx);

            byte[] env = Convert.FromBase64String(b64);
            byte[] salt = Encoding.UTF8.GetBytes("TRBO_CLOUD_SALT!");
            byte[] chal = Encoding.UTF8.GetBytes("TRBO_CLOUD_CHAL!");
            using (SecurityContext sec = new SecurityContext(ticketSecret, salt, chal))
            {
                byte[] plain = sec.VerifyAndDecrypt(PacketType.AuthChallenge, env);
                return Encoding.UTF8.GetString(plain);
            }
        }

        /// <summary>Returns null on success, otherwise why the publish failed.</summary>
        public static async Task<string> PublishHostTicketToCloudAsync(string roomCode, string pin, string ticket)
        {
            try
            {
                // Callers already refuse weak keys; this is the last line before the endpoints leave the PC.
                string weakness = AccessKeyStore.WhyTooWeakForInternet(pin);
                if (weakness != null) return "the access key is too weak to publish (" + weakness + ")";

                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                string url, ticketSecret;
                DeriveCloudDirectory(roomCode, pin, out url, out ticketSecret);
                string encryptedPayload = EncryptTicketForCloud(ticket, ticketSecret);

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.Timeout = 10000;
                byte[] data = Encoding.UTF8.GetBytes(encryptedPayload);
                req.ContentLength = data.Length;
                using (Stream s = await req.GetRequestStreamAsync())
                {
                    await s.WriteAsync(data, 0, data.Length);
                }
                using (WebResponse resp = await req.GetResponseAsync())
                {
                    return null;
                }
            }
            catch (WebException wex)
            {
                HttpWebResponse httpResp = wex.Response as HttpWebResponse;
                if (httpResp != null && (int)httpResp.StatusCode == 429)
                    return "ntfy.sh daily message limit reached for this public IP (HTTP 429)";
                return wex.Message;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        public static Task<string> ResolveHostTicketFromCloudAsync(string roomCode, string pin)
        {
            return Task.Run(() =>
            {
                try
                {
                    // A Host never publishes under a weak key, so there is nothing to find.
                    if (AccessKeyStore.WhyTooWeakForInternet(pin) != null) return (string)null;

                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    string topicUrl, ticketSecret;
                    DeriveCloudDirectory(roomCode, pin, out topicUrl, out ticketSecret);
                    string url = topicUrl + "/raw?poll=1&since=12h";

                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "GET";
                    // DNS + TCP + TLS + the poll took ~0.7 s even on a fast link; 2 s was too tight for slow ones.
                    req.Timeout = CloudLookupTimeoutMs;
                    req.ReadWriteTimeout = CloudLookupTimeoutMs;
                    using (WebResponse resp = req.GetResponse())
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                    {
                        string all = sr.ReadToEnd();
                        string[] lines = all.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        for (int i = lines.Length - 1; i >= 0; i--)
                        {
                            try
                            {
                                string decrypted = DecryptTicketFromCloud(lines[i], ticketSecret);
                                if (!string.IsNullOrWhiteSpace(decrypted))
                                {
                                    return decrypted;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
                return (string)null;
            });
        }

        public static List<string> GetRawHostIps()
        {
            List<string> rawIps = new List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    IPInterfaceProperties props = ni.GetIPProperties();
                    foreach (UnicastIPAddressInformation addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            rawIps.Add(addr.Address.ToString());
                        }
                    }
                }
            }
            catch { }
            return rawIps;
        }

        public static string BuildSmartConnectTicket(int tcpPort, string publicTunnelHostPort = "")
        {
            List<string> endpoints = new List<string>();
            foreach (string ip in GetRawHostIps())
            {
                endpoints.Add(ip + ":" + tcpPort);
            }
            if (!string.IsNullOrWhiteSpace(publicTunnelHostPort))
            {
                endpoints.Add(publicTunnelHostPort.Trim());
            }
            if (endpoints.Count == 0)
            {
                endpoints.Add("127.0.0.1:" + tcpPort);
            }
            return string.Join(",", endpoints);
        }

        private const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);

        public static void StartLanBeaconResponder(Func<int> getTcpPort, CancellationToken ct)
        {
            Task.Run(async () =>
            {
                try
                {
                    using (UdpClient udp = new UdpClient(DiscoveryUdpPort))
                    // ReceiveAsync ignores the token, so close the socket on cancel. Otherwise a stopped server
                    // kept answering discovery probes (with its old port) and held UDP 9878, so the beacon of
                    // the next "Start Host Server" could not bind and LAN AUTO discovery stopped working.
                    using (ct.Register(() => { try { udp.Close(); } catch { } }))
                    {
                        // Windows reports an ICMP "port unreachable" (from a viewer that already stopped
                        // listening) as an error on the NEXT receive, which used to kill the responder for good.
                        try { udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); } catch { }

                        while (!ct.IsCancellationRequested)
                        {
                            UdpReceiveResult res;
                            try
                            {
                                res = await udp.ReceiveAsync();
                            }
                            catch (SocketException)
                            {
                                res = default(UdpReceiveResult); // transient per-datagram error
                            }
                            if (res.Buffer == null)
                            {
                                await Task.Delay(50, ct); // back off; throws (and exits) once cancelled
                                continue;
                            }

                            string msg = Encoding.UTF8.GetString(res.Buffer);
                            if (msg == "TRBO_DISCOVER")
                            {
                                string reply = "TRBO_HOST|" + Environment.MachineName + "|" + getTcpPort();
                                byte[] replyBytes = Encoding.UTF8.GetBytes(reply);
                                try
                                {
                                    await udp.SendAsync(replyBytes, replyBytes.Length, res.RemoteEndPoint);
                                }
                                catch (SocketException) { }
                            }
                        }
                    }
                }
                catch { } // port 9878 already in use, or socket closed because the server stopped
            }, ct);
        }

        public static async Task<List<EndpointCandidate>> DiscoverLanHostsAsync(int timeoutMs = 450)
        {
            List<EndpointCandidate> found = new List<EndpointCandidate>();
            try
            {
                using (UdpClient udp = new UdpClient())
                {
                    udp.EnableBroadcast = true;
                    byte[] probe = Encoding.UTF8.GetBytes("TRBO_DISCOVER");
                    await udp.SendAsync(probe, probe.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryUdpPort));

                    Stopwatch sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < timeoutMs)
                    {
                        Task<UdpReceiveResult> recvTask = udp.ReceiveAsync();
                        Task done = await Task.WhenAny(recvTask, Task.Delay(Math.Max(50, timeoutMs - (int)sw.ElapsedMilliseconds)));
                        if (done != recvTask) break;

                        string text = Encoding.UTF8.GetString(recvTask.Result.Buffer);
                        if (text.StartsWith("TRBO_HOST|"))
                        {
                            string[] parts = text.Split('|');
                            int port;
                            // One malformed reply must not abort discovery of the other hosts.
                            if (parts.Length < 3 || !int.TryParse(parts[2], out port)) port = 9876;
                            string ip = recvTask.Result.RemoteEndPoint.Address.ToString();
                            found.Add(new EndpointCandidate
                            {
                                Host = ip,
                                Port = port,
                                RouteLabel = "Auto-Discovered Local LAN (" + parts[1] + ")"
                            });
                        }
                    }
                }
            }
            catch { }
            return found;
        }

        public static List<EndpointCandidate> ParseCandidates(string hostInput, int defaultPort)
        {
            List<EndpointCandidate> list = new List<EndpointCandidate>();
            if (string.IsNullOrWhiteSpace(hostInput)) return list;

            string[] tokens = hostInput.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawToken in tokens)
            {
                string clean = rawToken.Trim().Replace("tcp://", "").Replace("http://", "");
                if (clean.Equals("AUTO", StringComparison.OrdinalIgnoreCase)) continue;

                string host = clean;
                int port = defaultPort;
                int colonIdx = clean.LastIndexOf(':');
                if (colonIdx > 0 && colonIdx < clean.Length - 1)
                {
                    int parsedPort;
                    if (int.TryParse(clean.Substring(colonIdx + 1), out parsedPort))
                    {
                        host = clean.Substring(0, colonIdx);
                        port = parsedPort;
                    }
                }

                string label = ClassifyRoute(host);
                list.Add(new EndpointCandidate { Host = host, Port = port, RouteLabel = label });
            }
            return list;
        }

        private static string ClassifyRoute(string host)
        {
            if (host == "127.0.0.1" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return "Localhost Loopback";
            if (host.StartsWith("192.168.") || host.StartsWith("10.") || host.StartsWith("172."))
                return "Direct Local LAN";
            if (TailscaleInfo.IsTailscaleAddress(host))
                return "Tailscale (permanent)";
            if (host.StartsWith("100."))
                return "Direct P2P Tailscale/WireGuard WAN";
            return "Public Internet / WAN Tunnel";
        }

        /// <summary>The free pinggy tunnel: reachable from anywhere, but cut and given a new address every hour.</summary>
        public static bool IsPublicTunnelHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            string h = host.ToLowerInvariant();
            return h.EndsWith(".pinggy.link") || h.EndsWith(".pinggy-free.link") || h.EndsWith(".pinggy.io");
        }

        private static async Task<TcpClient> DialTcpAsync(EndpointCandidate c, CancellationToken token)
        {
            TcpClient tcp = new TcpClient();
            tcp.NoDelay = true;
            try
            {
                using (token.Register(() => { try { tcp.Close(); } catch { } }))
                {
                    await tcp.ConnectAsync(c.Host, c.Port);
                }
                if (token.IsCancellationRequested || !tcp.Connected) throw new OperationCanceledException(token);
                return tcp;
            }
            catch
            {
                try { tcp.Close(); } catch { }
                throw;
            }
        }

        private static bool LooksLikeHostId(string token)
        {
            return !string.IsNullOrEmpty(token) && !token.Equals("AUTO", StringComparison.OrdinalIgnoreCase) &&
                   !token.Contains(".") && !token.Contains(":");
        }

        /// <summary>
        /// Connects to the Host by whatever the user typed (Host ID, IP, address, or a comma-separated list).
        /// LAN and Tailscale routes are dialled first. The hourly public tunnel only joins after 2.5 s without a
        /// direct connection: it used to win the race (its edge answers fast) and the session then died with
        /// the tunnel's hourly cut.
        /// </summary>
        public static async Task<KeyValuePair<TcpClient, EndpointCandidate>> RaceFastestConnectionAsync(string hostInput, int defaultPort, string pin, CancellationToken ct)
        {
            string rawInput = hostInput != null ? hostInput.Trim() : "";
            bool isAuto = string.IsNullOrEmpty(rawInput) || rawInput.Equals("AUTO", StringComparison.OrdinalIgnoreCase);
            ConnectionRace race = new ConnectionRace(DialTcpAsync);

            // 1. Whatever the user typed is dialled at once, even a tunnel address they chose on purpose.
            List<string> hostIds = new List<string>();
            if (!isAuto)
            {
                foreach (string token in rawInput.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string clean = token.Trim().TrimStart('@');
                    if (LooksLikeHostId(clean) && !hostIds.Contains(clean, StringComparer.OrdinalIgnoreCase)) hostIds.Add(clean);
                    foreach (EndpointCandidate c in ParseCandidates(clean, defaultPort)) race.AddDirect(c);
                }
            }

            // 2. LAN broadcast discovery. With Host IDs typed, only a LAN Host of that name is taken: any other
            //    TurboRemote PC on the LAN could otherwise answer first.
            Task lanTask = DiscoverLanHostsAsync(300).ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion) return;
                foreach (EndpointCandidate lan in t.Result)
                {
                    bool wanted = isAuto || hostIds.Any(id => lan.RouteLabel.IndexOf("(" + id + ")", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (wanted) race.AddDirect(lan);
                }
            }, TaskScheduler.Default);

            // 3. Cloud directory lookup for each Host ID: its LAN/Tailscale addresses race at once, its tunnel
            //    address is held back.
            List<Task> lookups = new List<Task> { lanTask };
            foreach (string id in hostIds)
            {
                lookups.Add(ResolveHostTicketFromCloudAsync(id, pin).ContinueWith(t =>
                {
                    if (t.Status != TaskStatus.RanToCompletion || string.IsNullOrWhiteSpace(t.Result)) return;
                    foreach (EndpointCandidate cc in ParseCandidates(t.Result, defaultPort))
                    {
                        if (IsPublicTunnelHost(cc.Host)) race.AddTunnel(cc);
                        else race.AddDirect(cc);
                    }
                }, TaskScheduler.Default));
            }

            Task lookupsDone = Task.WhenAll(lookups).ContinueWith(t =>
            {
                if (race.CandidateCount == 0)
                {
                    race.AddDirect(new EndpointCandidate { Host = "127.0.0.1", Port = defaultPort, RouteLabel = "Localhost" });
                }
                race.NoMoreCandidates();
            }, TaskScheduler.Default);

            try
            {
                return await race.RunAsync(ct);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("Timed out connecting to " + rawInput + ":" + defaultPort + ". Make sure TurboRemote Host is running.");
            }
        }
    }

    /// <summary>
    /// Races connection attempts: direct candidates (LAN, Tailscale, what the user typed) start at once; public
    /// tunnel candidates start only after <c>tunnelHoldBackMs</c> without a winner. Every attempt gets its own
    /// window, so a held-back tunnel is not starved on slow links. Candidates may be added while it runs.
    /// </summary>
    public sealed class ConnectionRace
    {
        private readonly Func<EndpointCandidate, CancellationToken, Task<TcpClient>> _dial;
        private readonly int _holdBackMs, _windowMs, _overallMs;
        private readonly object _lock = new object();
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<EndpointCandidate> _pendingDirect = new List<EndpointCandidate>();
        private readonly List<EndpointCandidate> _heldTunnels = new List<EndpointCandidate>();
        private readonly TaskCompletionSource<KeyValuePair<TcpClient, EndpointCandidate>> _result =
            new TaskCompletionSource<KeyValuePair<TcpClient, EndpointCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource _raceCts;
        private bool _running, _holdBackOver, _noMore, _done;
        private int _inFlight;
        private Exception _lastError;

        public ConnectionRace(Func<EndpointCandidate, CancellationToken, Task<TcpClient>> dial,
                              int tunnelHoldBackMs = 2500, int candidateWindowMs = 6000, int overallTimeoutMs = 15000)
        {
            _dial = dial;
            _holdBackMs = tunnelHoldBackMs;
            _windowMs = candidateWindowMs;
            _overallMs = overallTimeoutMs;
        }

        public int CandidateCount { get { lock (_lock) { return _seen.Count; } } }

        public void AddDirect(EndpointCandidate c)
        {
            lock (_lock)
            {
                if (!IsNew(c)) return;
                if (_running) StartDial(c);
                else _pendingDirect.Add(c);
            }
        }

        public void AddTunnel(EndpointCandidate c)
        {
            lock (_lock)
            {
                if (!IsNew(c)) return;
                if (_running && _holdBackOver) StartDial(c);
                else _heldTunnels.Add(c);
            }
        }

        /// <summary>From now on the race fails as soon as every attempt has failed, instead of at the overall timeout.</summary>
        public void NoMoreCandidates()
        {
            Exception fail;
            lock (_lock)
            {
                _noMore = true;
                fail = CheckAllFailedLocked();
            }
            if (fail != null) _result.TrySetException(fail);
        }

        public async Task<KeyValuePair<TcpClient, EndpointCandidate>> RunAsync(CancellationToken ct)
        {
            Exception fail;
            lock (_lock)
            {
                _raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _running = true;
                foreach (EndpointCandidate c in _pendingDirect) StartDial(c);
                _pendingDirect.Clear();
                fail = CheckAllFailedLocked();
            }
            if (fail != null) _result.TrySetException(fail);

            CancellationToken raceToken = _raceCts.Token;
            Task holdBack = Task.Delay(_holdBackMs, raceToken).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                Exception f;
                lock (_lock)
                {
                    _holdBackOver = true;
                    if (!_done)
                    {
                        foreach (EndpointCandidate c in _heldTunnels) StartDial(c);
                    }
                    _heldTunnels.Clear();
                    f = CheckAllFailedLocked();
                }
                if (f != null) _result.TrySetException(f);
            }, TaskScheduler.Default);

            Task overall = Task.Delay(_overallMs, raceToken).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                bool timedOut;
                lock (_lock)
                {
                    timedOut = !_done;
                    _done = true;
                }
                if (timedOut) _result.TrySetException(new TimeoutException("No route to the Host answered within " + (_overallMs / 1000) + " s."));
            }, TaskScheduler.Default);

            using (ct.Register(() => _result.TrySetCanceled()))
            {
                try
                {
                    return await _result.Task;
                }
                finally
                {
                    lock (_lock) { _done = true; }
                    _raceCts.Cancel(); // stops the timers and every attempt still running
                }
            }
        }

        private bool IsNew(EndpointCandidate c)
        {
            return c != null && !_done && _seen.Add(c.Host + ":" + c.Port);
        }

        /// <summary>Caller holds the lock.</summary>
        private void StartDial(EndpointCandidate c)
        {
            _inFlight++;
            CancellationTokenSource dialCts = CancellationTokenSource.CreateLinkedTokenSource(_raceCts.Token);
            dialCts.CancelAfter(_windowMs);
            Task.Run(async () =>
            {
                TcpClient client = null;
                Exception error = null;
                try
                {
                    client = await _dial(c, dialCts.Token);
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                bool won = false;
                Exception fail = null;
                lock (_lock)
                {
                    _inFlight--;
                    if (client != null && !_done)
                    {
                        _done = true;
                        won = true;
                    }
                    else if (client == null)
                    {
                        bool windowExpired = dialCts.IsCancellationRequested && !_raceCts.IsCancellationRequested;
                        _lastError = windowExpired
                            ? new TimeoutException(c.Host + ":" + c.Port + " did not answer within " + (_windowMs / 1000) + " s.")
                            : error;
                        fail = CheckAllFailedLocked();
                    }
                }

                if (won) _result.TrySetResult(new KeyValuePair<TcpClient, EndpointCandidate>(client, c));
                else if (client != null) { try { client.Close(); } catch { } }
                if (fail != null) _result.TrySetException(fail);
                dialCts.Dispose();
            });
        }

        /// <summary>Caller holds the lock. Returns the error to fail with once nothing is left to try.</summary>
        private Exception CheckAllFailedLocked()
        {
            if (_done || !_running || !_noMore || _inFlight > 0 || _pendingDirect.Count > 0) return null;
            if (_heldTunnels.Count > 0 && !_holdBackOver) return null; // they still get their turn
            _done = true;
            return _lastError ?? new SocketException((int)SocketError.HostNotFound);
        }
    }

    public static class NetworkHelper
    {
        /// <summary>
        /// TCP keep-alive: the OS probes an idle connection after <paramref name="idleMs"/>, then every
        /// <paramref name="intervalMs"/>, so a dead path fails the socket instead of hanging it forever.
        /// </summary>
        public static void EnableKeepAlive(Socket s, int idleMs = 15000, int intervalMs = 5000)
        {
            try
            {
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                byte[] values = new byte[12];
                BitConverter.GetBytes(1u).CopyTo(values, 0);
                BitConverter.GetBytes((uint)idleMs).CopyTo(values, 4);
                BitConverter.GetBytes((uint)intervalMs).CopyTo(values, 8);
                s.IOControl(IOControlCode.KeepAliveValues, values, null);
            }
            catch { }
        }

        public static List<string> GetLocalIpAddresses()
        {
            List<string> ips = new List<string>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    IPInterfaceProperties props = ni.GetIPProperties();
                    foreach (UnicastIPAddressInformation addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            string ip = addr.Address.ToString();
                            string label = (ni.Name.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) >= 0 || ip.StartsWith("100."))
                                ? string.Format("{0} (Tailscale / Direct P2P WAN)", ip)
                                : string.Format("{0} ({1})", ip, ni.Name);
                            ips.Add(label);
                        }
                    }
                }
            }
            catch { }

            if (ips.Count == 0)
            {
                ips.Add("127.0.0.1 (Localhost)");
            }
            return ips;
        }

        public static bool EnsureFirewallRule(string ruleName, int port)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = string.Format("advfirewall firewall add rule name=\"{0}\" dir=in action=allow protocol=TCP localport={1}", ruleName, port),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process proc = Process.Start(psi))
                {
                    proc.WaitForExit(3000);
                    return proc.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        public static async Task<string> TryUpnpPortForwardAsync(int internalPort, int externalPort)
        {
            try
            {
                string ssdpRequest =
                    "M-SEARCH * HTTP/1.1\r\n" +
                    "HOST: 239.255.255.250:1900\r\n" +
                    "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n" +
                    "MAN: \"ssdp:discover\"\r\n" +
                    "MX: 2\r\n\r\n";

                using (UdpClient udp = new UdpClient())
                {
                    byte[] reqBytes = Encoding.ASCII.GetBytes(ssdpRequest);
                    IPEndPoint multicastEp = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                    await udp.SendAsync(reqBytes, reqBytes.Length, multicastEp);

                    Task<UdpReceiveResult> receiveTask = udp.ReceiveAsync();
                    Task completed = await Task.WhenAny(receiveTask, Task.Delay(2200));
                    if (completed != receiveTask)
                    {
                        return "UPnP router discovery timed out (Use Tailscale, Public Tunnel, or Reverse Connect).";
                    }

                    string response = Encoding.ASCII.GetString(receiveTask.Result.Buffer);
                    foreach (string line in response.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                        {
                            string loc = line.Substring(9).Trim();
                            return string.Format("UPnP Gateway detected at {0}. Port {1} ready.", loc, externalPort);
                        }
                    }
                    return "UPnP gateway responded.";
                }
            }
            catch (Exception ex)
            {
                return "UPnP status: " + ex.Message;
            }
        }
    }

    /// <summary>
    /// The pinggy.io SSH reverse tunnel that gives the Host a public Internet address.
    /// </summary>
    public static class PublicTunnel
    {
        /// <summary>Prefers the Windows OpenSSH client; falls back to whatever is on PATH.</summary>
        public static string FindOpenSshTool(string exeName)
        {
            try
            {
                string inbox = Path.Combine(Path.Combine(Environment.SystemDirectory, "OpenSSH"), exeName);
                if (File.Exists(inbox)) return inbox;
            }
            catch { }
            return exeName;
        }

        /// <summary>
        /// pinggy only offers publickey and password sign-in. Without a key, ssh.exe stopped at a password
        /// prompt that no one could see or answer, so the tunnel never opened. pinggy's free tier accepts
        /// any key, so make one of our own once and reuse it. Returns the private key path, or null.
        /// </summary>
        public static string EnsureTunnelKey(out string error)
        {
            error = null;
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurboRemote");
            string keyPath = Path.Combine(dir, "tunnel_ed25519");
            try
            {
                if (File.Exists(keyPath) && new FileInfo(keyPath).Length > 0) return keyPath;

                Directory.CreateDirectory(dir);
                // A half-written key from an earlier failed attempt would make ssh-keygen ask "Overwrite (y/n)?".
                try { File.Delete(keyPath); } catch { }
                try { File.Delete(keyPath + ".pub"); } catch { }

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = FindOpenSshTool("ssh-keygen.exe"),
                    Arguments = string.Format("-q -t ed25519 -N \"\" -C TurboRemote-tunnel -f \"{0}\"", keyPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process proc = Process.Start(psi))
                {
                    proc.StandardInput.Close();
                    Task<string> stdout = proc.StandardOutput.ReadToEndAsync();
                    Task<string> stderr = proc.StandardError.ReadToEndAsync();
                    if (!proc.WaitForExit(15000))
                    {
                        try { proc.Kill(); } catch { }
                        error = "ssh-keygen.exe did not finish within 15 seconds.";
                        return null;
                    }
                    if (proc.ExitCode != 0 || !File.Exists(keyPath))
                    {
                        error = ("ssh-keygen.exe failed: " + stderr.Result + " " + stdout.Result).Trim();
                        return null;
                    }
                }
                return keyPath;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        public static string BuildSshArguments(int localPort, string keyPath)
        {
            // -T: no pseudo-terminal. With one, pinggy draws a full-screen UI made of escape codes and no
            //     newlines, so the address never reached the line-based output reader.
            // BatchMode: never wait on a prompt; fail fast and let the watchdog retry.
            // 127.0.0.1: "localhost" resolves to ::1 first, but the Host listens on IPv4 only.
            return string.Format(
                "-T -p 443 -R0:127.0.0.1:{0} -i \"{1}\" -o IdentitiesOnly=yes -o BatchMode=yes " +
                "-o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL -o ConnectTimeout=15 " +
                "-o ServerAliveInterval=20 -o ServerAliveCountMax=3 -o ExitOnForwardFailure=yes tcp@a.pinggy.io",
                localPort, keyPath);
        }

        /// <summary>
        /// Returns "host:port" from a pinggy line such as "tcp://abcd-1-2-3-4.run.pinggy-free.link:35793",
        /// or null when the line holds no tunnel address.
        /// </summary>
        public static string TryParseEndpoint(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            int idx = line.IndexOf("tcp://", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            string rest = line.Substring(idx + 6);
            int end = 0;
            // Stop at the first character that cannot be part of host:port (space, escape code, box char).
            while (end < rest.Length && (char.IsLetterOrDigit(rest[end]) || rest[end] == '.' || rest[end] == '-' || rest[end] == ':')) end++;
            string endpoint = rest.Substring(0, end);

            int colon = endpoint.LastIndexOf(':');
            int port;
            if (colon <= 0 || !int.TryParse(endpoint.Substring(colon + 1), out port) || port < 1 || port > 65535) return null;
            return endpoint;
        }
    }

    /// <summary>Per-user settings in %LOCALAPPDATA%\TurboRemote\settings.txt, one "key=value" per line.</summary>
    public static class TurboSettings
    {
        private static readonly object _lock = new object();

        /// <summary>Tests only: use this file instead of the per-user one.</summary>
        public static string FilePathOverride;

        public static string FilePath
        {
            get
            {
                if (!string.IsNullOrEmpty(FilePathOverride)) return FilePathOverride;
                return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurboRemote"), "settings.txt");
            }
        }

        public static string Get(string key, string defaultValue)
        {
            lock (_lock)
            {
                string value;
                return ReadAll().TryGetValue(key, out value) ? value : defaultValue;
            }
        }

        public static void Set(string key, string value)
        {
            lock (_lock)
            {
                try
                {
                    Dictionary<string, string> all = ReadAll();
                    all[key] = value;
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    StringBuilder sb = new StringBuilder();
                    foreach (KeyValuePair<string, string> kv in all) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
                    File.WriteAllText(FilePath, sb.ToString());
                }
                catch { }
            }
        }

        private static Dictionary<string, string> ReadAll()
        {
            Dictionary<string, string> all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath)) return all;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) all[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return all;
        }
    }

    /// <summary>
    /// Decides whether the Host's public pinggy tunnel should be open. "on"/"off" are fixed; "auto" keeps it off
    /// while Tailscale runs (the permanent route), and opens it when Tailscale is missing or has been down for
    /// two checks in a row. Right after sign-in, a Tailscale that is still starting gets 60 s to come up.
    /// </summary>
    public sealed class TunnelAutoSwitch
    {
        private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(60);
        private readonly string _setting;
        private bool _decided, _open;
        private int _notRunningChecks;

        public TunnelAutoSwitch(string setting)
        {
            string s = (setting ?? "").Trim().ToLowerInvariant();
            _setting = s == "on" || s == "off" ? s : "auto";
        }

        public bool IsAutomatic { get { return _setting == "auto"; } }

        public bool Update(TailscaleState state, TimeSpan sinceHostStart)
        {
            if (_setting == "on") return true;
            if (_setting == "off") return false;

            if (state == TailscaleState.Running)
            {
                _decided = true;
                _open = false;
                _notRunningChecks = 0;
                return false;
            }

            if (!_decided)
            {
                if (state == TailscaleState.Starting && sinceHostStart < StartupGrace) return false;
                _decided = true;
                _open = true;
                return true;
            }

            _notRunningChecks++;
            if (_notRunningChecks >= 2) _open = true;
            return _open;
        }
    }

    public enum TailscaleState { NotInstalled, Stopped, SignedOut, Starting, Running }

    /// <summary>What Tailscale reports about this PC. Strings are empty when unknown.</summary>
    public sealed class TailscaleStatus
    {
        public TailscaleState State = TailscaleState.Stopped;
        /// <summary>MagicDNS name, e.g. "office-pc.tail1234.ts.net" (no trailing dot).</summary>
        public string DnsName = "";
        public string HostName = "";
        public string IPv4 = "";

        public bool IsRunning { get { return State == TailscaleState.Running; } }
    }

    /// <summary>
    /// Tailscale (free mesh VPN) gives every PC in an account a permanent name and 100.64.0.0/10 address that
    /// works from anywhere, which is TurboRemote's recommended way to connect over the Internet.
    /// </summary>
    public static class TailscaleInfo
    {
        public static string DefaultCliPath
        {
            get { return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale"), "tailscale.exe"); }
        }

        /// <summary>True only for 100.64.0.0 - 100.127.255.255, the range Tailscale hands out.</summary>
        public static bool IsTailscaleAddress(string ip)
        {
            IPAddress addr;
            if (string.IsNullOrEmpty(ip) || !IPAddress.TryParse(ip, out addr) || addr.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = addr.GetAddressBytes();
            return b[0] == 100 && (b[1] & 0xC0) == 0x40;
        }

        /// <summary>Reads the output of "tailscale status --json". Only this PC's own entry ("Self") is used.</summary>
        public static TailscaleStatus Parse(string statusJson)
        {
            TailscaleStatus st = new TailscaleStatus();
            if (string.IsNullOrEmpty(statusJson)) return st;
            try
            {
                switch (ReadString(statusJson, "BackendState", 0, statusJson.Length))
                {
                    case "Running": st.State = TailscaleState.Running; break;
                    case "NeedsLogin":
                    case "NeedsMachineAuth": st.State = TailscaleState.SignedOut; break;
                    case "NoState":
                    case "Starting": st.State = TailscaleState.Starting; break;
                    default: st.State = TailscaleState.Stopped; break;
                }

                int selfStart, selfEnd;
                if (FindObject(statusJson, "Self", out selfStart, out selfEnd))
                {
                    st.DnsName = (ReadString(statusJson, "DNSName", selfStart, selfEnd) ?? "").TrimEnd('.');
                    st.HostName = ReadString(statusJson, "HostName", selfStart, selfEnd) ?? "";
                    st.IPv4 = FirstIPv4(statusJson, "TailscaleIPs", selfStart, selfEnd) ?? "";
                }
            }
            catch
            {
                return new TailscaleStatus();
            }
            return st;
        }

        /// <summary>
        /// Asks the Tailscale CLI (hidden, at most <paramref name="timeoutMs"/>), falling back to the network adapter
        /// when the CLI is missing, hangs or prints something unexpected. Call it off the UI thread.
        /// </summary>
        public static TailscaleStatus Query(string cliPath = null, int timeoutMs = 3000)
        {
            string cli = cliPath ?? DefaultCliPath;
            bool installed = false;
            try { installed = File.Exists(cli); } catch { }
            if (installed)
            {
                string json = RunCli(cli, "status --json", timeoutMs);
                if (json != null && json.IndexOf("\"BackendState\"", StringComparison.Ordinal) >= 0)
                {
                    return Parse(json);
                }
            }
            return FromAdapter(installed);
        }

        private static string RunCli(string cli, string arguments, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(cli, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using (Process p = Process.Start(psi))
                {
                    Task<string> output = p.StandardOutput.ReadToEndAsync();
                    Task<string> errors = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return null;
                    }
                    return output.Wait(1000) ? output.Result : null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static TailscaleStatus FromAdapter(bool cliInstalled)
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.Name.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) < 0 &&
                        ni.Description.IndexOf("Tailscale", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                    {
                        string ip = a.Address.ToString();
                        if (!IsTailscaleAddress(ip)) continue;
                        return new TailscaleStatus
                        {
                            State = ni.OperationalStatus == OperationalStatus.Up ? TailscaleState.Running : TailscaleState.Stopped,
                            HostName = Environment.MachineName,
                            IPv4 = ip
                        };
                    }
                    return new TailscaleStatus { State = TailscaleState.Stopped, HostName = Environment.MachineName };
                }
            }
            catch { }
            return new TailscaleStatus { State = cliInstalled ? TailscaleState.Stopped : TailscaleState.NotInstalled };
        }

        // ---- tiny JSON reading for a few known keys ----

        private static int ValueStart(string json, string key, int from, int to)
        {
            string quoted = "\"" + key + "\"";
            int i = from;
            while (true)
            {
                i = json.IndexOf(quoted, i, Math.Max(0, to - i), StringComparison.Ordinal);
                if (i < 0) return -1;
                int j = i + quoted.Length;
                while (j < to && char.IsWhiteSpace(json[j])) j++;
                if (j < to && json[j] == ':')
                {
                    j++;
                    while (j < to && char.IsWhiteSpace(json[j])) j++;
                    return j;
                }
                i = j;
            }
        }

        private static string ReadString(string json, string key, int from, int to)
        {
            int i = ValueStart(json, key, from, to);
            if (i < 0 || i >= to || json[i] != '"') return null;
            return ReadQuoted(json, i, out i);
        }

        private static string ReadQuoted(string json, int quoteIndex, out int after)
        {
            StringBuilder sb = new StringBuilder();
            int i = quoteIndex + 1;
            while (i < json.Length && json[i] != '"')
            {
                if (json[i] == '\\' && i + 1 < json.Length) { i++; }
                sb.Append(json[i]);
                i++;
            }
            after = i + 1;
            return sb.ToString();
        }

        private static bool FindObject(string json, string key, out int start, out int end)
        {
            start = end = -1;
            int i = ValueStart(json, key, 0, json.Length);
            if (i < 0 || i >= json.Length || json[i] != '{') return false;
            int depth = 0;
            for (int j = i; j < json.Length; j++)
            {
                char c = json[j];
                if (c == '"') { ReadQuoted(json, j, out j); j--; continue; }
                if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                {
                    start = i;
                    end = j;
                    return true;
                }
            }
            return false;
        }

        private static string FirstIPv4(string json, string key, int from, int to)
        {
            int i = ValueStart(json, key, from, to);
            if (i < 0 || i >= to || json[i] != '[') return null;
            for (int j = i + 1; j < to && json[j] != ']'; j++)
            {
                if (json[j] != '"') continue;
                string ip = ReadQuoted(json, j, out j);
                j--;
                IPAddress addr;
                if (IPAddress.TryParse(ip, out addr) && addr.AddressFamily == AddressFamily.InterNetwork) return ip;
            }
            return null;
        }
    }
}
