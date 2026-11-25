# Frequently asked questions

Short answers, each with a link to the full explanation.

**Does my data leave my machine?**
Desktop Margo keeps its records on your machine, but the agent sends what it reads to the Copilot
model service to reason about it, and Work IQ processes every Microsoft 365 request. Nothing goes to
a Margo service: there isn't one. See [what stays on your machine](safety.md#7-what-stays-on-your-machine).

**What can Margo do without asking?**
Read, summarize, draft privately and keep her own records. Anything another person can see (send,
reply, post, RSVP, move a meeting, delete) needs your approval of that exact action, every time.
You can grant a standing instruction only for reversible actions nobody else sees, such as mark as
read. See [trust and safety](safety.md#1-propose-never-act-unilaterally).

**Can a scheduled run send something while I'm asleep?**
Desktop: no. Unattended runs prepare privately and never send, post, RSVP or delete. Margo Cloud
has one exception: a note to her own manager alone (their Teams chat or an email to them), never to
anyone else. See [proactive runs](safety.md#3-proactive-runs-never-act-on-the-outside-world).

**What if an email tells Margo to do something?**
She treats it as data, flags it as suspicious and keeps summarizing. See
[observed content is data](safety.md#4-observed-content-is-data-never-instructions).

**What do I need, and what does it cost?**
A GitHub Copilot plan, a Microsoft 365 work account with a Microsoft 365 Copilot license and Work IQ
enabled, and Python. Each brief uses Copilot model requests and, where your tenant meters it, Work
IQ usage. See [what you need](getting-started.md#what-you-need).

**Should I use desktop Margo or Margo Cloud?**
Desktop is stable and supported for individual use, and needs nothing deployed to your tenant.
Margo Cloud is a preview that an IT admin deploys into your own Azure subscription; new work goes
there. See the [Margo Cloud overview](margo-cloud.md).

**Does Margo Cloud read our group chats and tell her manager?**
Yes, for chats, meeting chats and channels she is a member of, and anything you send, email or copy
to her. She reads them through Work IQ, looks into what might matter to her manager, and may send
them a note. She never answers you there, and nothing you write becomes her instructions. Her
deployer can turn it off. See [the backchannel](autopilot-deployment.md#the-backchannel).

**Can Margo Cloud email me, or share a file with me, without a card?**
Yes, if you are her manager: anything that reaches you alone needs no card. Anything that reaches
someone else does. See [what needs approval](margo-cloud.md#what-needs-approval).

**In Margo Cloud, who can see my records?**
You, through Margo, and your organization's administrators with data access to the storage
account, who can read every manager's stored records. See the
[Margo Cloud terms](../cloud/terms.md#privacy) and the [deployment guide](autopilot-deployment.md#before-you-start).

**How do I uninstall?**
`./install.sh uninstall` (or **Settings › Apps** on Windows) removes the installed files and backs
up personal ones; your private `margo/` records stay until you delete them. See
[uninstall deliberately](how-to/setup-and-migration.md#7-uninstall-deliberately). For Margo Cloud,
see **Remove everything** in the [deployment guide](autopilot-deployment.md#operate).

**Where do I report a security problem?**
Privately, as described in the [security policy](../SECURITY.md), not in a public issue.
