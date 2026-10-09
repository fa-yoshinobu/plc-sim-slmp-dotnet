using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Protocol;
using PlcComm.Slmp;

namespace PlcSim.Slmp.Tests;

/// <summary>iQ-R プロファイルに PlcComm.Slmp から実機同様の手順でアクセスする統合テスト。</summary>
public sealed class IqRClientTests
{
    private const SlmpPlcProfile Profile = SlmpPlcProfile.IqR;

    private static SlmpDeviceAddress Dev(string text) => SlmpAddress.Parse(text, Profile);

    private static SlmpQualifiedDeviceAddress Q(string text) => SlmpQualifiedDeviceParser.Parse(text, Profile);

    [Fact]
    public async Task TypeNameAndCpuState()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync(Profile);

        var type = await client.ReadTypeNameAsync();
        Assert.Equal("R08CPU", type.Model);
        Assert.Equal(0x4801, type.ModelCode);

        Assert.Equal(SlmpCpuOperationStatus.Run, (await client.ReadCpuOperationStateAsync()).Status);
        await client.RemoteStopAsync();
        Assert.Equal(SlmpCpuOperationStatus.Stop, (await client.ReadCpuOperationStateAsync()).Status);
        await client.RemotePauseAsync(SlmpRemoteMode.Normal);
        Assert.Equal(SlmpCpuOperationStatus.Pause, (await client.ReadCpuOperationStateAsync()).Status);
        await client.RemoteRunAsync(SlmpRemoteMode.Normal, SlmpRemoteClearMode.NoClear);
        Assert.Equal(PlcRunState.Run, server.Plc.State);
    }

    [Fact]
    public async Task DeviceRangeCatalogMatchesMeasuredMemoryMap()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync(Profile);

        var catalog = await client.ReadDeviceRangeCatalogAsync();
        uint? Count(string device) => catalog.Entries.Single(e => e.Device == device).PointCount;
        Assert.Equal(0x3000u, Count("X"));
        Assert.Equal(262144u, Count("M"));
        Assert.Equal(0x40000u, Count("B"));
        Assert.Equal(16384u, Count("S"));
        Assert.Equal(262144u, Count("D"));
        Assert.Equal(262144u, Count("LTN"));
        Assert.Equal(1048576u, Count("RD"));
        Assert.Equal(4915200u, Count("ZR"));
        Assert.Equal(32768u, Count("R"));
    }

    [Fact]
    public async Task MaximumDevicesRoundTripAndOutOfRangeFails()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync(Profile);

        var values = Enumerable.Range(1, 16).Select(static v => (ushort)(v * 1000)).ToArray();
        await client.WriteWordsAsync(Dev("D262128"), values);
        Assert.Equal(values, await client.ReadWordsAsync(Dev("D262128"), 16));

        var bits = Enumerable.Repeat(true, 16).ToArray();
        await client.WriteBitsAsync(Dev("M262128"), bits);
        Assert.Equal(bits, await client.ReadBitsAsync(Dev("M262128"), 16));

        await client.WriteWordsAsync(Dev("ZR4915184"), values);
        Assert.Equal(values, await client.ReadWordsAsync(Dev("ZR4915184"), 16));

        var error = await Assert.ThrowsAsync<SlmpError>(() => client.ReadWordsAsync(Dev("D262143"), 2));
        Assert.Equal(EndCodes.DeviceOutOfRange, error.EndCode);
    }

    [Fact]
    public async Task SharedMemoryMatchesRealPlc()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync(Profile);

        // DX/DY は X/Y、R は ZR の先頭と同じメモリ
        await client.WriteBitsAsync(Dev("DX1F"), new[] { true });
        Assert.Equal(new[] { true }, await client.ReadBitsAsync(Dev("X1F"), 1));
        await client.WriteBitsAsync(Dev("Y20"), new[] { true });
        Assert.Equal(new[] { true }, await client.ReadBitsAsync(Dev("DY20"), 1));
        await client.WriteWordsAsync(Dev("R100"), new ushort[] { 4321 });
        Assert.Equal(new ushort[] { 4321 }, await client.ReadWordsAsync(Dev("ZR100"), 1));
        var error = await Assert.ThrowsAsync<SlmpError>(() => client.ReadWordsAsync(Dev("R32767"), 2));
        Assert.Equal(EndCodes.DeviceOutOfRange, error.EndCode);

        // 点数を変えても共有先の点数が揃う
        var profile = CpuProfiles.Get(CpuProfiles.IqR).With(pointOverrides: new Dictionary<string, uint> { ["X"] = 1024, ["ZR"] = 1000 });
        Assert.Equal(1024u, profile.PointsOf("DX"));
        Assert.Equal(1000u, profile.PointsOf("R"));
    }

    [Fact]
    public async Task StepRelayIsReadOnly()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync(Profile);

        Assert.Equal(new bool[16], await client.ReadBitsAsync(Dev("S16368"), 16));
        // クライアントは S への書込みを送信前に拒否するため、生の 1402/0003 で送る。
        var error = await Assert.ThrowsAsync<SlmpError>(() => client.RawCommandAsync(SlmpCommand.DeviceWriteRandom, 0x0003, new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x98, 0x00, 0x01, 0x00 }));
        Assert.Equal(EndCodes.DeviceAccessError, error.EndCode);
    }

    [Fact]
    public async Task DWordFloatRandomBlockAndMonitor()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync(Profile);

        await client.WriteDWordsAsync(Dev("D200"), new uint[] { 0x12345678, 0xCAFEBABE });
        Assert.Equal(new uint[] { 0x12345678, 0xCAFEBABE }, await client.ReadDWordsAsync(Dev("D200"), 2));
        await client.WriteFloat32sAsync(Dev("D300"), new[] { 1.5f });
        Assert.Equal(1.5f, (await client.ReadFloat32sAsync(Dev("D300"), 1))[0]);

        await client.WriteRandomWordsAsync(new[] { (Dev("D10"), (ushort)11), (Dev("W1F"), (ushort)22) }, new[] { (Dev("D20"), 0x00010002u) });
        var (words, dwords) = await client.ReadRandomAsync(new[] { Dev("D10"), Dev("W1F") }, new[] { Dev("D20") });
        Assert.Equal(new ushort[] { 11, 22 }, words);
        Assert.Equal(new uint[] { 0x00010002 }, dwords);

        await client.WriteRandomBitsAsync(new[] { (Dev("M5"), true), (Dev("Y1F"), true) });
        Assert.Equal(new[] { true }, await client.ReadBitsAsync(Dev("M5"), 1));

        await client.WriteBlockAsync(
            new[] { new SlmpBlockWrite(Dev("D500"), new ushort[] { 1, 2, 3 }) },
            new[] { new SlmpBlockWrite(Dev("M1600"), new ushort[] { 0x8001 }) });
        var block = await client.ReadBlockAsync(
            new[] { new SlmpBlockRead(Dev("D500"), 3) },
            new[] { new SlmpBlockRead(Dev("M1600"), 1) });
        Assert.Equal(new ushort[] { 1, 2, 3 }, block.WordValues);
        Assert.Equal(new ushort[] { 0x8001 }, block.BitWordValues);
        Assert.Equal(new[] { true, false }, await client.ReadBitsAsync(Dev("M1600"), 2));

        await client.RegisterMonitorDevicesAsync(new[] { Dev("D10") }, new[] { Dev("D20") });
        await client.WriteWordsAsync(Dev("D10"), new ushort[] { 99 });
        var monitor = await client.RunMonitorCycleAsync(1, 1);
        Assert.Equal(new ushort[] { 99 }, monitor.WordValues);
        Assert.Equal(new uint[] { 0x00010002 }, monitor.DwordValues);
    }

    [Fact]
    public async Task ExtendedDevicesUseModuleBufferAndLinkDirect()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync(Profile);

        await client.WriteWordsExtendedAsync(Q(@"U0\G0"), new ushort[] { 10, 20 });
        Assert.Equal(new ushort[] { 10, 20 }, await client.ReadWordsExtendedAsync(Q(@"U0\G0"), 2));
        // 既定の装着ユニットは U0、ネットワークは J1 だけ
        var noUnit = await Assert.ThrowsAsync<SlmpError>(() => client.ReadWordsExtendedAsync(Q(@"U2\G0"), 1));
        Assert.Equal(EndCodes.UnitNotFound, noUnit.EndCode);
        var noNetwork = await Assert.ThrowsAsync<SlmpError>(() => client.ReadWordsExtendedAsync(Q(@"J2\W0"), 1));
        Assert.Equal(EndCodes.UnitNotFound, noNetwork.EndCode);

        await client.WriteWordsExtendedAsync(Q(@"J1\W10"), new ushort[] { 0x1234 });
        Assert.Equal(new ushort[] { 0x1234 }, await client.ReadWordsExtendedAsync(Q(@"J1\W10"), 1));
        await client.WriteBitsExtendedAsync(Q(@"J1\X10"), new[] { true, false, true });
        Assert.Equal(new[] { true, false, true }, await client.ReadBitsExtendedAsync(Q(@"J1\X10"), 3));

        await client.WriteRandomWordsExtendedAsync(new[] { (Q(@"U3E0\HG5"), (ushort)7) }, Array.Empty<(SlmpQualifiedDeviceAddress, uint)>());
        Assert.Equal(new ushort[] { 7 }, await client.ReadRandomWordsExtendedAsync(new[] { Q(@"U3E0\HG5") }));

        Assert.Equal(10, server.Plc.Buffers.Module(0).Read(0, 1)[0]);
    }

    [Fact]
    public async Task RegisteredUnitsAndNetworksAreAccessible()
    {
        var settings = new PlcSettings { Units = new ushort[] { 0, 1, 2, 0x1F }, Networks = new byte[] { 1, 2 } };
        await using var server = await TestServer.StartAsync(settings: settings);
        await using var client = await server.ConnectAsync(Profile);

        await client.WriteWordsExtendedAsync(Q(@"U1F\G100"), new ushort[] { 31 });
        Assert.Equal(new ushort[] { 31 }, await client.ReadWordsExtendedAsync(Q(@"U1F\G100"), 1));
        await client.WriteWordsExtendedAsync(Q(@"J2\W10"), new ushort[] { 2 });
        Assert.Equal(new ushort[] { 2 }, await client.ReadWordsExtendedAsync(Q(@"J2\W10"), 1));
        Assert.Equal(new ushort[] { 0 }, await client.ReadWordsExtendedAsync(Q(@"J1\W10"), 1));

        // CPU バッファメモリは登録不要
        await client.WriteWordsExtendedAsync(Q(@"U3E0\G0"), new ushort[] { 7 });

        // 拡張ユニット読出し（0601）も同じ制約。終了コードは実機計測どおり 4043。
        var unit3 = await Assert.ThrowsAsync<SlmpError>(() => client.RawCommandAsync(SlmpCommand.ExtendUnitRead, 0, new byte[] { 0, 0, 0, 0, 2, 0, 3, 0 }));
        Assert.Equal(EndCodes.ModuleNotFound, unit3.EndCode);

        // 上限は 4 個
        Assert.Throws<ArgumentException>(() => new PlcSettings { Units = new ushort[] { 0, 1, 2, 3, 4 } }.Validate());
        Assert.Throws<ArgumentException>(() => new PlcSettings { Networks = new byte[] { 1, 2, 3, 4, 5 } }.Validate());
    }

    [Fact]
    public async Task LongTimerStatusBlock()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync(Profile);

        await client.WriteRandomWordsAsync(Array.Empty<(SlmpDeviceAddress, ushort)>(), new[] { (Dev("LTN100"), 123456u) });
        await client.WriteRandomBitsAsync(new[] { (Dev("LTC100"), true), (Dev("LTS101"), true) });

        var timers = await client.ReadLongTimerAsync(100, 2);
        Assert.Equal(123456u, timers[0].CurrentValue);
        Assert.True(timers[0].Coil);
        Assert.False(timers[0].Contact);
        Assert.True(timers[1].Contact);
        Assert.Equal(new[] { true, false }, await client.ReadLtcStatesAsync(100, 2));
        Assert.Equal(123456u, Convert.ToUInt32(await client.ReadTypedAsync("LTN100", "D")));
    }

    [Fact]
    public async Task LabelsMatchGxWorksDefinition()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync(Profile);

        await client.WriteRandomLabelsAsync(new[] { new SlmpLabelRandomWritePoint("SLMP_TEST.WordValue", new byte[] { 0x34, 0x12 }) });
        var random = await client.ReadRandomLabelsAsync(new[] { "%1.WordValue" }, new[] { "SLMP_TEST" });
        Assert.Equal(2, random[0].DataTypeId);
        Assert.Equal(new byte[] { 0x34, 0x12 }, random[0].Data);

        await client.WriteArrayLabelsAsync(new[] { new SlmpLabelArrayWritePoint("SLMP_TEST.WordArray[0]", 1, 4, new byte[] { 1, 0, 2, 0 }) });
        var array = await client.ReadArrayLabelsAsync(new[] { new SlmpLabelArrayReadPoint("SLMP_TEST.WordArray[0]", 1, 4) });
        Assert.Equal(new byte[] { 1, 0, 2, 0 }, array[0].Data);

        var error = await Assert.ThrowsAsync<SlmpError>(() => client.ReadRandomLabelsAsync(new[] { "NO_SUCH_LABEL" }));
        Assert.Equal(EndCodes.LabelNotFound, error.EndCode);
    }

    [Fact]
    public async Task MaintenanceCommands()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync(Profile);

        var echo = await client.SelfTestLoopbackAsync("0123ABCD"u8.ToArray());
        Assert.Equal("0123ABCD"u8.ToArray(), echo);

        server.Plc.SetErrorCode(0x1234);
        Assert.Equal(0x1234, await client.ReadLatestSelfDiagnosisErrorCodeAsync());
        await client.ClearErrorAsync();
        Assert.Equal(0, await client.ReadLatestSelfDiagnosisErrorCodeAsync());

        await client.WriteBitsAsync(Dev("L0"), new[] { true });
        var running = await Assert.ThrowsAsync<SlmpError>(() => client.RemoteLatchClearAsync());
        Assert.Equal(EndCodes.StateConditionError, running.EndCode);
        await client.RemoteStopAsync();
        await client.RemoteLatchClearAsync();
        Assert.Equal(new[] { false }, await client.ReadBitsAsync(Dev("L0"), 1));

        var memory = new byte[] { 0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0xAA, 0x55 };
        await client.RawCommandAsync(SlmpCommand.MemoryWrite, 0, memory);
        var read = await client.RawCommandAsync(SlmpCommand.MemoryRead, 0, memory[..6]);
        Assert.Equal(new byte[] { 0xAA, 0x55 }, read);

        var unitWrite = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0xCD, 0xAB };
        await client.RawCommandAsync(SlmpCommand.ExtendUnitWrite, 0, unitWrite);
        Assert.Equal(new ushort[] { 0xABCD }, await client.ReadWordsExtendedAsync(Q(@"U0\G0"), 1));
    }

    [Fact]
    public async Task UdpTransportWorks()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync(Profile, SlmpTransportMode.Udp);

        await client.WriteWordsAsync(Dev("D1"), new ushort[] { 42 });
        Assert.Equal(new ushort[] { 42 }, await client.ReadWordsAsync(Dev("D1"), 1));
    }

    [Fact]
    public async Task RemotePasswordLocksUntilUnlocked()
    {
        await using var server = await TestServer.StartAsync(settings: new PlcSettings { RemotePassword = "melsec" });
        await using var client = await server.ConnectAsync(Profile);

        var locked = await Assert.ThrowsAsync<SlmpError>(() => client.ReadWordsAsync(Dev("D0"), 1));
        Assert.Equal(EndCodes.RemotePasswordLocked, locked.EndCode);
        var mismatch = await Assert.ThrowsAsync<SlmpError>(() => client.RemotePasswordUnlockAsync("wrong!"));
        Assert.Equal(EndCodes.RemotePasswordMismatch, mismatch.EndCode);

        await client.RemotePasswordUnlockAsync("melsec");
        Assert.Equal(new ushort[] { 0 }, await client.ReadWordsAsync(Dev("D0"), 1));
        await client.RemotePasswordLockAsync("melsec");
        await Assert.ThrowsAsync<SlmpError>(() => client.ReadWordsAsync(Dev("D0"), 1));
    }

    [Fact]
    public async Task ClientWritesRaiseMemoryChangeWithSource()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync(Profile);
        var changes = new List<MemoryChange>();
        server.Plc.Memory.Changed += changes.Add;

        await client.WriteWordsAsync(Dev("D100"), new ushort[] { 5, 6 });

        var change = Assert.Single(changes);
        Assert.Equal("D", change.Device);
        Assert.Equal(100u, change.Start);
        Assert.Equal(2, change.Count);
        Assert.StartsWith("SLMP 127.0.0.1", change.Source);
        lock (server.Plc.SyncRoot)
            Assert.Equal(new ushort[] { 5, 6 }, server.Plc.Memory.ReadWords(DeviceCatalog.FindByName("D")!, 100, 2));
    }
}
