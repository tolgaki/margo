// Private state relies on POSIX modes, and the margo-state CLI and engine this project drives are Linux-only;
// Margo Cloud runs on Linux containers.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]
