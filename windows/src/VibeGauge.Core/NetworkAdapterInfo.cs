namespace VibeGauge.Core;

public sealed record NetworkAdapterInfo(string Name, string Kind, string Addresses, string Gateway,
    string Dns, double ReceiveBytesPerSecond, double SendBytesPerSecond);
