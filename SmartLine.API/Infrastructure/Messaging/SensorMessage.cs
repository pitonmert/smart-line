namespace SmartLine.API.Infrastructure.Messaging;

public sealed record SensorMessage(string Topic, string Payload);
