using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopCore.Clipboard;

/// <summary>
/// A Wayland connection, down to the wire.
///
/// <para><b>Why this exists at all.</b> Watching the clipboard in the background on Wayland needs
/// the <c>ext-data-control</c> protocol, and a client can only speak it over a native Wayland
/// connection. Avalonia runs through XWayland, so its window cannot; <c>wl-clipboard</c> can, but
/// that is a package the user may not have. Speaking the protocol directly means the clipboard
/// works out of the box on any compositor that offers it.</para>
///
/// <para>The protocol is small: every message is an object id, an opcode, a length, and some
/// arguments, and file descriptors travel out of band in the socket's ancillary data. That last
/// part is why this is raw syscalls rather than a <c>Socket</c> - .NET has no way to attach an
/// SCM_RIGHTS control message, and passing a pipe is how the clipboard's contents are moved.</para>
/// </summary>
internal sealed class WaylandTransport : IDisposable
{
    private const int AF_UNIX = 1;
    private const int SOCK_STREAM = 1;
    private const int SOL_SOCKET = 1;
    private const int SCM_RIGHTS = 1;

    private readonly int _fd;
    private readonly object _writeGate = new();
    private readonly byte[] _readBuffer = new byte[8192];
    private int _readLength;
    private readonly Queue<int> _receivedFds = new();
    private bool _disposed;

    private WaylandTransport(int fd) => _fd = fd;

    /// <summary>Connects to the compositor, or returns null when there is none to connect to.</summary>
    public static WaylandTransport? TryConnect()
    {
        string? display = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");

        if (string.IsNullOrEmpty(display)) return null;

        // An absolute WAYLAND_DISPLAY is used as-is; that is what the spec says and it is how
        // nested compositors are addressed.
        string path = Path.IsPathRooted(display)
            ? display
            : string.IsNullOrEmpty(runtime) ? "" : Path.Combine(runtime, display);

        if (path.Length == 0 || !File.Exists(path)) return null;

        int fd = socket(AF_UNIX, SOCK_STREAM, 0);
        if (fd < 0) return null;

        var addr = new byte[110];
        BinaryPrimitives.WriteUInt16LittleEndian(addr, AF_UNIX);
        Encoding.UTF8.GetBytes(path).CopyTo(addr, 2);

        if (connect(fd, addr, addr.Length) != 0)
        {
            close(fd);
            return null;
        }

        return new WaylandTransport(fd);
    }

    // ──────────────────────────────── writing

    /// <summary>Sends one request. <paramref name="fd"/> travels in the ancillary data.</summary>
    public void Send(uint objectId, ushort opcode, ReadOnlySpan<byte> args, int fd = -1)
    {
        int size = 8 + args.Length;
        var message = new byte[size];

        BinaryPrimitives.WriteUInt32LittleEndian(message, objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), (uint)((size << 16) | opcode));
        args.CopyTo(message.AsSpan(8));

