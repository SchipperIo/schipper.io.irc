using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Schipper.Io.Irc;

/// <summary>
/// A deliberately tiny IRC client — enough to register, join one channel, and relay <c>PRIVMSG</c>s,
/// answering <c>PING</c>. Written from scratch (no third-party library), matching the project's
/// "implement the protocol ourselves" approach. ShellyNet uses it purely as an encrypted relay bus;
/// the confidentiality/authentication lives in the caller's envelope layer, not here.
/// </summary>
/// <remarks>
/// One <see cref="RunAsync"/> call is one connection attempt: it connects, registers (retrying the
/// nick on a collision), joins, and pumps the read loop until the peer closes, the link goes idle, or
/// the token cancels — then it returns so the caller can reconnect. Every state transition is logged,
/// so a stuck link is visible rather than silent. A keepalive PING is sent when the link falls quiet,
/// and an unanswered keepalive tears the connection down for a reconnect.
/// </remarks>
public sealed class IrcConnection : IAsyncDisposable
{
    /// <summary>
    /// Give up waiting for <c>001</c> after this long and reconnect. Generous on purpose: networks
    /// that run a hostname/ident/proxy check (DALnet, Undernet, …) send a NOTICE or two and then go
    /// quiet for 30–60s waiting for the ident probe to time out before finally sending <c>001</c>.
    /// </summary>
    internal TimeSpan RegistrationTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Quiet period after which a keepalive PING is sent (and, if unanswered, the link dies).</summary>
    internal TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(150);

    /// <summary>Distinct nicks to try before giving up and reconnecting.</summary>
    private const int MaxNickAttempts = 8;

    private const int MaxIrcLineBytes = 512;
    private const int RecvBufferSize = 4096;

    private readonly string _host;
    private readonly int _port;
    private readonly bool _tls;
    private readonly string _requestedNick;
    private readonly string _channel;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _recvBuffer = new byte[RecvBufferSize];

    private TcpClient? _tcp;
    private Stream? _stream;
    private StreamWriter? _writer;
    private int _running;
    private int _recvLength;

    public IrcConnection(string host, int port, bool tls, string nick, string channel, Action<string> log)
    {
        RejectUnsafeIrcToken(host, nameof(host));
        RejectUnsafeIrcToken(nick, nameof(nick));
        RejectUnsafeIrcToken(channel, nameof(channel));
        ArgumentNullException.ThrowIfNull(log);

        EnsureCommandFits($"NICK {nick}", nameof(nick));
        EnsureCommandFits($"USER {nick} 0 * :Shelly BBS ShellyNet", nameof(nick));
        EnsureCommandFits($"JOIN {channel}", nameof(channel));
        EnsureCommandFits($"PING :{host}", nameof(host));

        _host = host;
        _port = port;
        _tls = tls;
        _requestedNick = nick;
        _channel = channel;
        _log = log;
    }

    /// <summary>Raised for each channel message: (sender nick, text).</summary>
    public event Action<string, string>? ChannelMessage;

    /// <summary>The nick actually registered (may differ from the requested one after a collision); empty until joined.</summary>
    public string Nick { get; private set; } = string.Empty;

    /// <summary>True once the server has confirmed the channel join on the current connection.</summary>
    public bool Joined { get; private set; }

    private enum ReadKind
    {
        Line,
        Eof,
        Idle,
        Overflow,
    }

