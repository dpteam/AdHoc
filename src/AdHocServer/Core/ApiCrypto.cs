using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dpt.AdHoc.Core
{
    /// <summary>
    /// Application level protection for the admin API.
    ///
    /// Scheme (documented honestly in docs/SECURITY.md - it is NOT a TLS replacement):
    ///   K        = PBKDF2-HMAC-SHA256(adminToken, salt, iterations, 32)
    ///   kmac     = HMAC-SHA256(K, "dpt.mac.v1")
    ///   kenc     = HMAC-SHA256(K, "dpt.enc.v1")
    ///   request  = method \n target \n timestamp \n nonce \n hex(sha256(wireBody))
    ///              signature = hex(HMAC-SHA256(kmac, request))
    ///   response = hex(sha256(wireBody)) inside the same signature, then
    ///              envelope = base64(iv16 | HMAC-SHA256-CTR(kenc, iv) XOR data | HMAC-SHA256(kmac, iv|data)[0..16])
    ///
    /// Everything is pure BCL (.NET 4.0): HMACSHA256 + RNGCryptoServiceProvider.
    /// </summary>
    public static class ApiCrypto
    {
        public const string Scheme = "hmac-sha256+ctr";
        public const int KeyBytes = 32;
        public const int SaltBytes = 16;
        public const int NonceBytes = 16;
        public const int IvBytes = 16;
        public const int TagBytes = 16;
        public const int MaxClockSkewSeconds = 180;

        private static readonly byte[] MacLabel = Encoding.ASCII.GetBytes("dpt.mac.v1");
        private static readonly byte[] EncLabel = Encoding.ASCII.GetBytes("dpt.enc.v1");

        // --------------------------------------------------------------- random

        public static byte[] RandomBytes(int count)
        {
            byte[] buf = new byte[count];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(buf);
            }
            return buf;
        }

        public static string RandomHex(int bytes)
        {
            return Hex(RandomBytes(bytes));
        }

        public static string Hex(byte[] data)
        {
            if (data == null) return "";
            StringBuilder sb = new StringBuilder(data.Length * 2);
            for (int i = 0; i < data.Length; i++) sb.Append(data[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public static byte[] FromHex(string hex)
        {
            if (string.IsNullOrEmpty(hex) || (hex.Length & 1) != 0) return null;
            byte[] result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                int hi = HexVal(hex[i * 2]);
                int lo = HexVal(hex[i * 2 + 1]);
                if (hi < 0 || lo < 0) return null;
                result[i] = (byte)((hi << 4) | lo);
            }
            return result;
        }

        private static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        // -------------------------------------------------------------- hashing

        public static byte[] Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(data ?? new byte[0]);
        }

        public static byte[] Hmac(byte[] key, byte[] data)
        {
            using (HMACSHA256 hmac = new HMACSHA256(key ?? new byte[0]))
            {
                return hmac.ComputeHash(data ?? new byte[0]);
            }
        }

        /// <summary>PBKDF2-HMAC-SHA256 (RFC 8018). .NET 4.0 only ships the SHA1 variant, hence this.</summary>
        public static byte[] Pbkdf2(byte[] password, byte[] salt, int iterations, int length)
        {
            if (iterations < 1) iterations = 1;
            if (length <= 0) return new byte[0];

            byte[] output = new byte[length];
            using (HMACSHA256 hmac = new HMACSHA256(password ?? new byte[0]))
            {
                int offset = 0;
                int block = 1;
                byte[] saltBlock = new byte[(salt == null ? 0 : salt.Length) + 4];
                while (offset < length)
                {
                    if (salt != null && salt.Length > 0) Buffer.BlockCopy(salt, 0, saltBlock, 0, salt.Length);
                    int sl = salt == null ? 0 : salt.Length;
                    saltBlock[sl + 0] = (byte)(block >> 24);
                    saltBlock[sl + 1] = (byte)(block >> 16);
                    saltBlock[sl + 2] = (byte)(block >> 8);
                    saltBlock[sl + 3] = (byte)block;

                    byte[] u = hmac.ComputeHash(saltBlock);
                    byte[] t = new byte[u.Length];
                    Buffer.BlockCopy(u, 0, t, 0, u.Length);
                    for (int i = 1; i < iterations; i++)
                    {
                        u = hmac.ComputeHash(u);
                        for (int j = 0; j < t.Length; j++) t[j] ^= u[j];
                    }

                    int take = Math.Min(t.Length, length - offset);
                    Buffer.BlockCopy(t, 0, output, offset, take);
                    offset += take;
                    block++;
                }
            }
            return output;
        }

        public static byte[] DeriveKey(string token, string saltHex, int iterations)
        {
            byte[] salt = FromHex(saltHex);
            if (salt == null || salt.Length == 0) salt = new byte[SaltBytes];
            return Pbkdf2(Encoding.UTF8.GetBytes(token ?? ""), salt, iterations, KeyBytes);
        }

        public static byte[] SubKey(byte[] master, byte[] label)
        {
            return Hmac(master, label);
        }

        // ------------------------------------------------------------ signature

        public static string Canonical(string method, string target, string timestamp, string nonce, byte[] body)
        {
            return (method ?? "GET").ToUpperInvariant() + "\n" +
                   (target ?? "/") + "\n" +
                   (timestamp ?? "") + "\n" +
                   (nonce ?? "") + "\n" +
                   Hex(Sha256(body));
        }

        public static string Signature(byte[] masterKey, string method, string target, string timestamp, string nonce, byte[] body)
        {
            byte[] mac = Hmac(SubKey(masterKey, MacLabel), Encoding.UTF8.GetBytes(Canonical(method, target, timestamp, nonce, body)));
            return Hex(mac);
        }

        /// <summary>Length independent comparison for hex strings (no early exit on content).</summary>
        public static bool FixedEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                char ca = char.ToLowerInvariant(a[i]);
                char cb = char.ToLowerInvariant(b[i]);
                diff |= ca ^ cb;
            }
            return diff == 0;
        }

        // -------------------------------------------------------------- sealing

        /// <summary>Encrypt-then-MAC envelope: base64(iv | ciphertext | tag[16]).</summary>
        public static string Seal(byte[] masterKey, byte[] plaintext)
        {
            byte[] iv = RandomBytes(IvBytes);
            byte[] cipher = new byte[plaintext.Length];
            Buffer.BlockCopy(plaintext, 0, cipher, 0, plaintext.Length);
            XorKeystream(SubKey(masterKey, EncLabel), iv, cipher, 0, cipher.Length);

            byte[] macInput = new byte[IvBytes + cipher.Length];
            Buffer.BlockCopy(iv, 0, macInput, 0, IvBytes);
            Buffer.BlockCopy(cipher, 0, macInput, IvBytes, cipher.Length);
            byte[] tag = Hmac(SubKey(masterKey, MacLabel), macInput);

            byte[] envelope = new byte[IvBytes + cipher.Length + TagBytes];
            Buffer.BlockCopy(iv, 0, envelope, 0, IvBytes);
            Buffer.BlockCopy(cipher, 0, envelope, IvBytes, cipher.Length);
            Buffer.BlockCopy(tag, 0, envelope, IvBytes + cipher.Length, TagBytes);
            return Convert.ToBase64String(envelope);
        }

        /// <summary>Returns null when the envelope or its MAC is invalid.</summary>
        public static byte[] Open(byte[] masterKey, string envelopeBase64)
        {
            if (string.IsNullOrEmpty(envelopeBase64)) return null;
            byte[] envelope;
            try { envelope = Convert.FromBase64String(envelopeBase64); }
            catch { return null; }
            if (envelope.Length < IvBytes + TagBytes) return null;

            byte[] iv = new byte[IvBytes];
            Buffer.BlockCopy(envelope, 0, iv, 0, IvBytes);
            int cipherLen = envelope.Length - IvBytes - TagBytes;
            byte[] cipher = new byte[cipherLen];
            Buffer.BlockCopy(envelope, IvBytes, cipher, 0, cipherLen);
            byte[] tag = new byte[TagBytes];
            Buffer.BlockCopy(envelope, IvBytes + cipherLen, tag, 0, TagBytes);

            byte[] macInput = new byte[IvBytes + cipherLen];
            Buffer.BlockCopy(iv, 0, macInput, 0, IvBytes);
            Buffer.BlockCopy(cipher, 0, macInput, IvBytes, cipherLen);
            byte[] expect = Hmac(SubKey(masterKey, MacLabel), macInput);

            int diff = 0;
            for (int i = 0; i < TagBytes; i++) diff |= tag[i] ^ expect[i];
            if (diff != 0) return null;

            XorKeystream(SubKey(masterKey, EncLabel), iv, cipher, 0, cipherLen);
            return cipher;
        }

        /// <summary>HMAC-SHA256 in counter mode used as a PRF based stream cipher.</summary>
        private static void XorKeystream(byte[] encKey, byte[] iv, byte[] data, int offset, int count)
        {
            using (HMACSHA256 hmac = new HMACSHA256(encKey))
            {
                byte[] blockInput = new byte[IvBytes + 4];
                Buffer.BlockCopy(iv, 0, blockInput, 0, IvBytes);
                byte[] stream = new byte[32];
                int produced = 0;
                uint counter = 0;
                for (int i = 0; i < count; i++)
                {
                    if (produced == 0)
                    {
                        blockInput[IvBytes + 0] = (byte)(counter >> 24);
                        blockInput[IvBytes + 1] = (byte)(counter >> 16);
                        blockInput[IvBytes + 2] = (byte)(counter >> 8);
                        blockInput[IvBytes + 3] = (byte)counter;
                        stream = hmac.ComputeHash(blockInput);
                        produced = stream.Length;
                        counter++;
                    }
                    data[offset + i] ^= stream[stream.Length - produced];
                    produced--;
                }
            }
        }

        public static bool IsFreshTimestamp(string timestamp, DateTime nowUtc)
        {
            long ts;
            if (!long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out ts)) return false;
            long now = (long)(nowUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            long delta = now - ts;
            if (delta < 0) delta = -delta;
            return delta <= MaxClockSkewSeconds;
        }

        public static bool IsValidNonce(string nonce)
        {
            if (string.IsNullOrEmpty(nonce) || nonce.Length < 16 || nonce.Length > 64) return false;
            for (int i = 0; i < nonce.Length; i++)
            {
                char c = nonce[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }
    }

    /// <summary>Replay protection: remembers nonces for a while, bounded in size.</summary>
    public sealed class NonceCache
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, DateTime> _seen = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly TimeSpan _window;
        private readonly int _max;

        public NonceCache(TimeSpan window, int max)
        {
            _window = window;
            _max = max;
        }

        public bool TryUse(string nonce)
        {
            if (string.IsNullOrEmpty(nonce)) return false;
            DateTime now = DateTime.UtcNow;
            lock (_sync)
            {
                DateTime seen;
                if (_seen.TryGetValue(nonce, out seen) && (now - seen) < _window) return false;

                if (_seen.Count > _max) PruneLocked(now);
                _seen[nonce] = now;
                return true;
            }
        }

        private void PruneLocked(DateTime now)
        {
            List<string> dead = new List<string>();
            foreach (KeyValuePair<string, DateTime> kv in _seen)
                if ((now - kv.Value) >= _window) dead.Add(kv.Key);
            for (int i = 0; i < dead.Count; i++) _seen.Remove(dead[i]);
            // still too big (clock jump / flood): drop the oldest half
            if (_seen.Count > _max)
            {
                List<KeyValuePair<string, DateTime>> all = new List<KeyValuePair<string, DateTime>>(_seen);
                all.Sort(delegate (KeyValuePair<string, DateTime> a, KeyValuePair<string, DateTime> b) { return a.Value.CompareTo(b.Value); });
                int drop = all.Count / 2;
                for (int i = 0; i < drop; i++) _seen.Remove(all[i].Key);
            }
        }
    }
}