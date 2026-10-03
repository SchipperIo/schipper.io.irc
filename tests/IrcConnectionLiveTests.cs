using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Schipper.Io.Irc.Tests;

/// <summary>
/// Live loopback tests: a scripted TCP "IRC server" drives one <see cref="IrcConnection"/> through
/// registration, nick collision, PING, and channel relay, asserting the client's behaviour on the wire.
/// </summary>
[TestClass]
public sealed class IrcConnectionLiveTests
{
    /// <summary>
    /// Spins up a loopback listener, starts an <see cref="IrcConnection"/> against it, and hands the
    /// test the server-side reader/writer plus the client — a single script controls interleaving.
    /// </summary>
    private static async Task RunAsync(
        Func<StreamReader, StreamWriter, IrcConnection, Task, CancellationToken, Task> script,
        Action<IrcConnection>? configure = null)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
        Task<TcpClient> accept = listener.AcceptTcpClientAsync(cts.Token).AsTask();

        await using IrcConnection conn = new("127.0.0.1", port, tls: false, "ShellyBBS", "#shellytest", _ => { });
        configure?.Invoke(conn);
        Task run = conn.RunAsync(cts.Token);

        using TcpClient server = await accept;
        listener.Stop();
        await using NetworkStream ns = server.GetStream();
        using StreamReader reader = new(ns, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        await using StreamWriter writer = new(ns, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true, NewLine = "\r\n" };

        try
        {
            await script(reader, writer, conn, run, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            await cts.CancelAsync();
            try
            {
                await run.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static async Task<string> ReadUntilAsync(StreamReader reader, string prefix, CancellationToken ct)
    {
        while (true)
        {
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            Assert.IsNotNull(line, $"server stream closed while waiting for '{prefix}'");
            if (line!.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line;
            }
        }
    }

    private static async Task WaitAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(25, ct).ConfigureAwait(false);
        }
    }

    private static async Task AcknowledgeJoinAsync(StreamReader reader, StreamWriter writer, CancellationToken ct, string nick = "ShellyBBS")
    {
        await ReadUntilAsync(reader, "NICK ", ct).ConfigureAwait(false);
        await ReadUntilAsync(reader, "USER ", ct).ConfigureAwait(false);
        await writer.WriteLineAsync($":srv 001 {nick} :Welcome").ConfigureAwait(false);
        await ReadUntilAsync(reader, "JOIN #shellytest", ct).ConfigureAwait(false);
        await writer.WriteLineAsync($":{nick} JOIN #shellytest").ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RegistersAndJoinsTheChannel() =>
        await RunAsync(async (reader, writer, conn, _, ct) =>
        {
            await AcknowledgeJoinAsync(reader, writer, ct);
            await WaitAsync(() => conn.Joined, ct);
            Assert.AreEqual("ShellyBBS", conn.Nick);
        });

    [TestMethod]
    public async Task RetriesTheNickOnCollisionThenJoins() =>
        await RunAsync(async (reader, writer, conn, _, ct) =>
        {
            await ReadUntilAsync(reader, "NICK ShellyBBS", ct);
            await ReadUntilAsync(reader, "USER ", ct);

            await writer.WriteLineAsync(":srv 433 * ShellyBBS :Nickname is already in use");
            string retry = await ReadUntilAsync(reader, "NICK ", ct);
            Assert.AreNotEqual("NICK ShellyBBS", retry, "the retried nick must differ from the rejected one");

            string retriedNick = retry["NICK ".Length..];
            await writer.WriteLineAsync($":srv 001 {retriedNick} :Welcome");
            await ReadUntilAsync(reader, "JOIN #shellytest", ct);
            await writer.WriteLineAsync($":{retriedNick} JOIN #shellytest");

            await WaitAsync(() => conn.Joined, ct);

            Assert.AreEqual(retriedNick, conn.Nick);
            Assert.AreNotEqual("ShellyBBS", conn.Nick);
            StringAssert.StartsWith(conn.Nick, "ShellyBBS", "the retried nick keeps the board prefix");
        });

    [TestMethod]
    public async Task RetriesTheNickOnUnavailResourceThenJoins() =>
        await RunAsync(async (reader, writer, conn, _, ct) =>
        {
            await ReadUntilAsync(reader, "NICK ShellyBBS", ct);
            await ReadUntilAsync(reader, "USER ", ct);

            await writer.WriteLineAsync(":srv 437 * ShellyBBS :Nick is temporarily unavailable");
            string retry = await ReadUntilAsync(reader, "NICK ", ct);
            Assert.AreNotEqual("NICK ShellyBBS", retry, "the retried nick must differ from the rejected one");

            string retriedNick = retry["NICK ".Length..];
            await writer.WriteLineAsync($":srv 001 {retriedNick} :Welcome");
            await ReadUntilAsync(reader, "JOIN #shellytest", ct);
            await writer.WriteLineAsync($":{retriedNick} JOIN #shellytest");
            await WaitAsync(() => conn.Joined, ct);
            Assert.AreEqual(retriedNick, conn.Nick);
        });

    [TestMethod]
    public async Task JoinBanLeavesJoinedFalse() =>
        await RunAsync(async (reader, writer, conn, run, ct) =>
        {
            await ReadUntilAsync(reader, "NICK ShellyBBS", ct);
            await ReadUntilAsync(reader, "USER ", ct);
            await writer.WriteLineAsync(":srv 001 ShellyBBS :Welcome");
            await ReadUntilAsync(reader, "JOIN #shellytest", ct);
            await writer.WriteLineAsync(":srv 474 ShellyBBS #shellytest :Cannot join channel (+b)");

            await run.WaitAsync(ct);
            Assert.IsFalse(conn.Joined);
        });

    [TestMethod]
    public async Task AnswersServerPingWithPong() =>
        await RunAsync(async (reader, writer, conn, _, ct) =>
        {
            await AcknowledgeJoinAsync(reader, writer, ct);
            await WaitAsync(() => conn.Joined, ct);

            await writer.WriteLineAsync("PING :abc123");
            string pong = await ReadUntilAsync(reader, "PONG ", ct);
            StringAssert.Contains(pong, "abc123");
        });

    [TestMethod]
    public async Task RelaysChannelPrivmsgToSubscribers() =>
        await RunAsync(async (reader, writer, conn, _, ct) =>
        {
            TaskCompletionSource<(string Nick, string Text)> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
            conn.ChannelMessage += (n, t) => received.TrySetResult((n, t));

            await AcknowledgeJoinAsync(reader, writer, ct);
            await WaitAsync(() => conn.Joined, ct);
            await writer.WriteLineAsync(":alice!u@h PRIVMSG #shellytest :hello board");

            (string Nick, string Text) got = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.AreEqual("alice", got.Nick);
            Assert.AreEqual("hello board", got.Text);
        });

    [TestMethod]
    public async Task IgnoresPrivmsgToOtherChannels() =>
        await RunAsync(async (reader, writer, conn, _, ct) =>
        {
            TaskCompletionSource<bool> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
            conn.ChannelMessage += (_, _) => received.TrySetResult(true);

            await AcknowledgeJoinAsync(reader, writer, ct);
            await WaitAsync(() => conn.Joined, ct);
            await writer.WriteLineAsync(":bob!u@h PRIVMSG #other :not for us");

            Task done = await Task.WhenAny(received.Task, Task.Delay(500, ct));
            Assert.AreNotSame(received.Task, done, "a message to another channel must not raise ChannelMessage");
        });

    [TestMethod]
    public async Task SendChannelAsync_WritesOneServerLine_WhenTextIsShort() =>
        await RunAsync(async (reader, writer, conn, _, ct) =>
        {
            await AcknowledgeJoinAsync(reader, writer, ct);
            await WaitAsync(() => conn.Joined, ct);
            await conn.SendChannelAsync("hello", ct);
            string line = await ReadUntilAsync(reader, "PRIVMSG ", ct);
            Assert.AreEqual("PRIVMSG #shellytest :hello", line);
        });

    [TestMethod]
    public async Task SendChannelAsync_DoesNotWrite_WhenMessageExceeds512Bytes() =>
        await RunAsync(async (reader, writer, conn, _, ct) =>
        {
            await AcknowledgeJoinAsync(reader, writer, ct);
            await WaitAsync(() => conn.Joined, ct);

            await Assert.ThrowsExactlyAsync<ArgumentException>(
                () => conn.SendChannelAsync(new string('x', 600), ct));

            await conn.SendChannelAsync("ok", ct);
            string line = await ReadUntilAsync(reader, "PRIVMSG ", ct);
            Assert.AreEqual("PRIVMSG #shellytest :ok", line);
        });

    [TestMethod]
    public async Task ConcurrentSendAndPong_AreWholeLines() =>
        await RunAsync(async (reader, writer, conn, _, ct) =>
        {
            await AcknowledgeJoinAsync(reader, writer, ct);
            await WaitAsync(() => conn.Joined, ct);

            Task send = conn.SendChannelAsync("hello", ct);
            await writer.WriteLineAsync("PING :token");

            List<string> seen = [];
            while (seen.Count < 2)
            {
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                Assert.IsNotNull(line);
                Assert.IsTrue(
                    line == "PONG :token" || line == "PRIVMSG #shellytest :hello",
                    $"unexpected server line '{line}'");
                seen.Add(line!);
            }

            await send;
            CollectionAssert.Contains(seen, "PONG :token");
            CollectionAssert.Contains(seen, "PRIVMSG #shellytest :hello");
        });

    [TestMethod]
    public async Task ErrorClosesTheSocketBeforeDispose() =>
        await RunAsync(async (reader, writer, conn, run, ct) =>
        {
            await ReadUntilAsync(reader, "NICK ", ct);
            await ReadUntilAsync(reader, "USER ", ct);
            await writer.WriteLineAsync("ERROR :bye");

            await run.WaitAsync(ct);
            string? eof = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            Assert.IsNull(eof, "the client must close the socket when RunAsync returns");
            Assert.IsFalse(conn.Joined);
        });

    [TestMethod]
    public async Task SplitPingSurvivesIdleTimeout() =>
        await RunAsync(async (reader, writer, conn, _, ct) =>
        {
            await AcknowledgeJoinAsync(reader, writer, ct);
            await WaitAsync(() => conn.Joined, ct);

            await writer.WriteAsync("PING :ab");
            await writer.FlushAsync(ct);
            await Task.Delay(300, ct);
            await writer.WriteAsync("c\r\n");
            await writer.FlushAsync(ct);

            string pong = await ReadUntilAsync(reader, "PONG ", ct);
            Assert.AreEqual("PONG :abc", pong);
        }, conn => conn.IdleTimeout = TimeSpan.FromMilliseconds(200));

    [TestMethod]
    public async Task LineWithoutLfReturns() =>
        await RunAsync(async (reader, writer, conn, run, ct) =>
        {
            await ReadUntilAsync(reader, "NICK ", ct);
            byte[] junk = Encoding.ASCII.GetBytes(new string('A', 8192));
            await writer.BaseStream.WriteAsync(junk, ct);
            await writer.BaseStream.FlushAsync(ct);
            await run.WaitAsync(ct);
        });

    [TestMethod]
    public async Task OverlappingRunAsync_LeavesOneLiveConnection()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
        await using IrcConnection conn = new("127.0.0.1", port, tls: false, "ShellyBBS", "#shellytest", _ => { });
        Task first = conn.RunAsync(cts.Token);

        using TcpClient server = await listener.AcceptTcpClientAsync(cts.Token);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => conn.RunAsync(cts.Token));

        using CancellationTokenSource acceptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        acceptTimeout.CancelAfter(TimeSpan.FromMilliseconds(400));
        try
        {
            await listener.AcceptTcpClientAsync(acceptTimeout.Token);
            Assert.Fail("a second RunAsync must not open another connection");
        }
        catch (OperationCanceledException)
        {
        }

        Assert.IsTrue(server.Connected);
        listener.Stop();
        await cts.CancelAsync();
        try
        {
            await first;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [TestMethod]
    public async Task TcpReset_ReturnsFromRunAsync()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
        await using IrcConnection conn = new("127.0.0.1", port, tls: false, "ShellyBBS", "#shellytest", _ => { });
        Task run = conn.RunAsync(cts.Token);

        using TcpClient server = await listener.AcceptTcpClientAsync(cts.Token);
        listener.Stop();
        await using NetworkStream ns = server.GetStream();
        using StreamReader reader = new(ns, Encoding.UTF8);
        await ReadUntilAsync(reader, "NICK ", cts.Token);
        server.LingerState = new LingerOption(enable: true, seconds: 0);
        server.Client.Close(timeout: 0);

        await run.WaitAsync(cts.Token);
    }

    [TestMethod]
    public async Task DisposeAfterPeerReset_DoesNotThrow()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
        IrcConnection conn = new("127.0.0.1", port, tls: false, "ShellyBBS", "#shellytest", _ => { });
        Task run = conn.RunAsync(cts.Token);

        using TcpClient server = await listener.AcceptTcpClientAsync(cts.Token);
        listener.Stop();
        await using NetworkStream ns = server.GetStream();
        using StreamReader reader = new(ns, Encoding.UTF8);
        await ReadUntilAsync(reader, "NICK ", cts.Token);
        server.LingerState = new LingerOption(enable: true, seconds: 0);
        server.Client.Close(timeout: 0);

        await run.WaitAsync(cts.Token);
        await conn.DisposeAsync();
    }

    [TestMethod]
    public async Task TlsHandshake_HonorsCancellation()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using CancellationTokenSource cts = new();
        await using IrcConnection conn = new("127.0.0.1", port, tls: true, "ShellyBBS", "#shellytest", _ => { });
        Task run = conn.RunAsync(cts.Token);

        using TcpClient server = await listener.AcceptTcpClientAsync(CancellationToken.None);
        listener.Stop();

        await Task.Delay(200);
        await cts.CancelAsync();

        try
        {
            await run;
            Assert.Fail("TLS RunAsync must complete with OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
        }
    }
}
