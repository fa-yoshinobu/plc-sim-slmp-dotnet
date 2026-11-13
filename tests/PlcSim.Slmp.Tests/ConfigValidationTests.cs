using System.Text.Json;
using PlcSim.Slmp.Core.Config;
using PlcSim.Slmp.Scripting;

namespace PlcSim.Slmp.Tests;

public sealed class ConfigValidationTests
{
    [Fact]
    public void ReportsAllIndependentFieldErrors()
    {
        var config = new SimulatorConfig();
        config.Plc.Profile = "melsec:unknown";
        config.Server.TcpPort = -1;
        config.Server.BindAddress = "invalid";
        config.Plc.Units = ["xyz", "100"];
        config.Plc.Networks = [0, 240];
        config.Api.BindAddress = "0.0.0.0";
        var errors = config.Validate();
        foreach (var field in new[] { "plc.profile", "server.tcpPort", "server.bindAddress", "plc.units", "plc.networks", "api.token" })
            Assert.Contains(errors, e => e.Field == field);
        Assert.Throws<ConfigValidationException>(() => new SimulatorRuntime(config, null));
    }

    [Theory]
    [InlineData("{\"plc\":null}")]
    [InlineData("{\"plc\":{\"units\":[\"0\",\"1\",\"2\",\"3\",\"4\"]}}")]
    [InlineData("{\"plc\":{\"devicePoints\":{\"UNKNOWN\":10}}}")]
    [InlineData("{\"plc\":{\"devicePoints\":{\"D\":4294967295}}}")]
    [InlineData("{\"plc\":{\"devicePoints\":{\"D\":0}}}")]
    [InlineData("{\"initialValues\":[{\"address\":\"D99999999\",\"type\":\"U16\",\"values\":[1]}]}")]
    [InlineData("{\"initialValues\":[{\"address\":\"D0\",\"type\":\"U16\",\"values\":[70000]}]}")]
    [InlineData("{\"initialValues\":[{\"address\":\"D0\",\"type\":\"WRONG\",\"values\":[1]}]}")]
    public void InvalidConfigurationReturnsErrors(string json)
    {
        var config = JsonSerializer.Deserialize<SimulatorConfig>(json, SimulatorConfig.JsonOptions)!;
        Assert.NotEmpty(config.Validate());
    }

    [Theory]
    [InlineData("{\"globals\":null}", "globals")]
    [InlineData("{\"structs\":null}", "structs")]
    [InlineData("{\"globals\":[null]}", "グローバルラベル[0]")]
    [InlineData("{\"structs\":{\"Recipe\":null}}", "Recipe")]
    [InlineData("{\"structs\":{\"Recipe\":[null]}}", "Recipe")]
    public void NullLabelDefinitionsReturnConfigurationErrors(string labels, string field)
    {
        var config = JsonSerializer.Deserialize<SimulatorConfig>(
            "{\"plc\":{\"labels\":" + labels + "}}", SimulatorConfig.JsonOptions)!;
        Assert.Contains(config.Plc.Labels!.Validate(), error => error.Contains(field, StringComparison.Ordinal));
        var issue = Assert.Single(config.Validate());
        Assert.Equal("plc.labels", issue.Field);
        Assert.Contains(field, issue.Reason);
        Assert.Contains("null", issue.Reason);
        Assert.Throws<ConfigValidationException>(() => new SimulatorRuntime(config, null));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public void OptionalAndEmptyLabelDefinitionsRemainValid(string labels)
    {
        var config = JsonSerializer.Deserialize<SimulatorConfig>(
            "{\"plc\":{\"labels\":" + labels + "}}", SimulatorConfig.JsonOptions)!;
        Assert.Empty(config.Validate());
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("65536")]
    [InlineData("-1")]
    public void BadPortIncludesFieldAndValue(string value)
    {
        var ex = Assert.Throws<ConfigValidationException>(() => SimulatorConfig.ParsePort("--port", value));
        Assert.Equal(new ConfigIssue("--port", value, "1〜65535の整数を指定してください"), Assert.Single(ex.Issues));
    }

    [Fact]
    public void SaveIsReadableAndDoesNotOverwriteOnValidationFailure()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "config.json");
            var config = new SimulatorConfig();
            config.Save(path);
            Assert.Empty(SimulatorConfig.Load(path).Validate());
            var original = File.ReadAllText(path);
            Assert.Contains("docs/CONFIGURATION.md", original);
            config.Plc.Profile = "unknown";
            Assert.Throws<ConfigValidationException>(() => config.Save(path));
            Assert.Equal(original, File.ReadAllText(path));
            Assert.Single(directory.GetFiles());
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void SignedHexAndBooleanInitialValuesKeepTheirValue()
    {
        var config = JsonSerializer.Deserialize<SimulatorConfig>("""
            {"initialValues":[
              {"address":"D0","type":"S16","values":["0xFFFF"]},
              {"address":"D2","type":"S32","values":["0xFFFFFFFF"]},
              {"address":"M0","type":"BIT","values":["TRUE"]}
            ]}
            """, SimulatorConfig.JsonOptions)!;
        Assert.Empty(config.Validate());
        var controller = new PlcSim.Slmp.Core.Control.PlcController(config.CreatePlc());
        Assert.Equal(ushort.MaxValue, controller.ReadWords("D0", 1)[0]);
        Assert.All(controller.ReadWords("D2", 2), word => Assert.Equal(ushort.MaxValue, word));
        Assert.True(controller.ReadBits("M0", 1)[0]);
    }

    [Fact]
    public void SaveFailureIncludesDestinationAndPreservesExistingFile()
    {
        var directory = Directory.CreateTempSubdirectory();
        var path = Path.Combine(directory.FullName, "config.json");
        try
        {
            File.WriteAllText(path, "original");
            File.SetAttributes(path, FileAttributes.ReadOnly);
            var ex = Assert.Throws<IOException>(() => new SimulatorConfig().Save(path));
            Assert.Contains(path, ex.Message);
            Assert.Equal("original", File.ReadAllText(path));
            Assert.Single(directory.GetFiles());
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task RebuildRejectsInvalidConfigurationBeforeReplacingRuntime()
    {
        await using var runtime = new SimulatorRuntime(new SimulatorConfig(), null);
        var plc = runtime.Plc;
        var invalid = new SimulatorConfig();
        invalid.Plc.Profile = "unknown";
        await Assert.ThrowsAsync<ConfigValidationException>(() => runtime.RebuildAsync(invalid));
        Assert.Same(plc, runtime.Plc);
    }
}
