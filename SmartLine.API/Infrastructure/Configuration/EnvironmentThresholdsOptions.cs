namespace SmartLine.API.Infrastructure.Configuration;

public class EnvironmentThresholdsOptions
{
    public const string SectionName = "EnvironmentThresholds";
    public decimal HighTempThreshold { get; set; } = 35;
    public decimal AlarmTempThreshold { get; set; } = 40;
    public decimal FanHysteresisCelsius { get; set; } = 2;
}
