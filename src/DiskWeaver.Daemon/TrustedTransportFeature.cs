namespace DiskWeaver.Daemon;

// Marker set on every connection accepted by the Unix socket listener specifically (see
// Program.cs's ListenUnixSocket(...).Use(...) wiring) -- not by inspecting the connection
// afterwards. An earlier version tried to tell the two listeners apart post hoc via
// Features.Get<IConnectionEndPointFeature>()?.LocalEndPoint, which turned out to not even be
// populated by this Kestrel version's socket transport (confirmed by probing a real connection:
// it was entirely absent from the feature collection) -- that check was silently always false,
// so it was rejecting the trusted Cockpit path outright rather than only rejecting the untrusted
// one. Tagging the connection at accept-time, from the one place that genuinely knows which
// listener it came from, doesn't depend on guessing which feature/property a given Kestrel
// version happens to expose.
public sealed class TrustedTransportFeature;