    /// <summary>Connects, registers, joins the channel, then pumps the read loop until closed/idle/cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("An IRC connection attempt is already running.");
        }

        try
        {
            Joined = false;
            Nick = string.Empty;
            await DisposeConnectionAsync().ConfigureAwait(false);

            try
            {
                _log($"IRC: connecting to {_host}:{_port}{(_tls ? " (TLS)" : string.Empty)} as {_requestedNick}…");

                _tcp = new TcpClient();
                await _tcp.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);

                Stream net = _tcp.GetStream();
                if (_tls)
                {
                    SslStream ssl = new(net, leaveInnerStreamOpen: true);
                    SslClientAuthenticationOptions options = new() { TargetHost = _host };
                    await ssl.AuthenticateAsClientAsync(options, cancellationToken).ConfigureAwait(false);
                    net = ssl;
                }

                _stream = net;
                _writer = new StreamWriter(net, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\r\n",
                };
                _recvLength = 0;

                try
                {
                    await PumpAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    _log($"IRC: {_host} dropped the link ({ex.Message}); will reconnect.");
                }
                catch (SocketException ex)
                {
                    _log($"IRC: {_host} dropped the link ({ex.Message}); will reconnect.");
                }
                catch (ObjectDisposedException)
                {
                    _log($"IRC: {_host} dropped the link (disposed); will reconnect.");
                }
            }
            finally
            {
                await DisposeConnectionAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        string nick = _requestedNick;
        int attempt = 1;
        await SendRawAsync($"NICK {nick}", cancellationToken).ConfigureAwait(false);
        await SendRawAsync($"USER {nick} 0 * :Shelly BBS ShellyNet", cancellationToken).ConfigureAwait(false);

        bool keepalivePending = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            (ReadKind kind, string? line) =
                await ReadLineAsync(Joined ? IdleTimeout : RegistrationTimeout, cancellationToken).ConfigureAwait(false);

            if (kind == ReadKind.Overflow)
            {
                _log($"IRC: {_host} sent a line longer than {RecvBufferSize} bytes; will reconnect.");
                return;
            }

            if (kind == ReadKind.Eof)
            {
                _log(Joined
                    ? $"IRC: {_host} closed the connection; will reconnect."
                    : $"IRC: {_host} closed before registration completed; will reconnect.");
                return;
            }

            if (kind == ReadKind.Idle)
            {
                if (!Joined)
                {
                    _log($"IRC: {_host} did not complete registration within {RegistrationTimeout.TotalSeconds:0}s; will reconnect.");
                    return;
                }

                if (keepalivePending)
                {
                    _log($"IRC: {_host} stopped responding (no keepalive answer); will reconnect.");
                    return;
                }

                keepalivePending = true;
                await SendRawAsync($"PING :{_host}", cancellationToken).ConfigureAwait(false);
                continue;
            }

            keepalivePending = false;
            string raw = line!;

            if (raw.StartsWith("PING ", StringComparison.Ordinal))
            {
                string pong = "PONG " + raw[5..];
                if (Encoding.UTF8.GetByteCount(pong) + 2 > MaxIrcLineBytes)
                {
                    _log($"IRC: {_host} sent an oversized PING; will reconnect.");
                    return;
                }

                await SendRawAsync(pong, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (raw.StartsWith("ERROR", StringComparison.Ordinal))
            {
                _log($"IRC: {_host} sent ERROR ({Trim(raw)}); will reconnect.");
                return;
            }

            if (!Joined)
            {
                if (TryParseJoin(raw, out string joinNick, out string joinChannel) &&
                    string.Equals(joinNick, Nick, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(joinChannel, _channel, StringComparison.OrdinalIgnoreCase))
                {
                    Joined = true;
                    _log($"IRC: joined {_channel} on {_host} as {Nick}.");
                    continue;
                }

                if (TryNumeric(raw, out int code))
                {
                    if (code == 1)
                    {
                        Nick = nick;
                        await SendRawAsync($"JOIN {_channel}", cancellationToken).ConfigureAwait(false);
                        _log($"IRC: registered on {_host} as {nick}; joining {_channel}.");
                        continue;
                    }

                    if (code is 433 or 431 or 432 or 437)
                    {
                        if (attempt >= MaxNickAttempts)
                        {
                            _log($"IRC: could not acquire a nick on {_host} after {attempt} attempts; will reconnect.");
                            return;
                        }

                        attempt++;
                        nick = $"{Truncate(_requestedNick, 12)}{attempt}";
                        _log($"IRC: nick rejected on {_host} (reply {code}); retrying as {nick}.");
                        await SendRawAsync($"NICK {nick}", cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (IsJoinFailure(code) && NumericMentionsChannel(raw, _channel))
                    {
                        _log($"IRC: {_host} refused JOIN {_channel} (reply {code}); will reconnect.");
                        return;
                    }
                }

                _log($"IRC: {_host} » {Trim(raw)}");
                continue;
            }

            if (TryParsePrivmsg(raw, out string sender, out string target, out string text) &&
                string.Equals(target, _channel, StringComparison.OrdinalIgnoreCase))
            {
                ChannelMessage?.Invoke(sender, text);
            }
        }
    }

    /// <summary>Sends one line to the joined channel.</summary>
    public async Task SendChannelAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        RejectLineBreaks(text, nameof(text));

        string command = $"PRIVMSG {_channel} :{text}";
        EnsureCommandFits(command, nameof(text));

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_writer is null || !Joined)
            {
                throw new InvalidOperationException("Cannot send until the channel join has been confirmed.");
            }

            await _writer.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task SendRawAsync(string line, CancellationToken cancellationToken)
    {
        EnsureCommandFits(line, nameof(line));

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_writer is null)
            {
                throw new InvalidOperationException("Cannot send before the connection is open.");
            }

            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Reads one line, distinguishing a real end-of-stream from an idle timeout. The idle timeout
    /// arms a keepalive rather than reconnecting on the first quiet stretch; a genuine cancel of
    /// <paramref name="cancellationToken"/> propagates. Bytes already taken from the socket stay in
    /// the buffer across an idle timeout.
    /// </summary>
    private async Task<(ReadKind Kind, string? Line)> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        while (true)
        {
            int newline = IndexOfByte(_recvBuffer, _recvLength, (byte)'\n');
            if (newline >= 0)
            {
                int length = newline;
                if (length > 0 && _recvBuffer[length - 1] == (byte)'\r')
                {
                    length--;
                }

                string line = Encoding.UTF8.GetString(_recvBuffer, 0, length);
                int remaining = _recvLength - newline - 1;
                if (remaining > 0)
                {
                    Buffer.BlockCopy(_recvBuffer, newline + 1, _recvBuffer, 0, remaining);
                }

                _recvLength = remaining;
                return (ReadKind.Line, line);
            }

            if (_recvLength == _recvBuffer.Length)
            {
                return (ReadKind.Overflow, null);
            }

            using CancellationTokenSource timed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timed.CancelAfter(timeout);
            try
            {
                int read = await _stream!.ReadAsync(_recvBuffer.AsMemory(_recvLength, _recvBuffer.Length - _recvLength), timed.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return (ReadKind.Eof, null);
                }

                _recvLength += read;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (ReadKind.Idle, null);
            }
        }
    }

    /// <summary>Parses a numeric reply: <c>:server NNN target …</c>; NNN must be a 3-digit code.</summary>
    internal static bool TryNumeric(string line, out int code)
    {
        code = 0;
        if (line.Length == 0 || line[0] != ':')
        {
            return false;
        }

        int sp1 = line.IndexOf(' ', StringComparison.Ordinal);
        if (sp1 < 0)
        {
            return false;
        }

        int sp2 = line.IndexOf(' ', sp1 + 1);
        ReadOnlySpan<char> token = sp2 < 0 ? line.AsSpan(sp1 + 1) : line.AsSpan(sp1 + 1, sp2 - sp1 - 1);
        return token.Length == 3 && int.TryParse(token, out code);
    }

    /// <summary>Parses <c>:nick!user@host PRIVMSG &lt;target&gt; :&lt;text&gt;</c>.</summary>
    internal static bool TryParsePrivmsg(string line, out string nick, out string target, out string text)
    {
        nick = target = text = string.Empty;
        if (line.Length == 0 || line[0] != ':')
        {
            return false;
        }

        int sp1 = line.IndexOf(' ', StringComparison.Ordinal);
        if (sp1 < 0)
        {
            return false;
        }

        string prefix = line[1..sp1];
        int bang = prefix.IndexOf('!', StringComparison.Ordinal);
        nick = bang > 0 ? prefix[..bang] : prefix;

        string rest = line[(sp1 + 1)..];
        if (!rest.StartsWith("PRIVMSG ", StringComparison.Ordinal))
        {
            return false;
        }

        rest = rest["PRIVMSG ".Length..];
        int colon = rest.IndexOf(" :", StringComparison.Ordinal);
        if (colon < 0)
        {
            return false;
        }

        target = rest[..colon];
        text = rest[(colon + 2)..];
        return true;
    }

    internal static bool TryParseJoin(string line, out string nick, out string channel)
    {
        nick = channel = string.Empty;
        if (line.Length == 0 || line[0] != ':')
        {
            return false;
        }

        int sp1 = line.IndexOf(' ', StringComparison.Ordinal);
        if (sp1 < 0)
        {
            return false;
        }

        string prefix = line[1..sp1];
        int bang = prefix.IndexOf('!', StringComparison.Ordinal);
        nick = bang > 0 ? prefix[..bang] : prefix;

        string rest = line[(sp1 + 1)..];
        if (!rest.StartsWith("JOIN ", StringComparison.Ordinal))
        {
            return false;
        }

        string chan = rest["JOIN ".Length..];
        if (chan.StartsWith(':'))
        {
            chan = chan[1..];
        }

        int extra = chan.IndexOf(' ', StringComparison.Ordinal);
        if (extra >= 0)
        {
            chan = chan[..extra];
        }

        channel = chan;
        return nick.Length > 0 && channel.Length > 0;
    }

    internal static bool NumericMentionsChannel(string line, string channel)
    {
        int sp1 = line.IndexOf(' ', StringComparison.Ordinal);
        if (sp1 < 0)
        {
            return false;
        }

        int sp2 = line.IndexOf(' ', sp1 + 1);
        if (sp2 < 0)
        {
            return false;
        }

        int sp3 = line.IndexOf(' ', sp2 + 1);
        if (sp3 < 0)
        {
            return false;
        }

        ReadOnlySpan<char> rest = line.AsSpan(sp3 + 1);
        int end = rest.IndexOf(' ');
        ReadOnlySpan<char> token = end < 0 ? rest : rest[..end];
        if (token.Length > 0 && token[0] == ':')
        {
            token = token[1..];
        }

        return token.Equals(channel, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJoinFailure(int code) =>
        code is 403 or 405 or 471 or 473 or 474 or 475 or 476 or 477;

    private static void RejectUnsafeIrcToken(string value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        if (value.Length == 0)
        {
            throw new ArgumentException("IRC parameter cannot be empty.", paramName);
        }

        foreach (char c in value)
        {
            if (c is '\0' or '\r' or '\n' or ' ' or ',')
            {
                throw new ArgumentException("IRC parameter cannot contain NUL, CR, LF, space, or comma.", paramName);
            }
        }
    }

    private static void RejectLineBreaks(string value, string paramName)
    {
        foreach (char c in value)
        {
            if (c is '\0' or '\r' or '\n')
            {
                throw new ArgumentException("IRC text cannot contain NUL, CR, or LF.", paramName);
            }
        }
    }

    private static void EnsureCommandFits(string command, string paramName)
    {
        if (Encoding.UTF8.GetByteCount(command) + 2 > MaxIrcLineBytes)
        {
            throw new ArgumentException("IRC command exceeds 512 bytes including CRLF.", paramName);
        }
    }

    private static int IndexOfByte(byte[] buffer, int length, byte value)
    {
        for (int i = 0; i < length; i++)
        {
            if (buffer[i] == value)
            {
                return i;
            }
        }

        return -1;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static string Trim(string line) => line.Length <= 120 ? line : line[..120];

    public async ValueTask DisposeAsync()
    {
        await DisposeConnectionAsync().ConfigureAwait(false);
    }

    private async ValueTask DisposeConnectionAsync()
    {
        StreamWriter? writer;
        Stream? stream;
        TcpClient? tcp;

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            writer = _writer;
            stream = _stream;
            tcp = _tcp;
            _writer = null;
            _stream = null;
            _tcp = null;
            _recvLength = 0;
        }
        finally
        {
            _writeLock.Release();
        }

        try
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            try
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                try
                {
                    tcp?.Dispose();
                }
                catch (IOException)
                {
                }
                catch (SocketException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }
}
