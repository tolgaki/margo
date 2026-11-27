// The orchestrator, engine and margo-state CLI this project drives are Linux-only (private state relies on POSIX
// modes); Margo Cloud runs on Linux containers.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]
