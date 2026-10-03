# Schipper.Io.Irc

A small IRC client for .NET 10. One connection registers a nick, joins one channel, relays `PRIVMSG` lines from that channel, and answers `PING`.

The library is a relay. It does not authenticate users, encrypt payloads, or open more than one channel. Put confidentiality and authentication in the caller's own envelope.

## Guides

| Guide | What it covers |
| --- | --- |
| [Usage](usage.md) | `IrcConnection`: connect, join, send, and reconnect |

## Public surface

Everything lives in `Schipper.Io.Irc`.

| Member | Role |
| --- | --- |
| `IrcConnection` | One connection attempt |
| `RunAsync` | Connect, register, join, and read until the link ends |
| `SendChannelAsync` | Send one `PRIVMSG` to the configured channel |
| `ChannelMessage` | Raised as `(nick, text)` for each channel message |
| `Nick` | The nick the server accepted. Empty until registration completes |
| `Joined` | True after the server confirms `JOIN` |
| `DisposeAsync` | Closes the socket |

There are no other public types. The package has no dependencies.

## Package

```xml
<PackageReference Include="Schipper.Io.Irc" Version="0.1.0-dev" />
```

Target framework: `net10.0`.
