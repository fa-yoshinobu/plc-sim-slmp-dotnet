using System.Net;
using System.Net.Sockets;
using System.Text;
using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Protocol;
using PlcSim.Slmp.Core.Server;

namespace PlcSim.Slmp.Tests;

/// <summary>
/// 上限・不正入力・書込み失敗時の扱い。
/// ASCII のビット一括の上限、ラベル番号の桁あふれ、書込み失敗時に値を変えないこと、大きな応答の拒否、不正な要求の後も通信が続くこと。
/// </summary>
public sealed class RobustnessTests
{
    private static SlmpEngine NewEngine(LabelDefinitions? labels = null)
        => new(new PlcModel(CpuProfiles.Get(CpuProfiles.IqR), null, labels));

    private static byte[] Process(SlmpEngine engine, byte[] frame)
        => engine.Process(FrameCodec.Parse(frame), new SlmpSession("t", "T")).Frame!;

    private static byte[] Ascii3E(string body) => Encoding.ASCII.GetBytes("500000FF03FF00" + body.Length.ToString("X4") + body);

    private static byte[] Ascii4E(string body) => Encoding.ASCII.GetBytes("54001234000000FF03FF00" + body.Length.ToString("X4") + body);

    private static string AsciiEndCode(byte[] response) => Encoding.ASCII.GetString(response, response[1] == (byte)'4' ? 26 : 18, 4);

    /// <summary>3E バイナリ（本文は監視タイマ・コマンド・サブコマンド・データ）。</summary>
    private static byte[] Bin(byte[] body)
    {
        var frame = new List<byte> { 0x50, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00 };
        frame.AddRange(BitConverter.GetBytes((ushort)body.Length));
        frame.AddRange(body);
        return frame.ToArray();
    }

    private static ushort BinEndCode(byte[] response) => (ushort)(response[9] | (response[10] << 8));

    private static byte[] LabelRandomRead(params string[] names)
    {
        var body = new List<byte> { 0x10, 0x00, 0x1C, 0x04, 0x00, 0x00 };
        body.AddRange(BitConverter.GetBytes((ushort)names.Length));
        body.AddRange(BitConverter.GetBytes((ushort)0));
        foreach (var name in names)
        {
            body.AddRange(BitConverter.GetBytes((ushort)name.Length));
            body.AddRange(Encoding.Unicode.GetBytes(name));
        }

        return Bin(body.ToArray());
    }

    // ---- ASCII のビット一括の上限（バイナリ 7168 点、ASCII 3584 点） ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AsciiBitBatchIsLimitedTo3584Points(bool frame4E)
    {
        var engine = NewEngine();
        var frame = frame4E ? (Func<string, byte[]>)Ascii4E : Ascii3E;
        Assert.Equal("0000", AsciiEndCode(Process(engine, frame("001004010001M*000000" + 3584.ToString("X4")))));
        Assert.Equal("C052", AsciiEndCode(Process(engine, frame("001004010001M*000000" + 3585.ToString("X4")))));
        Assert.Equal("0000", AsciiEndCode(Process(engine, frame("001014010001M*000000" + 3584.ToString("X4") + new string('1', 3584)))));
        Assert.Equal("C052", AsciiEndCode(Process(engine, frame("001014010001M*000000" + 3585.ToString("X4") + new string('1', 3585)))));
        Assert.False(engine.Plc.Memory.ReadBits(DeviceCatalog.FindByName("M")!, 3584, 1)[0]);
    }

    [Fact]
    public void BinaryBitBatchKeeps7168Points()
    {
        var engine = NewEngine();
        byte[] Read(int points) => Bin(new byte[] { 0x10, 0x00, 0x01, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x90, (byte)points, (byte)(points >> 8) });
        Assert.Equal(0x0000, BinEndCode(Process(engine, Read(7168))));
        Assert.Equal(EndCodes.PointsOverC052, BinEndCode(Process(engine, Read(7169))));
    }

    // ---- ラベル番号の桁あふれ ----

    [Theory]
    [InlineData("%2147483648")]
    [InlineData("SLMP_TEST.WordArray[2147483648]")]
    public void HugeLabelNumbersReturnErrorResponse(string name)
    {
        var response = Process(NewEngine(), LabelRandomRead(name));
        Assert.Equal(EndCodes.LabelNotFound, BinEndCode(response));
    }

