# Security

This page documents the implemented Phase0 web deployment profiles and their security boundaries. It does not describe a Portal administration application; a private Portal does not exist in Phase0.

## Deployment Profiles

`[WebFrontend] DeploymentProfile=Legacy` is the compatibility profile. It serves the game login and MTX routes and, when enabled, the legacy web API, status and metrics endpoints, and dashboard.

`[WebFrontend] DeploymentProfile=Portal` serves only the game login routes (`/Login/IndexPB` and `/AuthServer/Login/IndexPB`) and the MTX routes. During Phase0, Portal returns `404 Not Found` for all legacy administrative, status, metrics, and dashboard routes, including `/AccountManagement/*`, `/ServerStatus`, `/RegionReport`, `/Metrics/Performance`, `/`, and `/Dashboard/`. Portal does not load legacy web API keys, which are stored as plaintext bearer tokens in `Data/Web/ApiKeys.json` when Legacy is used.

Use Portal for an internet-facing auth endpoint. Use Legacy only when its local administration and monitoring routes are required and are protected by the deployment network.

## Reverse Proxies

The web frontend uses `X-Forwarded-For` only when the direct TCP peer is in `[WebFrontend] TrustedProxyNetworks`. The default configuration trusts loopback proxies only:

```ini
TrustedProxyNetworks=127.0.0.1/32,::1/128
```

When a trusted proxy supplies `X-Forwarded-For`, the server parses comma-separated IP addresses from right to left and selects the first untrusted address as the client source. Invalid headers, more than 16 hops, or headers longer than 2048 bytes are ignored and the direct peer is used. Do not add public or client networks to `TrustedProxyNetworks`; only add the addresses or CIDRs of proxies that overwrite or safely append the header.

## Request Limits

The default limits are part of the `[WebFrontend]` configuration:

- Login rate limiting charges each source and email key 30 seconds per attempt, with a burst of 10.
- Registration rate limiting charges each source and account key 5 minutes per attempt, with a burst of 3.
- Request bodies are limited to 16 KiB, read in 10 seconds, and JSON is limited to depth 32.

## Accounts And Sessions

New accounts and password changes require passwords from 12 through 64 characters. Existing legacy accounts with shorter passwords can still log in; login does not apply the new-password length rule.

After a password change, account status change, or user-level change is committed, the server revokes that account's pending and active sessions and disconnects its active clients.

The JSON development database is a single-account mode that does not verify credentials. It is unsafe to expose publicly. Use the SQLite account database for any deployed server that accepts untrusted connections.
