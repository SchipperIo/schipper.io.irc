using Schipper.Io.Irc;

namespace Schipper.Io.Irc.Tests;

[TestClass]
public sealed class IrcConnectionTests
{
    [TestMethod]
    public void TryParsePrivmsg_ParsesNickTargetAndText()
    {
        bool ok = IrcConnection.TryParsePrivmsg(
            ":alice!user@host PRIVMSG #shellynet :hello world", out string nick, out string target, out string text);

        Assert.IsTrue(ok);
        Assert.AreEqual("alice", nick);
        Assert.AreEqual("#shellynet", target);
        Assert.AreEqual("hello world", text);
    }

    [TestMethod]
    public void TryParsePrivmsg_PrefixWithoutBang_UsesWholePrefixAsNick()
    {
        bool ok = IrcConnection.TryParsePrivmsg(
            ":server PRIVMSG #c :hi", out string nick, out string target, out string text);

        Assert.IsTrue(ok);
        Assert.AreEqual("server", nick);
        Assert.AreEqual("#c", target);
        Assert.AreEqual("hi", text);
    }

    [TestMethod]
    public void TryParsePrivmsg_TextContainingColons_IsPreserved()
    {
        bool ok = IrcConnection.TryParsePrivmsg(
            ":bob!b@h PRIVMSG #c :a:b :c", out _, out _, out string text);

        Assert.IsTrue(ok);
        Assert.AreEqual("a:b :c", text);
    }

    [TestMethod]
    [DataRow("PING :x")]
    [DataRow(":nick JOIN #c")]
    [DataRow(":nick!u@h PRIVMSG #c")]
    [DataRow("")]
    public void TryParsePrivmsg_RejectsNonPrivmsgLines(string line)
    {
        Assert.IsFalse(IrcConnection.TryParsePrivmsg(line, out _, out _, out _));
    }

    [TestMethod]
    public void Constructor_RejectsNickThatEmbedsACommand()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new IrcConnection("127.0.0.1", 6667, tls: false, "A\r\nQUIT", "#c", _ => { }));
    }

    [TestMethod]
    public void Constructor_RejectsChannelThatEmbedsACommand()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new IrcConnection("127.0.0.1", 6667, tls: false, "nick", "#c\nJOIN #x", _ => { }));
    }

    [TestMethod]
    public async Task SendChannelAsync_BeforeRunAsync_Throws()
    {
        await using IrcConnection conn = new("127.0.0.1", 6667, tls: false, "nick", "#c", _ => { });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => conn.SendChannelAsync("hello", CancellationToken.None));
    }

    [TestMethod]
    public async Task SendChannelAsync_RejectsTextThatEmbedsACommand()
    {
        await using IrcConnection conn = new("127.0.0.1", 6667, tls: false, "nick", "#c", _ => { });
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => conn.SendChannelAsync("hello\nPRIVMSG #other :x", CancellationToken.None));
    }
}
