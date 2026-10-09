using System.Net.Sockets;
using System.Text;
using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Protocol;

namespace PlcSim.Slmp.Tests;

/// <summary>手組みフレームでの要求/応答バイト列の確認（ASCII はクライアント非対応のためここで確認）。</summary>
public sealed class FrameTests
{
    private static (SlmpEngine Engine, SlmpSession Session) NewEngine(string profile = CpuProfiles.IqR)
        => (new SlmpEngine(new PlcModel(CpuProfiles.Get(profile))), new SlmpSession("test", "TEST"));

    private static byte[] Process(SlmpEngine engine, SlmpSession session, byte[] frame)
        => engine.Process(FrameCodec.Parse(frame), session).Frame!;

    [Fact]
    public void Binary3EReadWordsReturnsSameRouteAndData()
    {
        var (engine, session) = NewEngine();
        engine.Plc.Memory.WriteWords(DeviceCatalog.FindByName("D")!, 100, new ushort[] { 0x1234, 0x5678 }, "test");

        // 3E, 自局, 監視タイマ 0010, 0401/0000, D100 2 点（Q 互換形式）
        var request = Convert.FromHexString("500000FFFF03000C00100001040000640000A80200");
        var response = Process(engine, session, request);

        Assert.Equal(Convert.FromHexString("D00000FFFF030006000000341278 56".Replace(" ", string.Empty)), response);
    }

    [Fact]
    public void Binary4EEchoesSerial()
    {
        var (engine, session) = NewEngine();
        // 4E serial=ABCD, 0101/0000 形名読出し
        var request = Convert.FromHexString("5400CDAB000000FFFF03000600100001010000");
        var response = Process(engine, session, request);

        Assert.Equal(new byte[] { 0xD4, 0x00, 0xCD, 0xAB, 0x00, 0x00 }, response[..6]);
        Assert.Equal("R08CPU          ", Encoding.ASCII.GetString(response, 15, 16));
        Assert.Equal(new byte[] { 0x01, 0x48 }, response[31..33]);
    }

    [Fact]
    public void MonitorExecuteAcceptsIqrSubcommand()
    {
        // 実機（iQ-R 系）は 0801/0002 で登録したモニタを 0802/0002 でも返す。
        // iQ-R 形式に対応しない Q 系の機種では 0802/0002 を C059 で拒否する（Q 実機では未確認）。
        var (engine, session) = NewEngine();
        engine.Plc.Memory.WriteWords(DeviceCatalog.FindByName("D")!, 100, new ushort[] { 0x1234 }, "test");
        var register = Convert.FromHexString("500000FFFF03000E00100001080200010064000000A800");
        Assert.Equal(Convert.FromHexString("D00000FFFF030002000000"), Process(engine, session, register));
        var execute = Convert.FromHexString("500000FFFF030006001000020802 00".Replace(" ", string.Empty));
        Assert.Equal(Convert.FromHexString("D00000FFFF0300040000003412"), Process(engine, session, execute));

        var (qEngine, qSession) = NewEngine(CpuProfiles.QnUQj71E71100);
        var qRegister = Convert.FromHexString("500000FFFF03000C001000010800000100640000A8");
        Process(qEngine, qSession, qRegister);
        var qResponse = Process(qEngine, qSession, execute);
        Assert.Equal(new byte[] { 0x59, 0xC0 }, qResponse[9..11]);
    }

    [Fact]
    public void ErrorResponseCarriesErrorInformation()
    {
        var (engine, session) = NewEngine();
        // 未対応コマンド 0E30
        var request = Convert.FromHexString("500000FFFF03000600100030 0E0000".Replace(" ", string.Empty));
        var response = Process(engine, session, request);

        Assert.Equal(Convert.FromHexString("D00000FFFF03000B0059C000FFFF0300300E0000"), response);
    }

    [Fact]
    public void AsciiQFormatReadAndWrite()
    {
        var (engine, session) = NewEngine();
        var write = Encoding.ASCII.GetBytes("500000FF03FF000020001014010000D*0001000002ABCD1234");
        Assert.Equal("D00000FF03FF0000040000", Encoding.ASCII.GetString(Process(engine, session, write)));

        var read = Encoding.ASCII.GetBytes("500000FF03FF000018001004010000D*0001000002");
        Assert.Equal("D00000FF03FF00000C0000ABCD1234", Encoding.ASCII.GetString(Process(engine, session, read)));

        var bits = Encoding.ASCII.GetBytes("500000FF03FF000018001004010001M*0000000003");
        Assert.Equal("D00000FF03FF0000070000000", Encoding.ASCII.GetString(Process(engine, session, bits)));
    }

    [Fact]
    public void AsciiIqrFormatAnd4E()
    {
        var (engine, session) = NewEngine();
        engine.Plc.Memory.WriteWords(DeviceCatalog.FindByName("W")!, 0x1F, new ushort[] { 0xBEEF }, "test");
        var read = Encoding.ASCII.GetBytes("54000012000000FF03FF00001C0010040100 02W***0000001F0001".Replace(" ", string.Empty));
        Assert.Equal("D40000120000" + "00FF03FF00" + "0008" + "0000BEEF", Encoding.ASCII.GetString(Process(engine, session, read)));
    }

    [Fact]
    public void TrailingDataIsRejectedWithLengthMismatch()
    {
        var (engine, session) = NewEngine();
        var request = Convert.FromHexString("500000FFFF03000D00100001040000640000A8020000");
        var response = Process(engine, session, request);
        Assert.Equal(new byte[] { 0x61, 0xC0 }, response[9..11]); // 実機計測: バイナリのデータ長の誤りは C061
    }

    [Fact]
    public void FrameLengthNeedsMoreDataUntilComplete()
    {
        var request = Convert.FromHexString("500000FFFF03000C00100001040000640000A80200");
        Assert.Equal(0, FrameCodec.GetFrameLength(request.AsSpan(0, 5)));
        Assert.Equal(0, FrameCodec.GetFrameLength(request.AsSpan(0, 2)));
        Assert.Equal(request.Length, FrameCodec.GetFrameLength(request));
        Assert.Throws<SlmpFrameFormatException>(() => FrameCodec.GetFrameLength(new byte[] { 0x12, 0x34 }));
    }

    [Fact]
    public async Task TcpHandlesSplitAndCoalescedFrames()
    {
        await using var server = await TestServer.StartAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", server.Server.TcpPort);
        var stream = tcp.GetStream();
        var request = Convert.FromHexString("500000FFFF03000C00100001040000640000A80100");

        await stream.WriteAsync(request.AsMemory(0, 4));
        await Task.Delay(50);
        await stream.WriteAsync(request.AsMemory(4).ToArray().Concat(request).ToArray());

        var expected = Convert.FromHexString("D00000FFFF0300040000000000");
        var buffer = new byte[expected.Length * 2];
        var total = 0;
        while (total < buffer.Length)
            total += await stream.ReadAsync(buffer.AsMemory(total));
        Assert.Equal(expected, buffer[..expected.Length]);
        Assert.Equal(expected, buffer[expected.Length..]);
    }

    [Fact]
    public void ErrorInjectionOverridesEndCode()
    {
        var (engine, session) = NewEngine();
        engine.Injector.Add(new ErrorInjectionRule { Command = 0x0401, EndCode = 0xC051, RemainingCount = 1 });
        var request = Convert.FromHexString("500000FFFF03000C00100001040000640000A80100");

        Assert.Equal(new byte[] { 0x51, 0xC0 }, Process(engine, session, request)[9..11]);
        Assert.Equal(new byte[] { 0x00, 0x00 }, Process(engine, session, request)[9..11]);
    }
}
