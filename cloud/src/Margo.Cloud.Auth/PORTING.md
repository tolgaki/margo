# Margo.Cloud.Auth: porting notes

The platform token check, once: the Microsoft 365 Agents SDK's `jwt_authorization_middleware` and the parts of PyJWT
it used, as `Margo.Cloud.Router` ported them. `Margo.Cloud.Router` (the public endpoint) and `Margo.Cloud.Host` (each
instance) both reference this project, so the two hops accept exactly the same tokens and a fix is made in one place.

| File | Holds |
| --- | --- |
| `AgentTokenValidator.cs` | `AgentAuthSettings`, `AuthorizationResult`, `AgentTokenValidator` |
| `CompactJws.cs` | `TokenRejectedException`, `CompactJws` |
| `EntraIssuers.cs` | The issuer allowlist and key set addresses |
| `SigningKeys.cs` | `IJwksSource`, `HttpJwksSource`, `JwksUnavailableException`, `SigningKey`, `JwksClient`, `JwksClientManager` |
| `PythonCompat.cs`, `PythonBase64.cs`, `PythonJsonBytes.cs` | The Python semantics the check depends on; the router also uses them for its own request handling |

The mapping to Python, every deliberate deviation and the tests are recorded where the port was made:
[`Margo.Cloud.Router/PORTING.md`](../Margo.Cloud.Router/PORTING.md) ("What maps to what", "The token check",
deviations 13 and 15). The tests are in `Margo.Cloud.Router.Tests` (`TokenValidationTests`, `SigningKeyCacheTests`,
`PythonCompatTests`, `ReviewParityTests`) and, through the host's pipeline, `Margo.Cloud.Host.Tests`.

## Deliberate choices

- **A project of its own, not `Margo.Cloud.Core`.** Core has no dependencies and every component references it; the
  check needs logging, HTTP and RSA, and its internals are opened to the router. Core also has its own internal Python
  helpers with one known difference (`int()`; see the Router's open questions), which this move does not merge.
- **Logging comes from the shared framework** (`Microsoft.AspNetCore.App`), not a package: both users are ASP.NET Core
  applications.
- **`Margo.Cloud.Voice/Jwt/` is not folded in.** It verifies a different token (the voice page's sign-in) and its
  copies of `CompactJws`, `SigningKeys` and `PythonBase64` have been adapted; see `Margo.Cloud.Voice/PORTING.md`.
