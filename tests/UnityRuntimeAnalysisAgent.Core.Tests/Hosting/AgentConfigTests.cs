using UnityLudometry.Protocol;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.Core.Tests.Hosting;

public sealed class AgentConfigTests
{
    [Fact]
    public void Defaults_have_no_providers_dir_and_read_only_mode()
    {
        var config = AgentConfig.Read(new DictionaryConfigSource());
        Assert.Null(config.ProvidersDir);
        Assert.Equal(AgentMode.ReadOnly, config.Mode);
        Assert.Equal(TransportMode.Auto, config.Transport);
        Assert.Equal(16 * 1024 * 1024, config.MaxFrameBytes);
        Assert.Equal(AgentLogLevel.Info, config.LogLevel);
        Assert.Empty(config.Warnings);
    }

    [Fact]
    public void Values_are_read()
    {
        var config = AgentConfig.Read(new DictionaryConfigSource()
            .Set("discovery.providersdir", " X:/Example/providers ")
            .Set(AgentConfig.ModeKey, "ReadOnly+Load")
            .Set(AgentConfig.TransportModeKey, "TCP")
            .Set(AgentConfig.MaxFrameBytesKey, "2048")
            .Set(AgentConfig.LogLevelKey, "debug"));
        Assert.Equal("X:/Example/providers", config.ProvidersDir);
        Assert.Equal(AgentMode.ReadOnlyLoad, config.Mode);
        Assert.Equal(TransportMode.Tcp, config.Transport);
        Assert.Equal(2048, config.MaxFrameBytes);
        Assert.Equal(AgentLogLevel.Debug, config.LogLevel);
    }

    [Fact]
    public void Invalid_values_fall_back_to_safe_defaults_with_warnings()
    {
        var config = AgentConfig.Read(new DictionaryConfigSource()
            .Set(AgentConfig.ModeKey, "Admin")
            .Set(AgentConfig.TransportModeKey, "carrier-pigeon")
            .Set(AgentConfig.MaxFrameBytesKey, "12")
            .Set(AgentConfig.LogLevelKey, "7"));
        Assert.Equal(AgentMode.ReadOnly, config.Mode);
        Assert.Equal(TransportMode.Auto, config.Transport);
        Assert.Equal(16 * 1024 * 1024, config.MaxFrameBytes);
        Assert.Equal(AgentLogLevel.Info, config.LogLevel);
        Assert.Equal(4, config.Warnings.Count);
    }
}
