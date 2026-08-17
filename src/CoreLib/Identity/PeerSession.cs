using System;
using System.Security.Cryptography;

namespace CoreLib.Identity
{
    /// <summary>
    /// One connection's agreed key, and the peer it is agreed with.
    ///
    /// <para><b>Why this exists at all.</b> The key used to belong to the <em>peer</em> and was
    /// cached against its fingerprint for the life of the process, because a static agreement
    /// always produced the same value. With ephemeral keys mixed in there is a different key
    /// per connection, so it belongs to the connection - and disposing the connection is what
    /// destroys it. That destruction is the forward secrecy: once these bytes are gone, nothing
    /// short of the plaintext can recover what crossed this link.</para>
    ///
    /// <para>Encryption lives here rather than at the four call sites that need it, so both
    /// transports keep carrying byte-for-byte identical payloads and neither can drift into its
    /// own idea of the format.</para>
    /// </summary>
    public sealed class PeerSession : IDisposable
    {
        private readonly byte[] _key;
        private readonly PeerRegistry _registry;
        private bool _disposed;

        /// <summary>The paired device on the other end.</summary>
        public PeerRecord Peer { get; }

        public string Fingerprint => Peer.Fingerprint;

        /// <summary>
        /// False once the device has been forgotten, whatever state the link is in.
        ///
        /// <para>Checked on every payload rather than once at the start. The key used to live in
        /// a cache the registry could clear, so forgetting a device stopped it syncing
        /// immediately; a session holds its own copy, so without this a forgotten device would
        /// keep working until its link happened to drop. Revoking has to mean revoking now.</para>
        /// </summary>
        public bool IsUsable => !_disposed && _registry.IsTrusted(Fingerprint);

        internal PeerSession(PeerRecord peer, byte[] key, PeerRegistry registry)
        {
            Peer = peer;
            _key = key;
            _registry = registry;
        }

        /// <summary>Seals a payload for this peer. Null once disposed or revoked.</summary>
        public byte[]? Encrypt(byte contentType, ReadOnlySpan<byte> body)
        {
            if (!IsUsable) return null;

            try { return CryptoEngine.EncryptTagged(contentType, body, _key); }
            catch (Exception ex)
            {
                Diagnostics.Log.Write("Peers", "Encryption failed", ex);
                return null;
            }
        }

        /// <summary>
        /// Opens a payload that arrived on this connection.
        ///
        /// A failure here is no longer "try the next peer's key" - there is exactly one key
        /// this connection could have been sealed with, so a failure means the payload did not
        /// come from the device this session belongs to, and it is dropped.
        /// </summary>
        public bool TryDecrypt(byte[] encrypted, out DecryptedPayload result)
        {
            result = default;
            if (!IsUsable || encrypted == null || encrypted.Length == 0) return false;

            try
            {
                var (contentType, body) = CryptoEngine.DecryptTagged(encrypted, _key);
                result = new DecryptedPayload(Peer, contentType, body);
                return true;
            }
            catch
            {
                // Expected for a corrupt frame or a payload that is not ours. The caller logs;
                // there is nothing useful to add from in here.
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CryptographicOperations.ZeroMemory(_key);
        }
    }
}
