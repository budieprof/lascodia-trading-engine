using Lascodia.Trading.Engine.SharedApplication.Common.Models;

namespace LascodiaTradingEngine.Application.Common.Options;

public class IntradayAttributionOptions : ConfigurationOption<IntradayAttributionOptions>
{
    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 3600;
}
