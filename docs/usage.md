# Using IrcConnection

[Index](README.md)

`IrcConnection` is one connection attempt. `RunAsync` returns when the peer closes, registration times out, a keepalive goes unanswered, the server sends `ERROR`, a join is refused, the inbound line exceeds 4096 bytes with no terminator, the TCP link resets, or the cancellation token fires. The caller reconnects by constructing another connection, or by calling `RunAsync` again after the previous call has returned. A second overlapping `RunAsync` throws `InvalidOperationException`.

```csharp
using Schipper.Io.Irc;

await using var irc = new IrcConnection(
    host: "irc.example.net",
    port: 6697,
    tls: true,
    nick: "ada",
    channel: "#workshop",
    log: line => logger.LogInformation("{Line}", line));

irc.ChannelMessage += (nick, text) =>
{
    // text is the PRIVMSG body for #workshop
};

await irc.RunAsync(cancellationToken);
```

A reconnect loop looks like this:

```csharp
while (!cancellationToken.IsCancellationRequested)
{
    await using var irc = new IrcConnection(host, port, tls: true, nick, channel, log);
    irc.ChannelMessage += OnMessage;

    try
    {
        await irc.RunAsync(cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        break;
    }

    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
}
```

The constructor rejects a `host`, `nick`, or `channel` that is empty or that contains NUL, CR, LF, space, or comma, and it rejects values that would make `NICK`, `USER`, `JOIN`, or the keepalive `PING` exceed 512 bytes including CRLF.

## What RunAsync does

1. Opens a TCP connection. When `tls` is true, it wraps the stream in `SslStream` and authenticates as a client using `host` as the target name. The handshake honors the cancellation token.
2. Sends `NICK` and `USER`. The `USER` real-name field is the fixed string `Shelly BBS ShellyNet`.
3. Waits for numeric `001` (welcome). Notices and other pre-welcome lines are written to the log callback.
4. On nick errors `431`, `432`, `433`, or `437`, retries as `{nick}{n}` up to eight attempts. The base nick is truncated to 12 characters before the suffix is added.
5. On `001`, stores `Nick` and sends `JOIN`. `Joined` stays false until the server echoes `:{nick} JOIN {channel}` (or `:{nick} JOIN :{channel}`) for that nick. A join-failure numeric that names the configured channel (`403`, `405`, `471`, `473`–`477`) is logged and `RunAsync` returns so the caller can reconnect.
6. Reads lines from a 4096-byte buffer. A server `PING` is answered with `PONG`. A `PRIVMSG` whose target equals the configured channel raises `ChannelMessage` with the sender nick and the text. An idle timeout leaves already-read bytes in place and sends a keepalive.
7. Returns on end of stream, `ERROR`, cancellation, a registration wait of 90 seconds, an unanswered keepalive, a join refusal, an inbound line with no LF in 4096 bytes, or `IOException` / `SocketException` from a dropped link. Cancellation still throws `OperationCanceledException`.

Registration allows 90 seconds on purpose. Networks that probe ident often sit quiet for half a minute before `001`.

After the join is confirmed, 150 seconds of silence sends `PING`. If the next wait is also idle, `RunAsync` returns so the caller can reconnect. Any inbound line clears the pending keepalive.

Writes (`PRIVMSG`, `PONG`, keepalive `PING`, registration) are serialized. Each command plus CRLF is at most 512 UTF-8 bytes.

## Sending

```csharp
if (irc.Joined)
{
    await irc.SendChannelAsync("hello", cancellationToken);
}
```

`SendChannelAsync` writes `PRIVMSG <channel> :<text>` with CRLF and UTF-8. It sends to the channel given in the constructor. It throws `InvalidOperationException` until `RunAsync` has a confirmed join. Text that contains NUL, CR, or LF, or that would make the command exceed 512 bytes including CRLF, throws `ArgumentException` and is not written.

Private messages, extra channels, `CAP`, SASL, and `WHO` are outside this type. The read loop ignores a `PRIVMSG` whose target is not the configured channel.

## Logging

The `Action<string>` passed to the constructor receives connection, registration, nick-retry, and link-failure lines. Pass a logger. A silent callback hides a handshake that is stuck on a notice.

## Disposal

`DisposeAsync` closes the writer, the network stream, and the socket, each even when a previous dispose throws. Dispose the connection when `RunAsync` returns, and dispose it to abandon an attempt. `await using` covers both. The socket is also closed on every `RunAsync` exit so a later attempt does not leak the previous client.

`Nick` is empty until `001`. After a nick collision it is the nick the server accepted, which can differ from the nick you requested. `Joined` is reset to false at the start of each `RunAsync` and becomes true only after the server confirms `JOIN`.