        lock (_writeGate)
        {
            if (fd < 0) SendPlain(message);
            else SendWithFd(message, fd);
        }
    }

    private unsafe void SendPlain(byte[] message)
    {
        fixed (byte* p = message)
        {
            int sent = 0;
            while (sent < message.Length)
            {
                long n = send(_fd, p + sent, (nuint)(message.Length - sent), 0);
                if (n <= 0) throw new IOException("The Wayland connection closed while writing.");
                sent += (int)n;
            }
        }
    }

    /// <summary>
    /// Sends a message with one file descriptor attached.
    ///
    /// The descriptor is not in the byte stream: it rides in an SCM_RIGHTS control message, and
    /// the kernel installs a copy of it in the receiving process. That is the whole mechanism by
    /// which the compositor hands clipboard bytes to a pipe this process created.
    /// </summary>
    private unsafe void SendWithFd(byte[] message, int fd)
    {
        const int controlSize = 16 + 8;   // CMSG_SPACE(sizeof(int)) with 8-byte alignment

        byte* control = stackalloc byte[controlSize];
        new Span<byte>(control, controlSize).Clear();

        // struct cmsghdr { size_t len; int level; int type; } then the payload.
        *(nuint*)control = (nuint)(16 + sizeof(int));       // CMSG_LEN(4)
        *(int*)(control + 8) = SOL_SOCKET;
        *(int*)(control + 12) = SCM_RIGHTS;
        *(int*)(control + 16) = fd;

        fixed (byte* p = message)
        {
            var iov = new IoVec { Base = (nint)p, Length = (nuint)message.Length };

            var header = new MsgHdr
            {
                Name = 0,
                NameLength = 0,
                Iov = (nint)(&iov),
                IovLength = 1,
                Control = (nint)control,
                ControlLength = controlSize,
                Flags = 0,
            };

            long n = sendmsg(_fd, ref header, 0);
            if (n <= 0) throw new IOException("The Wayland connection closed while passing a descriptor.");
        }
    }

    // ──────────────────────────────── reading

    /// <summary>
    /// Reads the next event, blocking until one arrives. Returns false when the connection ends.
    ///
    /// Any descriptors that arrive alongside are queued and handed out by
    /// <see cref="TakeFd"/>, because they belong to the event being decoded rather than to the
    /// socket read that happened to carry them.
    /// </summary>
    public bool TryReadEvent(out uint objectId, out ushort opcode, out byte[] body)
    {
        objectId = 0; opcode = 0; body = Array.Empty<byte>();

        while (true)
        {
            if (_readLength >= 8)
            {
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(_readBuffer);
                uint second = BinaryPrimitives.ReadUInt32LittleEndian(_readBuffer.AsSpan(4));
                int size = (int)(second >> 16);
                ushort op = (ushort)(second & 0xFFFF);

                if (size < 8 || size > _readBuffer.Length) return false;

                if (_readLength >= size)
                {
                    objectId = id;
                    opcode = op;
                    body = _readBuffer.AsSpan(8, size - 8).ToArray();

                    Array.Copy(_readBuffer, size, _readBuffer, 0, _readLength - size);
                    _readLength -= size;
                    return true;
                }
            }

            int read = Receive(_readBuffer.AsSpan(_readLength));
            if (read <= 0) return false;
            _readLength += read;
        }
    }

    /// <summary>One socket read, collecting any descriptors that came with it.</summary>
    private unsafe int Receive(Span<byte> into)
    {
        const int controlSize = 16 + 8 * 8;   // room for a few descriptors at once

        byte* control = stackalloc byte[controlSize];

        fixed (byte* p = into)
        {
            var iov = new IoVec { Base = (nint)p, Length = (nuint)into.Length };

            var header = new MsgHdr
            {
                Name = 0,
                NameLength = 0,
                Iov = (nint)(&iov),
                IovLength = 1,
                Control = (nint)control,
                ControlLength = controlSize,
                Flags = 0,
            };

            long n = recvmsg(_fd, ref header, 0);
            if (n <= 0) return (int)n;

            // Walk the control messages and keep every descriptor handed over.
            nuint offset = 0;
            while (offset + 16 <= header.ControlLength)
            {
                byte* cmsg = control + offset;
                nuint len = *(nuint*)cmsg;
                int level = *(int*)(cmsg + 8);
                int type = *(int*)(cmsg + 12);

                if (len < 16) break;

                if (level == SOL_SOCKET && type == SCM_RIGHTS)
                {
                    int count = (int)((len - 16) / sizeof(int));
                    for (int i = 0; i < count; i++)
                    {
                        lock (_receivedFds) _receivedFds.Enqueue(*(int*)(cmsg + 16 + i * sizeof(int)));
                    }
                }

                nuint aligned = (len + 7) & ~(nuint)7;
                if (aligned == 0) break;
                offset += aligned;
            }

            return (int)n;
        }
    }

    /// <summary>Takes the next descriptor the compositor handed over, or -1.</summary>
    public int TakeFd()
    {
        lock (_receivedFds) return _receivedFds.Count > 0 ? _receivedFds.Dequeue() : -1;
    }

    // ──────────────────────────────── argument helpers

    /// <summary>A Wayland string: length including the terminator, the bytes, then padding to 4.</summary>
    public static byte[] String(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        int padded = (utf8.Length + 1 + 3) & ~3;

        var buffer = new byte[4 + padded];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)(utf8.Length + 1));
        utf8.CopyTo(buffer, 4);
        return buffer;
    }

    public static byte[] UInt(uint value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return buffer;
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        int at = 0;
        foreach (var part in parts) { part.CopyTo(result, at); at += part.Length; }
        return result;
    }

    /// <summary>Reads a string argument, returning how many bytes it occupied.</summary>
    public static string ReadString(ReadOnlySpan<byte> body, ref int offset)
    {
        if (offset + 4 > body.Length) return "";

        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[offset..]);
        offset += 4;

        if (length <= 0 || offset + length > body.Length) return "";

        string value = Encoding.UTF8.GetString(body.Slice(offset, length - 1));
        offset += (length + 3) & ~3;
        return value;
    }

    public static uint ReadUInt(ReadOnlySpan<byte> body, ref int offset)
    {
        if (offset + 4 > body.Length) return 0;

        uint value = BinaryPrimitives.ReadUInt32LittleEndian(body[offset..]);
        offset += 4;
        return value;
    }

    // ──────────────────────────────── pipes and syscalls

    public static (int Read, int Write) CreatePipe()
    {
        var fds = new int[2];
        return pipe(fds) == 0 ? (fds[0], fds[1]) : (-1, -1);
    }

    public static byte[] ReadAll(int fd, int limit)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4096];

        while (output.Length < limit)
        {
            long n = ReadFd(fd, buffer, (nuint)buffer.Length);
            if (n <= 0) break;
            output.Write(buffer, 0, (int)n);
        }

        return output.ToArray();
    }

    public static void WriteAll(int fd, byte[] data)
    {
        int at = 0;
        while (at < data.Length)
        {
            long n = WriteFd(fd, data.AsSpan(at).ToArray(), (nuint)(data.Length - at));
            if (n <= 0) break;
            at += (int)n;
        }
    }

    public static void CloseFd(int fd) { if (fd >= 0) close(fd); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_receivedFds) { while (_receivedFds.Count > 0) close(_receivedFds.Dequeue()); }
        close(_fd);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVec { public nint Base; public nuint Length; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsgHdr
    {
        public nint Name;
        public uint NameLength;
        private readonly uint _pad;
        public nint Iov;
        public nuint IovLength;
        public nint Control;
        public nuint ControlLength;
        public int Flags;
        private readonly int _pad2;
    }

    [DllImport("libc", SetLastError = true)] private static extern int socket(int domain, int type, int protocol);
    [DllImport("libc", SetLastError = true)] private static extern int connect(int fd, byte[] addr, int len);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int pipe(int[] fds);
    [DllImport("libc", SetLastError = true)] private static unsafe extern long send(int fd, byte* buffer, nuint length, int flags);
    [DllImport("libc", SetLastError = true)] private static extern long sendmsg(int fd, ref MsgHdr message, int flags);
    [DllImport("libc", SetLastError = true)] private static extern long recvmsg(int fd, ref MsgHdr message, int flags);
    [DllImport("libc", SetLastError = true, EntryPoint = "read")] private static extern long ReadFd(int fd, byte[] buffer, nuint count);
    [DllImport("libc", SetLastError = true, EntryPoint = "write")] private static extern long WriteFd(int fd, byte[] buffer, nuint count);
}
