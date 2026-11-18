namespace KeyPassXCCloudSync;

// Protects entire Proton remote-store lifetimes, not individual CLI processes.
// packaged Proton transport, interactive login or another update.
internal static class ProtonCliMaintenance
{
    internal static IDisposable BeginUse()=>KeePassXC.CloudTransport.ProtonCliLease.BeginUse();
    internal static IDisposable? TryBeginUpdate()=>KeePassXC.CloudTransport.ProtonCliLease.TryBeginUpdate();
}
