# Getting help

Margo is a community reference implementation maintained in spare time. There is no paid support and no response
time guarantee.

- **Setup questions and bugs:** [open an issue](https://github.com/tolgaki/margo/issues) using the templates. Include
  the output of `./install.sh status` and of the doctor
  (`python3 ~/.copilot/skills/chief-of-staff/scripts/margo_doctor.py`), and remove any real names, addresses or tenant details first.
- **Security problems:** follow [SECURITY.md](SECURITY.md). Do not open a public issue.
- **Work IQ itself** (enabling it, consent, licenses, service errors): Microsoft's Work IQ documentation and your
  Microsoft support channel. Margo cannot change how Work IQ behaves.
- **Before asking:** [Getting started](docs/getting-started.md), the [FAQ](docs/faq.md) and, for Margo Cloud, the
  deployment guide's [known limits](docs/autopilot-deployment.md#known-limits).

## What is supported

| Part | Status |
| --- | --- |
| Desktop Margo on the `main` branch | Supported: fixes and updates |
| Desktop Margo's last tagged release | Supported until the next release is tagged |
| Margo Cloud | Preview: running in a test tenant; the [maturity table](docs/margo-cloud.md#maturity) says which features are verified there |
| Margo's skills in Claude Code or Codex | Not officially supported; the Codex plugin is untested |
| Older tags | Not supported; update to `main` |