    [Fact]
    public void HugeStringLengthIsInvalidLabelType()
    {
        var defs = new LabelDefinitions { Globals = { new("S", "STRING(99999999999)") } };
        Assert.Contains(defs.Validate(), e => e.Contains("未定義", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UdpKeepsAnsweringAfterInvalidRequests()
    {
        var labels = new LabelDefinitions { Globals = { new("BIG", "WORD", 32768) } };
        await using var server = new SlmpServer(NewEngine(labels), new SlmpServerOptions { TcpPort = 0, UdpPort = 0 });
        await server.StartAsync();

        async Task<byte[]?> Udp(byte[] frame)
        {
            using var client = new UdpClient();
            await client.SendAsync(frame, new IPEndPoint(IPAddress.Loopback, server.UdpPort));
            using var timeout = new CancellationTokenSource(1000);
            try
            {
                return (await client.ReceiveAsync(timeout.Token)).Buffer;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        var readD100 = Bin(new byte[] { 0x10, 0x00, 0x01, 0x04, 0x00, 0x00, 0x64, 0x00, 0x00, 0xA8, 0x01, 0x00 });
        Assert.Equal(EndCodes.LabelNotFound, BinEndCode((await Udp(LabelRandomRead("%2147483648")))!));
        Assert.Equal(EndCodes.RequestContentError, BinEndCode((await Udp(LabelRandomRead("BIG")))!));
        Assert.Equal(0x0000, BinEndCode((await Udp(readD100))!));
        Assert.True(server.IsRunning);
    }

    // ---- 書込み失敗時は何も書かない ----

    [Fact]
    public void FailedRandomWriteChangesNothing()
    {
        var engine = NewEngine();
        var accessed = 0;
        engine.Accessed += _ => accessed++;
        // 1402/0000: D100=BEEF、D262144（範囲外）=1234
        var response = Process(engine, Bin(Convert.FromHexString("1000" + "0214" + "0000" + "0200" + "640000A8EFBE" + "000004A83412")));
        Assert.Equal(EndCodes.DeviceOutOfRange, BinEndCode(response));
        Assert.Equal(0, engine.Plc.Memory.ReadWords(DeviceCatalog.FindByName("D")!, 100, 1)[0]);
        Assert.Equal(0, accessed);
    }

    [Fact]
    public void FailedRandomBitWriteChangesNothing()
    {
        var engine = NewEngine();
        // 1402/0001: M10=ON、M262144（範囲外）=ON
        var response = Process(engine, Bin(Convert.FromHexString("1000" + "0214" + "0100" + "02" + "0A000090" + "01" + "00000490" + "01")));
        Assert.Equal(EndCodes.DeviceOutOfRange, BinEndCode(response));
        Assert.False(engine.Plc.Memory.ReadBits(DeviceCatalog.FindByName("M")!, 10, 1)[0]);
    }

    [Fact]
    public void FailedBlockWriteChangesNothing()
    {
        var engine = NewEngine();
        // 1406/0000: D100 1 点、D262143 から 2 点（範囲外）
        var response = Process(engine, Bin(Convert.FromHexString("1000" + "0614" + "0000" + "0200" + "640000A8" + "0100" + "3412" + "FFFF03A8" + "0200" + "11112222")));
        Assert.Equal(EndCodes.DeviceOutOfRange, BinEndCode(response));
        Assert.Equal(0, engine.Plc.Memory.ReadWords(DeviceCatalog.FindByName("D")!, 100, 1)[0]);
    }

    [Fact]
    public void FailedExtendUnitWriteChangesNothing()
    {
        var engine = NewEngine();
        // 1601/0000: U0 のバイト番地 1FFFFF から 3 バイト（最後の 2 バイトは範囲外）
        var response = Process(engine, Bin(Convert.FromHexString("1000" + "0116" + "0000" + "FFFF1F00" + "0300" + "0000" + "AABBCC")));
        Assert.Equal(EndCodes.DeviceOutOfRange, BinEndCode(response));
        Assert.Equal(0, engine.Plc.Buffers.Module(0).Read(0xFFFFF, 1)[0]);
    }

    [Fact]
    public void FailedArrayLabelWriteChangesNothing()
    {
        var labels = new LabelDefinitions { Globals = { new("A", "WORD", 2), new("B", "WORD", 2) } };
        var engine = NewEngine(labels);
        var body = new List<byte> { 0x10, 0x00, 0x1A, 0x14, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00 };
        void Item(string name, byte[] data)
        {
            body.AddRange(BitConverter.GetBytes((ushort)name.Length));
            body.AddRange(Encoding.Unicode.GetBytes(name));
            body.Add(1); // バイト単位
            body.Add(0);
            body.AddRange(BitConverter.GetBytes((ushort)data.Length));
            body.AddRange(data);
        }

        Item("A[0]", new byte[] { 0x34, 0x12, 0x78, 0x56 });
        Item("B[1]", new byte[] { 1, 0, 2, 0 }); // B[1] から 4 バイトは配列範囲外
        var response = Process(engine, Bin(body.ToArray()));
        Assert.Equal(EndCodes.RequestContentError, BinEndCode(response));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, engine.Plc.Labels.Read(engine.Plc.Labels.Resolve("A"), 4));
    }

    [Fact]
    public void FailedLinkDirectBitWriteChangesNothing()
    {
        // J1\X の末尾をまたぐ 16 点の書込み（後半 8 点が範囲外）
        var controller = new PlcSim.Slmp.Core.Control.PlcController(new PlcModel(CpuProfiles.Get(CpuProfiles.IqR)));
        Assert.Throws<SlmpEndCodeException>(() => controller.WriteBits(@"J1\XFFFFF8", Enumerable.Repeat(true, 16).ToArray(), "test"));
        Assert.All(controller.ReadBits(@"J1\XFFFFF8", 8), Assert.False);
        Assert.Throws<SlmpEndCodeException>(() => controller.WriteWords(@"J1\XFFFFF8", new ushort[] { 0xFFFF }, "test"));
        Assert.All(controller.ReadBits(@"J1\XFFFFF8", 8), Assert.False);
    }

    // ---- 1 回の応答で返せない大きさ ----

    [Fact]
    public void OversizedLabelReadsAreRejected()
    {
        var labels = new LabelDefinitions { Globals = { new("BIG", "WORD", 32768), new("HALF1", "WORD", 20000), new("HALF2", "WORD", 20000), new("SMALL", "WORD", 100) } };
        var engine = NewEngine(labels);
        Assert.Equal(EndCodes.RequestContentError, BinEndCode(Process(engine, LabelRandomRead("BIG"))));
        Assert.Equal(EndCodes.RequestContentError, BinEndCode(Process(engine, LabelRandomRead("HALF1", "HALF2"))));
        Assert.Equal(0x0000, BinEndCode(Process(engine, LabelRandomRead("SMALL"))));
    }

    // ---- 応答の大きさは組み立てる前に確かめる・UDP で送れる長さ ----

    private static async Task<(SlmpServer Server, Func<byte[], Task<byte[]?>> Udp)> StartUdpAsync(SlmpEngine engine)
    {
        var server = new SlmpServer(engine, new SlmpServerOptions { TcpPort = 0, UdpPort = 0 });
        await server.StartAsync();
        async Task<byte[]?> Udp(byte[] frame)
        {
            using var client = new UdpClient();
            await client.SendAsync(frame, new IPEndPoint(IPAddress.Loopback, server.UdpPort));
            using var timeout = new CancellationTokenSource(1000);
            try
            {
                return (await client.ReceiveAsync(timeout.Token)).Buffer;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return (server, Udp);
    }

    [Fact]
    public void OversizedLabelReadIsRejectedBeforeBuildingResponse()
    {
        // WORD[32767]（65,534 バイト）を 512 回指定: 応答を組み立てていれば 32 MB 以上を確保する
        var engine = NewEngine(new LabelDefinitions { Globals = { new("A", "WORD", 32767) } });
        var request = LabelRandomRead(Enumerable.Repeat("A", 512).ToArray());
        Process(engine, request); // 初回の準備（JIT など）を除く
        var before = GC.GetAllocatedBytesForCurrentThread();
        var response = Process(engine, request);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(EndCodes.RequestContentError, BinEndCode(response));
        Assert.True(allocated < 4 * 1024 * 1024, $"確保量 {allocated} バイト");
    }

    [Fact]
    public async Task UdpRejectsResponsesLargerThanOneDatagram()
    {
        var engine = NewEngine(new LabelDefinitions { Globals = { new("NEAR", "WORD", 32750), new("FITS", "WORD", 32000) } });
        var (server, udp) = await StartUdpAsync(engine);
        await using (server)
        {
            // 65,500 バイト + ヘッダ: データ長欄には収まるが UDP の 1 データグラム（65,507 バイト）を超える
            Assert.Equal(EndCodes.RequestContentError, BinEndCode((await udp(LabelRandomRead("NEAR")))!));
            Assert.Equal(0x0000, BinEndCode((await udp(LabelRandomRead("FITS")))!));
            Assert.Equal(0x0000, BinEndCode((await udp(Bin(new byte[] { 0x10, 0x00, 0x01, 0x04, 0x00, 0x00, 0x64, 0x00, 0x00, 0xA8, 0x01, 0x00 })))!));
        }

        // TCP では同じ読出しが成功する
        Assert.Equal(0x0000, BinEndCode(Process(engine, LabelRandomRead("NEAR"))));
    }

    [Fact]
    public async Task UnexpectedErrorIsCountedOnceAndServerContinues()
    {
        var engine = NewEngine();
        var thrown = false;
        engine.Accessed += _ =>
        {
            if (!thrown)
            {
                thrown = true;
                throw new InvalidOperationException("テスト用の想定外エラー");
            }
        };
        var (server, udp) = await StartUdpAsync(engine);
        await using (server)
        {
            var readD100 = Bin(new byte[] { 0x10, 0x00, 0x01, 0x04, 0x00, 0x00, 0x64, 0x00, 0x00, 0xA8, 0x01, 0x00 });
            Assert.Null(await udp(readD100));
            Assert.Equal(0x0000, BinEndCode((await udp(readD100))!));
            Assert.Equal(2, engine.RequestCount);
            Assert.True(server.IsRunning);
        }
    }
}
